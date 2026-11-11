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
            var line = KernelFile.ReadAllLines(cpuInfoPath).FirstOrDefault(l => l.StartsWith("model name", StringComparison.Ordinal));
            _model = line?[(line.IndexOf(':') + 1)..].Trim();
        }

        return _model;
    }
}
