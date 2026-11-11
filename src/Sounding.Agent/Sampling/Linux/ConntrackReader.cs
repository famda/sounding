using System.Globalization;

namespace Sounding.Agent.Sampling;

public static class ConntrackReader
{
    public static Conntrack? Read(string statPath, string procPath)
    {
        try
        {
            if (KernelFile.ReadAllLines(statPath).Skip(1).FirstOrDefault()?.Split(' ', StringSplitOptions.RemoveEmptyEntries) is not [var entries, ..]
                || !long.TryParse(entries, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var count))
            {
                return null;
            }

            var maxPath = Path.Combine(procPath, "sys/net/netfilter/nf_conntrack_max");
            long? max = File.Exists(maxPath) && long.TryParse(KernelFile.ReadAllText(maxPath).Trim(), CultureInfo.InvariantCulture, out var m) ? m : null;
            return new Conntrack(count, max);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
