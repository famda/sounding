using System.Globalization;

namespace Sounding.Agent.Sampling;

public sealed class TemperatureSampler
{
    private const string HwmonPath = "/sys/class/hwmon";
    private const string ThermalPath = "/sys/class/thermal";

    public IReadOnlyList<TemperatureSensor>? Sample()
    {
        if (!Directory.Exists("/sys/class"))
        {
            return null;
        }

        var sensors = new List<TemperatureSensor>();
        var sources = new HashSet<string>(StringComparer.Ordinal);
        foreach (var device in Entries(HwmonPath).Order(StringComparer.Ordinal))
        {
            var source = Text(Path.Combine(device, "name")) ?? Path.GetFileName(device);
            sources.Add(source);
            foreach (var input in Entries(device, "temp*_input").Order(StringComparer.Ordinal))
            {
                var prefix = input[..^"_input".Length];
                if (Millidegrees(input) is not { } celsius)
                {
                    continue;
                }

                var label = Text(prefix + "_label") ?? Path.GetFileName(prefix);
                sensors.Add(new TemperatureSensor(source, label, celsius, Millidegrees(prefix + "_max"), Millidegrees(prefix + "_crit")));
            }
        }

        foreach (var zone in Entries(ThermalPath, "thermal_zone*").Order(StringComparer.Ordinal))
        {
            var type = Text(Path.Combine(zone, "type")) ?? Path.GetFileName(zone);
            if (!sources.Contains(type) && Millidegrees(Path.Combine(zone, "temp")) is { } celsius)
            {
                sensors.Add(new TemperatureSensor(type, Path.GetFileName(zone), celsius, null, CriticalTrip(zone)));
            }
        }

        return sensors;
    }

    private static double? CriticalTrip(string zone)
    {
        foreach (var type in Entries(zone, "trip_point_*_type"))
        {
            if (Text(type) == "critical")
            {
                return Millidegrees(type[..^"_type".Length] + "_temp");
            }
        }

        return null;
    }

    private static double? Millidegrees(string path) =>
        long.TryParse(Text(path), CultureInfo.InvariantCulture, out var value) && value > -50_000 && value < 200_000
            ? Math.Round(value / 1000.0, 1)
            : null;

    private static string? Text(string path)
    {
        try
        {
            return File.Exists(path) ? KernelFile.ReadAllText(path).Trim() : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // some sensors fail reads while the device sleeps
        }
    }

    private static IEnumerable<string> Entries(string directory, string pattern = "*")
    {
        try
        {
            return Directory.Exists(directory) ? Directory.GetFileSystemEntries(directory, pattern) : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
