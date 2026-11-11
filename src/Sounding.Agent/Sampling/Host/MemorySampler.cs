using System.Diagnostics;
using Microsoft.Extensions.Options;

namespace Sounding.Agent.Sampling;

public sealed class MemorySampler(IOptions<AgentOptions> options)
{
    private readonly string _procPath = options.Value.ProcPath;
    private (long MajorFaults, long SwapIn, long SwapOut, long Scanned, long Stolen, long Timestamp)? _previous;

    // Filled again on each sample, with every name one string for good.
    private readonly Dictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly NamePool _names = new();

    public MemoryStats? Sample()
    {
        var memInfoPath = Path.Combine(_procPath, "meminfo");
        if (!File.Exists(memInfoPath))
        {
            return null;
        }

        // "MemTotal:       16303428 kB"
        var values = _values;
        values.Clear();
        var text = KernelFile.ReadAllText(memInfoPath).AsSpan();
        Span<Range> parts = stackalloc Range[3];
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var rest = line[(colon + 1)..];
            var count = rest.Split(parts, ' ', StringSplitOptions.RemoveEmptyEntries);
            if (count > 0 && long.TryParse(rest[parts[0]], out var value))
            {
                values[_names.Get(line[..colon])] = count > 1 && rest[parts[1]] is "kB" ? value * 1024 : value;
            }
        }

        long Get(string key) => values.GetValueOrDefault(key);

        var total = Get("MemTotal");
        var available = values.TryGetValue("MemAvailable", out var a) ? a : Get("MemFree") + Get("Buffers") + Get("Cached");
        var used = total - available;
        var swapTotal = Get("SwapTotal");

        var (majorFaults, swapIn, swapOut, scanned, reclaimed) = Rates();
        long? Optional(string key) => values.TryGetValue(key, out var v) ? v : null;
        return new MemoryStats(
            total,
            used,
            available,
            Get("Cached") + Get("Buffers"),
            swapTotal,
            swapTotal - Get("SwapFree"),
            total > 0 ? Math.Round(100.0 * used / total, 1) : 0,
            majorFaults,
            swapIn,
            swapOut,
            Optional("Dirty"),
            Optional("Writeback"),
            Optional("Committed_AS"),
            Optional("CommitLimit"),
            Optional("SReclaimable"),
            Optional("SUnreclaim"),
            scanned,
            reclaimed);
    }

    private (double?, double?, double?, double?, double?) Rates()
    {
        var counters = _counters;
        counters.Clear();
        if (KernelFile.TryReadAllText(Path.Combine(_procPath, "vmstat")) is not { } vmstat)
        {
            return (null, null, null, null, null);
        }

        var text = vmstat.AsSpan();
        foreach (var range in text.Split('\n'))
        {
            // "name value", and nothing else on the line.
            var line = text[range];
            var space = line.IndexOf(' ');
            if (space >= 0 && !line[(space + 1)..].Contains(' ') && long.TryParse(line[(space + 1)..], out var number))
            {
                counters[_names.Get(line[..space])] = number;
            }
        }

        long Get(string name) => counters.GetValueOrDefault(name);
        (long MajorFaults, long SwapIn, long SwapOut, long Scanned, long Stolen, long Timestamp) now = (Get("pgmajfault"), Get("pswpin"), Get("pswpout"),
            Get("pgscan_kswapd") + Get("pgscan_direct"), Get("pgsteal_kswapd") + Get("pgsteal_direct"), Stopwatch.GetTimestamp());
        var previous = _previous;
        _previous = now;
        if (previous is not { } before)
        {
            return (null, null, null, null, null);
        }

        var elapsed = Stopwatch.GetElapsedTime(before.Timestamp, now.Timestamp).TotalSeconds;
        var page = Environment.SystemPageSize;
        double Rate(long a, long b, long scale = 1) => elapsed > 0 ? Math.Round(Math.Max(0, a - b) * scale / elapsed, 1) : 0;
        return (Rate(now.MajorFaults, before.MajorFaults), Rate(now.SwapIn, before.SwapIn, page), Rate(now.SwapOut, before.SwapOut, page),
            Rate(now.Scanned, before.Scanned, page), Rate(now.Stolen, before.Stolen, page));
    }
}
