using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.History;
using static Sounding.Dashboard.Application.Diagnostics.Rules.DiagnosisText;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// A fact, not a verdict: which containers stopped in the last hours and have not started again, beyond the few
// minutes the fleet view keeps them. Whether that explains another finding is for the reader to work out.
internal static class StoppedRules
{
    public static void Check(FleetState fleet, Scope scope, IReadOnlyList<(string HostId, ContainerState Container)> stopped, List<Finding> findings)
    {
        foreach (var (hostId, container) in stopped)
        {
            if (!scope.IncludesHost(hostId) || !scope.Includes(container.ContainerId)) continue;
            // Containers still in the fleet view are already checked with the others.
            if (FleetSnapshot.FindHostById(fleet, hostId)?.Containers.Any(c => c.ContainerId == container.ContainerId) == true) continue;

            var how = container.ExitSignal is { } signal ? $"by signal {signal}{SignalName(signal)}"
                : container.ExitCode is { } code ? $"with exit code {code}" : "";
            var failed = container.ExitCode is not (null or 0) || container.ExitSignal is 9;
            findings.Add(new Finding(failed ? Severity.Critical : Severity.Warning, FindingCode.ContainerStopped, ResourceTarget.Container,
                container.ContainerId, container.Name, hostId,
                $"Stopped at {container.LastSeen:u} {how} and has not started again: no running container has its name.".Replace("  ", " "),
                [Look.Events], Advice: StoppedWhy, Facts: new FindingFacts(Since: container.LastSeen)));
        }
    }
}
