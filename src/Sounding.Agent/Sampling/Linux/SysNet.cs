using System.Globalization;

namespace Sounding.Agent.Sampling;

// Interface counters from <sys>/class/net/<interface>/statistics, the same figures as /proc/net/dev.
public static class SysNet
{
    public static Dictionary<string, NetDevCounters> Read(string classNet)
    {
        var counters = new Dictionary<string, NetDevCounters>(StringComparer.Ordinal);
        foreach (var device in Directory.EnumerateDirectories(classNet))
        {
            var statistics = Path.Combine(device, "statistics");
            long Get(string name) =>
                long.TryParse(KernelFile.ReadAllText(Path.Combine(statistics, name)).Trim(), CultureInfo.InvariantCulture, out var value) ? value : 0;
            try
            {
                counters[Path.GetFileName(device)] = new NetDevCounters(
                    Get("rx_bytes"), Get("rx_packets"),
                    Get("tx_bytes"), Get("tx_packets"),
                    Get("rx_errors") + Get("tx_errors"),
                    Get("rx_dropped") + Get("tx_dropped"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // an interface that went away
            }
        }

        return counters;
    }
}
