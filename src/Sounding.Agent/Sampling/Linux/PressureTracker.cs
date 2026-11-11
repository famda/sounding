using System.Diagnostics;
using System.Globalization;

namespace Sounding.Agent.Sampling;

public sealed class PressureTracker(string cpuPath, string memoryPath, string ioPath, bool cpuFullMeaningful)
{
    private long[]? _previous;
    private long _previousTimestamp;

    public Pressure? Sample()
    {
        var cpu = ReadTotals(cpuPath);
        var memory = ReadTotals(memoryPath);
        var io = ReadTotals(ioPath);
        if (cpu is null || memory is null || io is null)
        {
            return null;
        }

        long[] totals = [cpu.Value.Some, cpu.Value.Full, memory.Value.Some, memory.Value.Full, io.Value.Some, io.Value.Full];
        var timestamp = Stopwatch.GetTimestamp();
        var previous = _previous;
        var elapsedUs = Stopwatch.GetElapsedTime(_previousTimestamp, timestamp).TotalMicroseconds;
        _previous = totals;
        _previousTimestamp = timestamp;
        if (previous is null || elapsedUs <= 0)
        {
            return null;
        }

        double Rate(int i) => Math.Round(Math.Clamp(100.0 * (totals[i] - previous[i]) / elapsedUs, 0, 100), 2);
        return new Pressure(Rate(0), cpuFullMeaningful ? Rate(1) : null, Rate(2), Rate(3), Rate(4), Rate(5));
    }

    private static (long Some, long Full)? ReadTotals(string path)
    {
        if (KernelFile.TryReadAllLines(path) is not { } lines)
        {
            return null;
        }

        long? some = null, full = null;
        foreach (var line in lines)
        {
            var total = line.Split(' ').FirstOrDefault(f => f.StartsWith("total=", StringComparison.Ordinal));
            if (total is null || !long.TryParse(total.AsSpan(6), CultureInfo.InvariantCulture, out var value))
            {
                continue;
            }

            if (line.StartsWith("some", StringComparison.Ordinal))
            {
                some = value;
            }
            else if (line.StartsWith("full", StringComparison.Ordinal))
            {
                full = value;
            }
        }

        return some is null ? null : (some.Value, full ?? 0);
    }
}
