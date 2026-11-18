using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class CpuSampler(IOptions<AgentOptions> options)
{
    private readonly string _procPath = options.Value.ProcPath;
    private Dictionary<string, (long Idle, long Total)> _previous = [];
    private long[]? _previousSplit;
    private (long Switches, long Started, long Timestamp)? _previousActivity;
    private string? _model;
    private bool _modelRead;
    private readonly NamePool _names = new();

    public CpuStats? Sample()
    {
        var statPath = Path.Combine(_procPath, "stat");
        if (!File.Exists(statPath))
        {
            return null;
        }

        var current = new Dictionary<string, (long Idle, long Total)>();
        long[]? split = null;
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        var text = KernelFile.ReadAllText(statPath).AsSpan();
        Span<Range> parts = stackalloc Range[11];
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            if (!line.StartsWith("cpu", StringComparison.Ordinal))
            {
                // "name value" lines only: the interrupt lines have thousands of fields.
                var space = line.IndexOf(' ');
                if (space >= 0 && !line[(space + 1)..].Contains(' ') && long.TryParse(line[(space + 1)..], out var number))
                {
                    counters[_names.Get(line[..space])] = number;
                }

                continue;
            }

            // "cpu3 user nice system idle iowait irq softirq steal guest guest_nice"
            var count = line.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
            long total = 0;
            for (var i = 1; i < Math.Min(count, 9); i++)
            {
                total += long.Parse(line[parts[i]]);
            }

            var idle = long.Parse(line[parts[4]]) + (count > 5 ? long.Parse(line[parts[5]]) : 0);
            var name = line[parts[0]];
            current[_names.Get(name)] = (idle, total);
            if (name is "cpu" && count >= 9)
            {
                split = new long[8];
                for (var i = 0; i < split.Length; i++)
                {
                    split[i] = long.Parse(line[parts[i + 1]]);
                }
            }
        }

        var cores = current.Keys
            .Where(key => key != "cpu")
            .OrderBy(key => int.Parse(key.AsSpan(3)))
            .Select(key => Usage(key, current))
            .ToArray();

        var stats = new CpuStats(ReadModel(), cores.Length, Usage("cpu", current), cores, Split(split), Activity(counters));
        _previous = current;
        _previousSplit = split;
        return stats;
    }

    private CpuActivity? Activity(Dictionary<string, long> counters)
    {
        if (!counters.TryGetValue("procs_running", out var running) || !counters.TryGetValue("procs_blocked", out var blocked))
        {
            return null;
        }

        (long Switches, long Started, long Timestamp) now = (counters.GetValueOrDefault("ctxt"), counters.GetValueOrDefault("processes"), Stopwatch.GetTimestamp());
        var previous = _previousActivity;
        _previousActivity = now;
        var elapsed = previous is { } p ? Stopwatch.GetElapsedTime(p.Timestamp, now.Timestamp).TotalSeconds : 0;
        double? Rate(long a, long? b) => elapsed > 0 && b is { } before ? Math.Round(Math.Max(0, a - before) / elapsed, 1) : null;
        return new CpuActivity((int)running, (int)blocked, Rate(now.Switches, previous?.Switches), Rate(now.Started, previous?.Started));
    }

    private CpuTimeSplit? Split(long[]? now)
    {
        if (now is null || _previousSplit is not { } before)
        {
            return null;
        }

        var delta = now.Select((v, i) => Math.Max(0, v - before[i])).ToArray();
        double total = delta.Sum();
        if (total <= 0)
        {
            return null;
        }

        double Pct(int i) => Math.Round(100 * delta[i] / total, 2);
        return new CpuTimeSplit(Pct(0), Pct(1), Pct(2), Pct(4), Pct(5), Pct(6), Pct(7));
    }

    private double Usage(string key, Dictionary<string, (long Idle, long Total)> current)
    {
        if (!_previous.TryGetValue(key, out var previous) || !current.TryGetValue(key, out var now))
        {
            return 0;
        }

        var totalDelta = now.Total - previous.Total;
        if (totalDelta <= 0)
        {
            return 0;
        }

        var busy = 1 - (double)(now.Idle - previous.Idle) / totalDelta;
        return Math.Round(Math.Clamp(busy * 100, 0, 100), 1);
    }

    private string? ReadModel()
    {
        if (_modelRead)
        {
            return _model;
        }

        _modelRead = true;
        var cpuInfoPath = Path.Combine(_procPath, "cpuinfo");
        if (File.Exists(cpuInfoPath))
        {
            _model = ParseModel(KernelFile.ReadAllLines(cpuInfoPath));
        }

        return _model;
    }

    // arm64 kernels usually have no "model name", only the implementer and part numbers, named here as lscpu does.
    public static string? ParseModel(string[] cpuInfo)
    {
        string? implementer = null, part = null;
        foreach (var line in cpuInfo)
        {
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var key = line.AsSpan(0, colon).Trim();
            if (key.SequenceEqual("model name"))
            {
                return line[(colon + 1)..].Trim();
            }

            if (implementer is null && key.SequenceEqual("CPU implementer"))
            {
                implementer = line[(colon + 1)..].Trim();
            }
            else if (part is null && key.SequenceEqual("CPU part"))
            {
                part = line[(colon + 1)..].Trim();
            }
        }

        var vendor = implementer switch
        {
            "0x41" => "ARM",
            "0x42" => "Broadcom",
            "0x43" => "Cavium",
            "0x48" => "HiSilicon",
            "0x4e" => "NVIDIA",
            "0x51" => "Qualcomm",
            "0x61" => "Apple",
            "0xc0" => "Ampere",
            _ => null,
        };
        if (vendor is null)
        {
            return null;
        }

        var core = (implementer, part) switch
        {
            ("0x41", "0xd03") => "Cortex-A53",
            ("0x41", "0xd05") => "Cortex-A55",
            ("0x41", "0xd08") => "Cortex-A72",
            ("0x41", "0xd0b") => "Cortex-A76",
            ("0x41", "0xd0c") => "Neoverse-N1",
            ("0x41", "0xd40") => "Neoverse-V1",
            ("0x41", "0xd49") => "Neoverse-N2",
            ("0x41", "0xd4f") => "Neoverse-V2",
            ("0x41", "0xd84") => "Neoverse-V3",
            ("0x41", "0xd8e") => "Neoverse-N3",
            ("0xc0", "0xac3") => "Ampere-1",
            ("0xc0", "0xac4") => "Ampere-1a",
            _ => null,
        };
        return core is null ? vendor : $"{vendor} {core}";
    }
}
