using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;

namespace Sounding.Agent.Sampling;

// The command's output, as counted by the launcher (SOUNDING_LOG_FD): lines, errors, warnings and bytes each
// second, and its last lines. Not there when the container has a terminal or runs the agent alone.
public sealed class LogSampler
{
    private const int Kept = 20;

    private readonly Lock _lock = new();
    private readonly Queue<LogLine> _recent = new();
    private long _lines, _errors, _warnings, _bytes;
    private long _previousTimestamp = Stopwatch.GetTimestamp();

    public bool Available { get; }

    public LogSampler(ILogger<LogSampler> logger)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SOUNDING_LOG_FD"), CultureInfo.InvariantCulture, out var fd) || fd < 0)
        {
            return;
        }

        Available = true;
        new Thread(() =>
        {
            try
            {
                using var reader = new StreamReader(new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Read, 1));
                while (reader.ReadLine() is { } line)
                {
                    Read(line);
                }
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
            {
                logger.LogDebug("Log counts unreadable: {Message}", ex.Message);
            }
        }) { IsBackground = true, Name = "logs" }.Start();
    }

    private void Read(string line)
    {
        lock (_lock)
        {
            if (line.Split(' ') is ["S", var lines, var errors, var warnings, var bytes])
            {
                _lines += long.Parse(lines, CultureInfo.InvariantCulture);
                _errors += long.Parse(errors, CultureInfo.InvariantCulture);
                _warnings += long.Parse(warnings, CultureInfo.InvariantCulture);
                _bytes += long.Parse(bytes, CultureInfo.InvariantCulture);
            }
            // Blank lines are counted, not kept.
            else if (line.Length > 6 && line.StartsWith("L ", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(line[6..]))
            {
                var level = line[4] switch { 'e' => "error", 'w' => "warning", _ => "info" };
                _recent.Enqueue(new LogLine(DateTimeOffset.UtcNow, line[2] == 'e' ? "stderr" : "stdout", level, line[6..]));
                while (_recent.Count > Kept)
                {
                    _recent.Dequeue();
                }
            }
        }
    }

    public (LogStats? Stats, IReadOnlyList<LogLine>? Recent) Sample()
    {
        if (!Available)
        {
            return (null, null);
        }

        lock (_lock)
        {
            var now = Stopwatch.GetTimestamp();
            var elapsed = Math.Max(0.001, Stopwatch.GetElapsedTime(_previousTimestamp, now).TotalSeconds);
            _previousTimestamp = now;
            var stats = new LogStats(Math.Round(_lines / elapsed, 2), Math.Round(_errors / elapsed, 2), Math.Round(_warnings / elapsed, 2), Math.Round(_bytes / elapsed));
            (_lines, _errors, _warnings, _bytes) = (0, 0, 0, 0);
            var recent = _recent.Count > 0 ? _recent.ToList() : null;
            return (stats, recent);
        }
    }
}
