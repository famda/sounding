using System.Text.RegularExpressions;

namespace Sounding.Agent.Sampling;

public sealed record Mount(string MountPoint, string FileSystemType, string Source, string Root = "/", bool ReadOnly = false);

public static partial class MountInfo
{
    public static List<Mount> Read(string path)
    {
        var mounts = new List<Mount>();
        foreach (var line in KernelFile.ReadAllLines(path))
        {
            var fields = line.Split(' ');
            var separator = Array.IndexOf(fields, "-", 6);
            if (separator < 0 || fields.Length < separator + 3)
            {
                continue;
            }

            mounts.Add(new Mount(Unescape(fields[4]), fields[separator + 1], Unescape(fields[separator + 2]), Unescape(fields[3]),
                fields[5].Split(',').Contains("ro")));
        }

        return mounts;
    }

    private static string Unescape(string value) =>
        OctalEscape().Replace(value, m => ((char)Convert.ToInt32(m.Groups[1].Value, 8)).ToString());

    [GeneratedRegex(@"\\([0-7]{3})")]
    private static partial Regex OctalEscape();
}
