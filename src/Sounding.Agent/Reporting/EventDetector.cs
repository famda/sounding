namespace Sounding.Agent.Reporting;

// Turns changes between two samples into events: OOM kills (with the process that most likely died), memory
// limit hits, bursts of CPU throttling, and processes starting and exiting.
public sealed class EventDetector
{
    private const double ThrottledFrom = 25;
    private const int CalmTicks = 10;

    private long? _oomKills, _limitHits;
    private bool _atLimit, _throttled;
    private int _calm;
    private Dictionary<(int, DateTimeOffset?), ProcessUsage>? _processes;

    public List<AgentEvent> Detect(ContainerDetail? detail, IReadOnlyList<ProcessUsage> processes, bool complete)
    {
        var events = new List<AgentEvent>();
        var current = processes.Where(p => p.Sounding != true).ToDictionary(p => (p.Pid, p.StartedAt));
        var exited = _processes is null ? [] : _processes.Where(p => !current.ContainsKey(p.Key)).Select(p => p.Value).ToList();
        var started = _processes is null ? [] : current.Where(p => !_processes.ContainsKey(p.Key)).Select(p => p.Value).ToList();

        if (detail?.MemoryEvents is { } memory)
        {
            if (_oomKills is { } kills && memory.OomKill > kills)
            {
                events.Add(exited.MaxBy(p => p.MemoryBytes) is { } victim
                    ? new AgentEvent("oom", "error", $"Out of memory: {victim.Name} (pid {victim.Pid}) was killed, using {Megabytes(victim.MemoryBytes)}")
                    : new AgentEvent("oom", "error", "Out of memory: a process was killed before it was sampled"));
            }

            var hit = _limitHits is { } hits && memory.Max > hits;
            if (hit && !_atLimit)
            {
                events.Add(new AgentEvent("memory", "warning", "Reached its memory limit"));
            }

            (_oomKills, _limitHits, _atLimit) = (memory.OomKill, memory.Max, hit);
        }

        if (detail?.Throttling is { } throttling)
        {
            if (!_throttled && throttling.ThrottledPercent >= ThrottledFrom)
            {
                events.Add(new AgentEvent("cpu", "warning", $"CPU throttled: held back in {throttling.ThrottledPercent:0}% of periods by its limit"));
                (_throttled, _calm) = (true, 0);
            }
            else if (_throttled && (throttling.ThrottledPercent < 1 ? ++_calm : _calm = 0) >= CalmTicks)
            {
                _throttled = false;
            }
        }

        // With the list cut to the busiest processes, one leaving it isn't an exit.
        if (complete)
        {
            if (started.Count > 0)
            {
                events.Add(new AgentEvent("process", "info", started.Count == 1
                    ? $"Started {started[0].Name} (pid {started[0].Pid})"
                    : $"Started {started.Count} processes: {string.Join(", ", started.Select(p => p.Name).Distinct())}"));
            }

            if (exited.Count > 0)
            {
                events.Add(new AgentEvent("process", "info", exited.Count == 1
                    ? $"{exited[0].Name} (pid {exited[0].Pid}) exited{Lived(exited[0])}"
                    : $"{exited.Count} processes exited: {string.Join(", ", exited.Select(p => p.Name).Distinct())}"));
            }
        }

        _processes = complete ? current : null;
        return events;
    }

    private static string Lived(ProcessUsage p) =>
        p.StartedAt is { } start ? $" after {Math.Max(0, (DateTimeOffset.UtcNow - start).TotalSeconds):0.#} s" : "";

    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024):0.#} MB";
}
