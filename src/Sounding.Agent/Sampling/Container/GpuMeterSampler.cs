using System.Globalization;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class GpuMeterSampler(IOptions<AgentOptions> options, ILogger<GpuMeterSampler> logger)
{
    private readonly string _directory = options.Value.GpuMeterPath;
    private readonly string _procPath = options.Value.ProcPath;

    private Dictionary<(string Pid, string Bus), (Counters Counters, Rates Rates)> _previous = [];
    private long _peakBytes;

    private Dictionary<(string Pid, string Name), (ulong Timestamp, ulong Launches, ulong Ns)> _previousKernels = [];
    private Dictionary<string, KernelRate> _kernelRates = [];

    private Dictionary<string, ulong> _failures = [];
    private long _failuresTotal;
    private bool _failuresReported;

    private readonly record struct KernelRate(double BusyFraction, double LaunchesPerSecond, double Ns, double Count);

    private readonly record struct Counters(ulong Timestamp, long Allocated, ulong BusyNs, ulong ToDevice, ulong ToHost, ulong Launches, ulong KernelNs);

    private readonly record struct Rates(double BusyFraction, double ToDevice, double ToHost, double Launches, double? AverageKernelUs);

    public GpuMeterSample Sample()
    {
        var current = new Dictionary<(string Pid, string Bus), (Counters, Rates)>();
        var devices = new Dictionary<string, (double Busy, long Memory, double ToDevice, double ToHost, double Launches, double KernelUs, double KernelUsWeight)>();
        var processes = new Dictionary<int, ProcessGpu>();
        var visible = 1;
        var kernels = new Dictionary<(string Pid, string Name), (ulong Timestamp, ulong Launches, ulong Ns)>();
        var kernelRates = new Dictionary<string, KernelRate>();
        var failures = new Dictionary<string, ulong>();
        var kernelsChanged = false;

        foreach (var file in Directory.Exists(_directory) ? Directory.EnumerateFiles(_directory) : [])
        {
            var pid = Path.GetFileName(file);
            if (!int.TryParse(pid, CultureInfo.InvariantCulture, out var pidNumber))
            {
                continue; // a ".tmp" mid-write
            }

            if (!Directory.Exists(Path.Combine(_procPath, pid)))
            {
                TryDelete(file);
                continue;
            }

            string[] lines;
            try
            {
                lines = KernelFile.ReadAllLines(file);
            }
            catch (IOException)
            {
                continue;
            }

            if (lines.Length == 0 || Fields(lines[0]) is not ["v3" or "v4", var visibleText, var peakText, var timestampText, .. var extra]
                || !int.TryParse(visibleText, CultureInfo.InvariantCulture, out visible)
                || !long.TryParse(peakText, CultureInfo.InvariantCulture, out var processPeak)
                || !ulong.TryParse(timestampText, CultureInfo.InvariantCulture, out var timestamp))
            {
                continue;
            }

            if (extra is [var failedText, ..] && ulong.TryParse(failedText, CultureInfo.InvariantCulture, out var failed))
            {
                failures[pid] = failed;
                _failuresTotal += (long)(failed - Math.Min(failed, _failures.GetValueOrDefault(pid)));
                _failuresReported = true;
            }

            double processBusy = 0;
            long processMemory = 0;
            foreach (var line in lines.Skip(1))
            {
                if (line.StartsWith("k ", StringComparison.Ordinal))
                {
                    var parts = line.Split(' ', 4);
                    if (parts.Length == 4 && ulong.TryParse(parts[1], CultureInfo.InvariantCulture, out var launches)
                        && ulong.TryParse(parts[2], CultureInfo.InvariantCulture, out var ns))
                    {
                        var name = parts[3].Trim();
                        kernels[(pid, name)] = (timestamp, launches, ns);
                        if (_previousKernels.TryGetValue((pid, name), out var before) && timestamp > before.Timestamp && launches >= before.Launches)
                        {
                            kernelsChanged = true;
                            double window = timestamp - before.Timestamp;
                            var readable = KernelName.Readable(name);
                            var sum = kernelRates.GetValueOrDefault(readable);
                            kernelRates[readable] = new KernelRate(
                                sum.BusyFraction + (ns - before.Ns) / window,
                                sum.LaunchesPerSecond + (launches - before.Launches) / (window / 1e9),
                                sum.Ns + (ns - before.Ns),
                                sum.Count + (launches - before.Launches));
                        }
                    }

                    continue;
                }

                if (Fields(line) is not [var busText, .. var numbers] || numbers.Length < 6
                    || !long.TryParse(numbers[0], CultureInfo.InvariantCulture, out var allocated))
                {
                    continue;
                }

                var values = numbers[1..6].Select(n => ulong.TryParse(n, CultureInfo.InvariantCulture, out var v) ? v : 0).ToArray();
                var bus = PciBus.Normalize(busText);
                var now = new Counters(timestamp, allocated, values[0], values[1], values[2], values[3], values[4]);
                var rates = RatesSince(_previous.GetValueOrDefault((pid, bus)), now);
                current[(pid, bus)] = (now, rates);

                var device = devices.GetValueOrDefault(bus);
                devices[bus] = (
                    device.Busy + rates.BusyFraction,
                    device.Memory + allocated,
                    device.ToDevice + rates.ToDevice,
                    device.ToHost + rates.ToHost,
                    device.Launches + rates.Launches,
                    device.KernelUs + (rates.AverageKernelUs ?? 0) * rates.Launches,
                    device.KernelUsWeight + (rates.AverageKernelUs is null ? 0 : rates.Launches));
                processBusy += rates.BusyFraction;
                processMemory += allocated;
            }

            processes[pidNumber] = new ProcessGpu(Percent(processBusy / Math.Max(1, visible)), processMemory, processPeak);
        }

        _previous = current;
        foreach (var (key, now) in kernels)
        {
            _previousKernels[key] = now;
        }

        _previousKernels = _previousKernels.Where(k => kernels.ContainsKey(k.Key)).ToDictionary();
        if (kernelsChanged || kernels.Count == 0)
        {
            _kernelRates = kernelRates;
        }

        _failures = failures;

        var shares = devices
            .OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => new GpuShare(
                d.Key,
                Percent(d.Value.Busy),
                d.Value.Memory,
                Math.Round(d.Value.ToDevice),
                Math.Round(d.Value.ToHost),
                Math.Round(d.Value.Launches, 1),
                d.Value.KernelUsWeight > 0 ? Math.Round(d.Value.KernelUs / d.Value.KernelUsWeight, 1) : null))
            .ToList();

        var memory = shares.Sum(s => s.MemoryBytes);
        _peakBytes = Math.Max(_peakBytes, memory);
        var topKernels = _kernelRates
            .Where(k => k.Value.Count > 0)
            .OrderByDescending(k => k.Value.BusyFraction)
            .Take(20)
            .Select(k => new GpuKernel(
                k.Key,
                Math.Round(100 * k.Value.BusyFraction, 2),
                Math.Round(k.Value.LaunchesPerSecond, 1),
                Math.Round(k.Value.Ns / k.Value.Count / 1000, 1)))
            .ToList();

        return new GpuMeterSample(
            Math.Round(Math.Min(100, shares.Sum(s => s.Percent) / Math.Max(1, visible)), 1),
            memory,
            shares,
            processes,
            _peakBytes,
            topKernels,
            _failuresReported ? _failuresTotal : null);
    }

    // Rates over the GPU clock between two reports, so late delivery of the trace records doesn't skew them.
    private static Rates RatesSince((Counters Counters, Rates Rates) previous, Counters now)
    {
        var (before, rates) = previous;
        if (before.Timestamp == 0 || now.Timestamp < before.Timestamp || now.BusyNs < before.BusyNs)
        {
            return default;
        }

        if (now.Timestamp == before.Timestamp)
        {
            return rates;
        }

        double window = now.Timestamp - before.Timestamp;
        var seconds = window / 1e9;
        var launches = now.Launches - before.Launches;
        return new Rates(
            Math.Min(1, (now.BusyNs - before.BusyNs) / window),
            (now.ToDevice - before.ToDevice) / seconds,
            (now.ToHost - before.ToHost) / seconds,
            launches / seconds,
            launches > 0 ? (now.KernelNs - before.KernelNs) / 1000.0 / launches : null);
    }

    private static double Percent(double fraction) => Math.Round(Math.Min(100, 100 * fraction), 1);

    private static string[] Fields(string line) => line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogDebug("Could not remove stale gpu-meter file {File}: {Message}", file, ex.Message);
        }
    }
}

public sealed record GpuMeterSample(
    double? Percent,
    long? MemoryBytes,
    IReadOnlyList<GpuShare> Devices,
    IReadOnlyDictionary<int, ProcessGpu> Processes,
    long PeakMemoryBytes,
    IReadOnlyList<GpuKernel> Kernels,
    long? AllocationFailures);

public static class KernelName
{
    public static string Readable(string mangled)
    {
        if (!mangled.StartsWith("_Z", StringComparison.Ordinal))
        {
            return mangled;
        }

        var i = 2;
        var nested = i < mangled.Length && mangled[i] == 'N';
        if (nested)
        {
            i++;
            while (i < mangled.Length && mangled[i] is 'K' or 'V' or 'r')
            {
                i++; // const/volatile/restrict of a member function
            }
        }

        var parts = new List<string>();
        while (i < mangled.Length && char.IsAsciiDigit(mangled[i]))
        {
            var start = i;
            while (i < mangled.Length && char.IsAsciiDigit(mangled[i]))
            {
                i++;
            }

            var length = int.Parse(mangled.AsSpan(start, i - start), CultureInfo.InvariantCulture);
            if (i + length > mangled.Length)
            {
                break;
            }

            parts.Add(mangled.Substring(i, length));
            i += length;
            if (!nested)
            {
                break;
            }
        }

        if (parts.Count == 0)
        {
            return mangled;
        }

        var name = string.Join("::", parts.Select(p => p.StartsWith("_GLOBAL__N", StringComparison.Ordinal) ? "(anonymous namespace)" : p));
        return i < mangled.Length && mangled[i] == 'I' ? name + "<…>" : name;
    }
}

public sealed record ProcessGpu(double Percent, long MemoryBytes, long PeakMemoryBytes);

public static class PciBus
{
    public static string Normalize(string busId)
    {
        var colon = busId.IndexOf(':');
        return colon > 0 && int.TryParse(busId.AsSpan(0, colon), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var domain)
            ? $"{domain:x4}{busId[colon..].ToLowerInvariant()}"
            : busId.ToLowerInvariant();
    }
}
