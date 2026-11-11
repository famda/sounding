using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class GpuSampler(IOptions<AgentOptions> options, ILogger<GpuSampler> logger)
{
    private static readonly string[] Fields =
    [
        "index", "name", "driver_version", "utilization.gpu", "utilization.memory",
        "memory.total", "memory.used", "temperature.gpu", "power.draw", "power.limit",
        "fan.speed", "clocks.sm", "clocks.max.sm",
    ];

    private static readonly string[][] Extras =
    [
        ["pci.bus_id", "pstate", "pcie.link.gen.current", "pcie.link.width.current", "clocks_event_reasons.active",
            "utilization.encoder", "utilization.decoder", "clocks.mem", "clocks.max.mem", "temperature.gpu.tlimit",
            "ecc.errors.corrected.volatile.total", "ecc.errors.uncorrected.volatile.total"],
        ["pci.bus_id", "pstate", "pcie.link.gen.current", "pcie.link.width.current", "clocks_event_reasons.active",
            "utilization.encoder", "utilization.decoder", "clocks.mem", "clocks.max.mem", "temperature.gpu.tlimit"],
        ["pci.bus_id", "pstate", "pcie.link.gen.current", "pcie.link.width.current", "clocks_event_reasons.active",
            "utilization.encoder", "utilization.decoder", "clocks.mem", "clocks.max.mem"],
        ["pci.bus_id", "pstate", "pcie.link.gen.current", "pcie.link.width.current", "clocks_throttle_reasons.active",
            "utilization.encoder", "utilization.decoder", "clocks.mem", "clocks.max.mem"],
        ["pci.bus_id", "pstate", "pcie.link.gen.current", "pcie.link.width.current", "clocks_throttle_reasons.active"],
        [],
    ];

    private static readonly (ulong Bit, string Reason)[] ClockReasons =
    [
        (0x1, "GPU idle"), (0x2, "Application clocks setting"), (0x4, "Software power cap"), (0x8, "Hardware slowdown"),
        (0x10, "Sync boost"), (0x20, "Software thermal slowdown"), (0x40, "Hardware thermal slowdown"),
        (0x80, "Hardware power brake"), (0x100, "Display clock setting"),
    ];

    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(30);

    private readonly string _nvidiaSmi = options.Value.NvidiaSmiPath;
    private readonly int _intervalMs = Math.Max(250, options.Value.IntervalMilliseconds);
    private readonly Lock _lock = new();
    private readonly SortedDictionary<int, GpuDevice> _devices = [];
    private Process? _process;
    private DateTime _retryAt = DateTime.MinValue;
    private string? _error;
    private int _extras;
    private readonly XidWatcher _xid = new(logger);

    public IReadOnlyList<AgentEvent> TakeEvents() => _xid.TakeEvents();

    // nvidia-smi is there when the container has GPU access (the NVIDIA runtime adds it to the image).
    public bool HasGpu => _nvidiaSmi.Contains('/')
        ? File.Exists(_nvidiaSmi)
        : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Any(directory => File.Exists(Path.Combine(directory, _nvidiaSmi)));

    // One nvidia-smi in loop mode (-lms) prints every GPU each interval: far cheaper than starting it every tick.
    public GpuStats Sample()
    {
        EnsureRunning();
        _xid.Start();
        lock (_lock)
        {
            return _devices.Count > 0
                ? new GpuStats(true, null, _devices.Values.ToList())
                : new GpuStats(false, _error ?? "Not available", []);
        }
    }

    public void Stop()
    {
        _xid.Stop();
        lock (_lock)
        {
            _devices.Clear();
        }

        try
        {
            _process?.Kill();
        }
        catch (InvalidOperationException)
        {
            // already exited
        }

        _process?.Dispose();
        _process = null;
    }

    private void EnsureRunning()
    {
        if (_process is { HasExited: false } || DateTime.UtcNow < _retryAt)
        {
            return;
        }

        Stop();
        _retryAt = DateTime.UtcNow + Retry;
        try
        {
            var process = Process.Start(new ProcessStartInfo(_nvidiaSmi)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                ArgumentList =
                {
                    $"--query-gpu={string.Join(',', Fields.Concat(Extras[_extras]))}", "--format=csv,noheader,nounits",
                    $"--loop-ms={_intervalMs}",
                },
            }) ?? throw new InvalidOperationException("Could not start nvidia-smi");
            _process = process;
            new Thread(() => Read(process)) { IsBackground = true, Name = "nvidia-smi" }.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            _error = "nvidia-smi not found";
            logger.LogWarning("GPU metrics unavailable: {Message}", ex.Message);
        }
    }

    private void Read(Process process)
    {
        try
        {
            while (process.StandardOutput.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var device = ParseDevice(line);
                lock (_lock)
                {
                    _devices[device.Index] = device;
                    _error = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or FormatException or IndexOutOfRangeException or InvalidOperationException)
        {
            logger.LogDebug("nvidia-smi output unreadable: {Message}", ex.Message);
        }

        var error = process.StandardError.ReadToEnd().Trim() + process.StandardOutput.ReadToEnd().Trim();
        lock (_lock)
        {
            _devices.Clear();
            // Older drivers reject newer fields: retry at once with fewer.
            if (error.Contains("not a valid field", StringComparison.Ordinal) && _extras < Extras.Length - 1)
            {
                _extras++;
                _retryAt = DateTime.MinValue;
            }
            else
            {
                _error = error.Length > 0 ? error : "nvidia-smi stopped";
            }
        }
    }

    private static GpuDevice ParseDevice(string line)
    {
        var v = line.Split(',', StringSplitOptions.TrimEntries);
        const long MiB = 1024 * 1024;
        var memoryTotal = Number(v[5]);
        var memoryUsed = Number(v[6]);

        return new GpuDevice(
            Index: int.Parse(v[0], CultureInfo.InvariantCulture),
            Name: v[1],
            DriverVersion: Text(v[2]),
            UtilizationPercent: Number(v[3]),
            MemoryUtilizationPercent: Number(v[4]),
            MemoryTotalBytes: memoryTotal is { } t ? (long)(t * MiB) : null,
            MemoryUsedBytes: memoryUsed is { } u ? (long)(u * MiB) : null,
            MemoryUsagePercent: memoryTotal > 0 && memoryUsed is { } used ? Math.Round(100 * used / memoryTotal.Value, 1) : null,
            TemperatureCelsius: Number(v[7]),
            PowerDrawWatts: Number(v[8]),
            PowerLimitWatts: Number(v[9]),
            FanPercent: Number(v[10]),
            ClockSmMhz: Number(v[11]),
            ClockSmMaxMhz: Number(v[12]),
            PciBusId: v.Length > 13 && Text(v[13]) is { } bus ? PciBus.Normalize(bus) : null,
            PerformanceState: v.Length > 14 ? Text(v[14]) : null,
            PcieGeneration: v.Length > 15 && Number(v[15]) is { } gen ? (int)gen : null,
            PcieWidth: v.Length > 16 && Number(v[16]) is { } width ? (int)width : null,
            ClockEventReasons: v.Length > 17 ? Reasons(v[17]) : null,
            EncoderPercent: v.Length > 18 ? Number(v[18]) : null,
            DecoderPercent: v.Length > 19 ? Number(v[19]) : null,
            ClockMemoryMhz: v.Length > 20 ? Number(v[20]) : null,
            ClockMemoryMaxMhz: v.Length > 21 ? Number(v[21]) : null,
            TemperatureHeadroomCelsius: v.Length > 22 ? Number(v[22]) : null,
            EccCorrected: v.Length > 23 && Number(v[23]) is { } corrected ? (long)corrected : null,
            EccUncorrected: v.Length > 24 && Number(v[24]) is { } uncorrected ? (long)uncorrected : null);
    }

    private static IReadOnlyList<string>? Reasons(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        && ulong.TryParse(value.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var bits)
            ? ClockReasons.Where(r => (bits & r.Bit) != 0).Select(r => r.Reason).ToList()
            : null;

    private static string? Text(string value) => value.StartsWith('[') || value == "N/A" ? null : value;

    private static double? Number(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : null;
}
