using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.History;
using Sounding.Dashboard.Application.Network;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// What is done with the findings once every area has added its own. Reads codes and facts, never messages.
internal static class FindingRelations
{
    // One incident shows up in the latest sample, in the events and in the stopped list: the finding that says most
    // stays, and what repeats it goes.
    public static void Dedupe(List<Finding> findings)
    {
        var codes = new HashSet<(string TargetId, string Code)>();
        foreach (var finding in findings) codes.Add((finding.TargetId, finding.Code));

        findings.RemoveAll(f => f.Code switch
        {
            FindingCode.ContainerOomKill => codes.Contains((f.TargetId, FindingCode.ContainerCrashLoop)) || codes.Contains((f.TargetId, FindingCode.EventOom)),
            FindingCode.EventLifecycle => codes.Contains((f.TargetId, FindingCode.ContainerKilled))
                || codes.Contains((f.TargetId, FindingCode.ContainerFailed)) || codes.Contains((f.TargetId, FindingCode.ContainerStopped)),
            FindingCode.EventCpu => codes.Contains((f.TargetId, FindingCode.ContainerCpuThrottled)),
            _ => false,
        });
    }

    // Past CPU throttling is a problem when it shows in what the container does: requests it serves or makes that
    // are slow or failing. Without that, bursts held back by a limit are how a busy service works, and the events and
    // the timeline still show them. Throttling now is reported from the live sample whatever its impact.
    public static void KeepWithImpact(List<Finding> findings)
    {
        var affected = new HashSet<string>();
        foreach (var finding in findings)
        {
            if (!FindingCode.IsAboutCalls(finding.Code)) continue;
            affected.Add(finding.TargetId);
            if (finding.Facts?.CalledId is { } called) affected.Add(called);
        }

        findings.RemoveAll(f => f.Code == FindingCode.EventCpu && !affected.Contains(f.TargetId));
    }

    // A finding about a call to another monitored container says what is known of that container: a fact next to a
    // fact. Whether one explains the other is still the reader's to work out, but it no longer takes a second call
    // to see them side by side. A failed lookup is related to the stopped container that carried the name.
    public static void Relate(FleetState fleet, List<Finding> findings)
    {
        var own = new Dictionary<string, List<Finding>>();
        var failingCallers = new Dictionary<string, List<string>>();
        foreach (var finding in findings)
        {
            if (finding is { Code: FindingCode.RequestsFailing, Facts.CalledId: { } calledId })
            {
                if (!failingCallers.TryGetValue(calledId, out var callers)) failingCallers[calledId] = callers = [];
                callers.Add(finding.TargetName ?? finding.TargetId);
            }

            if (finding.Target != ResourceTarget.Container || FindingCode.IsAboutCalls(finding.Code)) continue;
            if (!own.TryGetValue(finding.TargetId, out var list)) own[finding.TargetId] = list = [];
            list.Add(finding);
        }

        // A container that stopped reporting, and that the requests of its callers did not already show unanswered,
        // is known not to answer when calls to it are seen failing.
        for (var i = 0; i < findings.Count; i++)
        {
            if (findings[i] is not { Code: FindingCode.ContainerNotReporting, Severity: Severity.Warning, Facts.Since: { } since } silent
                || !failingCallers.TryGetValue(silent.TargetId, out var callers)) continue;
            findings[i] = silent with
            {
                Severity = Severity.Critical,
                Message = DiagnosisText.NotAnswering(since, $"calls to it from {string.Join(", ", callers.Distinct().Take(3))} fail"),
                Next = null,
                Advice = DiagnosisText.NotAnsweringWhy,
            };
            if (own.TryGetValue(silent.TargetId, out var list)) list[list.IndexOf(silent)] = findings[i];
        }

        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];
            if (finding.Facts?.CalledId is { } peerId)
            {
                var peer = FleetSnapshot.FindContainerById(fleet, peerId);
                var name = peer?.Name ?? peerId;
                var related = own.TryGetValue(peerId, out var theirs)
                    ? $"{name} has findings of its own: {string.Join(" | ", theirs.Take(2).Select(f => f.Message))}"
                    : $"{name} has no finding of its own" + (peer is null ? "" : $" ({TraceFailureHandler.State(peer)})") + ".";
                findings[i] = finding with { Related = related };
            }
            else if (finding is { Code: FindingCode.DnsFailed, Facts.UnresolvedNames: { } names })
            {
                RelateLookup(findings, i, finding, names);
            }
        }
    }

    // Only a single stopped container on the caller's own host that Compose would give the name: the same name on
    // another host is another service, and several candidates leave it unresolved.
    private static void RelateLookup(List<Finding> findings, int index, Finding lookup, IReadOnlyList<string> names)
    {
        var match = -1;
        for (var j = 0; j < findings.Count; j++)
        {
            var stopped = findings[j];
            if (!FindingCode.IsStop(stopped.Code) || stopped.HostId != lookup.HostId) continue;
            if (!names.Any(name => PeerDirectory.NameMatches(stopped.TargetName, name))) continue;
            if (match >= 0) return;
            match = j;
        }

        if (match < 0) return;
        var found = findings[match];
        findings[index] = lookup with { Related = $"Container {found.TargetName} on the same host carried that name: {found.Message}" };
        var caller = lookup.TargetName ?? lookup.TargetId;
        findings[match] = found with
        {
            Related = found.Related is null ? $"Containers that call it and can no longer find it: {caller}" : $"{found.Related}, {caller}",
        };
    }
}
