using Sounding.Dashboard.Application.Events;
using Sounding.Dashboard.Application.History;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// The warnings, errors and lifecycle events of the event window: containers that keep restarting, and each other kind
// of trouble once per container. Events come newest first.
internal static class EventRules
{
    private const int CrashLoopStarts = 3;
    private const int StrongThrottlePercent = 50;

    // Bursts of work held back in a quarter of their CPU periods are what a limit does to a busy moment, not a
    // problem: past throttling counts from half the periods. Throttling now is reported by ContainerRules from 25 %.
    private static bool Routine(HostEvent e) => AgentEvents.ThrottledPercent(e) is < StrongThrottlePercent;

    public static void Check(HostState host, string? containerId, IReadOnlyList<HostEvent> events,
        IReadOnlyDictionary<string, (string Label, bool Replaced)> labels, int windowSeconds, List<Finding> findings)
    {
        var looping = CheckCrashLoops(host, containerId, events, labels, windowSeconds, findings);
        foreach (var group in events
            .Where(e => EventLevel.AtLeast(e.Level, EventLevel.Warning) && (containerId is null || e.ContainerId == containerId) && !Routine(e))
            .GroupBy(e => (e.ContainerId, e.Kind, e.Level)))
        {
            var latest = group.First();
            // The crash-loop finding already counts these.
            if (latest.ContainerId is { } loopingId && looping.Contains(loopingId)
                && latest.Kind is EventKind.Lifecycle or EventKind.Oom or EventKind.Memory) continue;
            var label = latest.ContainerId is { } cid ? labels.GetValueOrDefault(cid, (cid, false)) : (host.DisplayName, false);
            // The stop of a container since replaced under the same name (Compose up, a deploy) is history, not a problem.
            if (label.Item2 && latest.Kind == EventKind.Lifecycle) continue;
            var count = group.Count();
            // A SIGKILL is a forced stop or the kernel ending the container at its memory limit, which often leaves no
            // event of its own: the memory before it tells them apart.
            var killed = AgentEvents.Signal(latest) == 9;
            findings.Add(new Finding(
                latest.Level == EventLevel.Error ? Severity.Critical : Severity.Warning,
                FindingCode.Event(latest.Kind),
                latest.ContainerId is null ? ResourceTarget.Host : ResourceTarget.Container,
                latest.ContainerId ?? host.HostId,
                label.Item1,
                host.HostId,
                (count == 1 ? $"{latest.Message} ({latest.Time:u})" : $"{count} times, most recently at {latest.Time:u}: {latest.Message}")
                + (killed ? ". Signal 9 is also how the kernel ends a container at its memory limit: its memory before the stop shows which." : ""),
                killed ? [Look.Trends("memoryGrowth", 3600)] : [Look.Events]));
        }
    }

    // A container that keeps starting and ending inside the event window: one finding that counts the starts, how
    // each run ended and how long a run lasts, rather than one finding per kind of event.
    private static HashSet<string> CheckCrashLoops(HostState host, string? containerId, IReadOnlyList<HostEvent> events,
        IReadOnlyDictionary<string, (string Label, bool Replaced)> labels, int windowSeconds, List<Finding> findings)
    {
        var looping = new HashSet<string>();
        foreach (var group in events.Where(e => e.ContainerId is not null && (containerId is null || e.ContainerId == containerId)).GroupBy(e => e.ContainerId!))
        {
            int starts = 0, ends = 0, kills = 0, limits = 0;
            DateTimeOffset firstStart = default, lastStart = default;
            string? lastEnd = null;
            foreach (var e in group)
            {
                if (e.Kind == EventKind.Oom) kills++;
                else if (e.Kind == EventKind.Memory) limits++;
                else if (AgentEvents.IsStart(e))
                {
                    if (starts++ == 0) lastStart = e.Time;
                    firstStart = e.Time;
                }
                else if (AgentEvents.IsFailedEnd(e))
                {
                    ends++;
                    lastEnd ??= e.Message;
                }
            }

            if (starts < CrashLoopStarts || ends + kills < CrashLoopStarts - 1) continue;

            var every = (lastStart - firstStart).TotalSeconds / (starts - 1);
            var memory = kills + limits > 0
                ? " In that time it " + (limits > 0 ? $"reached its memory limit {limits} time(s)" : "")
                    + (limits > 0 && kills > 0 ? " and " : "") + (kills > 0 ? $"had {kills} out-of-memory kill(s)" : "")
                    + (Math.Max(limits, kills) >= starts - 1 ? ": about once per run, so each run ends when its memory reaches the limit." : ".")
                : "";
            findings.Add(new Finding(Severity.Critical, FindingCode.ContainerCrashLoop, ResourceTarget.Container, group.Key,
                labels.GetValueOrDefault(group.Key, (group.Key, false)).Item1, host.HostId,
                $"Keeps restarting: started {starts} times in the last {windowSeconds / 60} min, about every {every:0} s, and ended with an error {ends} time(s)"
                + (lastEnd is null ? "." : $" (last: {lastEnd}).") + memory,
                kills + limits > 0
                    ? [Look.Trends("memory"), Look.Metrics()]
                    : [Look.Logs("error"), Look.Events]));
            looping.Add(group.Key);
        }

        return looping;
    }
}
