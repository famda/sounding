namespace Sounding.Agent.Sampling;

public readonly record struct NetDevCounters(
    long ReceiveBytes,
    long ReceivePackets,
    long TransmitBytes,
    long TransmitPackets,
    long Errors,
    long Drops);

public static class NetDev
{
    // Only read from the sampling loop.
    private static readonly NamePool Names = new();

    public static Dictionary<string, NetDevCounters> Read(string path)
    {
        var counters = new Dictionary<string, NetDevCounters>(StringComparer.Ordinal);
        var text = KernelFile.ReadAllText(path).AsSpan();
        Span<Range> f = stackalloc Range[13];
        foreach (var range in text.Split('\n'))
        {
            var line = text[range];
            var colon = line.IndexOf(':');
            if (colon < 0)
            {
                continue; // the two header lines
            }

            var values = line[(colon + 1)..];
            if (values.Split(f, ' ', StringSplitOptions.RemoveEmptyEntries) < 12)
            {
                continue;
            }

            counters[Names.Get(line[..colon].Trim())] = new NetDevCounters(
                long.Parse(values[f[0]]), long.Parse(values[f[1]]),
                long.Parse(values[f[8]]), long.Parse(values[f[9]]),
                long.Parse(values[f[2]]) + long.Parse(values[f[10]]),
                long.Parse(values[f[3]]) + long.Parse(values[f[11]]));
        }

        return counters;
    }
}
