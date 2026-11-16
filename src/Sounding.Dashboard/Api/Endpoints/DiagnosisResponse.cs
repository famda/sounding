using System.Globalization;
using Sounding.Dashboard.Application.Common;
using Sounding.Dashboard.Application.Diagnostics;
using Sounding.Dashboard.Application.History;

namespace Sounding.Dashboard.Api.Endpoints;

// A diagnosis as the REST API gives it: what to look at next for a finding is a link to the request that shows it.
public sealed record DiagnosisResponse(
    DateTimeOffset At,
    string Status,
    int HostsChecked,
    int ContainersChecked,
    int EventWindowSeconds,
    int Critical,
    int Warnings,
    int Omitted,
    string Checked,
    IReadOnlyList<string> Measured,
    IReadOnlyList<FindingResponse> Findings,
    IReadOnlyList<string> Gaps);

public sealed record FindingResponse(
    string Severity,
    string Code,
    string Target,
    string TargetId,
    string? TargetName,
    string HostId,
    string Message,
    string? Advice,
    string? Related,
    IReadOnlyList<FindingLink> Links);

// Rel says what the link shows (events, timeline, processTrends, metrics, storage, logs, connections, network,
// dependencyTrace, hostContainers, host).
public sealed record FindingLink(string Rel, string Href);

public static class DiagnosisLinks
{
    private const string Root = "/api/v1";

    public static IResult ToDiagnosisResult(this Result<DiagnosisReport> result) =>
        result is { IsSuccess: true, Value: { } report } ? TypedResults.Ok(From(report)) : result.ToHttpResult();

    public static DiagnosisResponse From(DiagnosisReport report) => new(
        report.At, report.Status, report.HostsChecked, report.ContainersChecked, report.EventWindowSeconds,
        report.Critical, report.Warnings, report.Omitted, report.Checked, report.Measured,
        [.. report.Findings.Select(static f => new FindingResponse(f.Severity, f.Code, f.Target, f.TargetId, f.TargetName, f.HostId,
            f.Message, f.Advice, f.Related, [.. (f.Next ?? []).Select(lookup => new FindingLink(lookup.Kind, Href(f, lookup)))]))],
        report.Gaps);

    private static string Href(Finding finding, Lookup lookup)
    {
        var host = $"{Root}/hosts/{Uri.EscapeDataString(finding.HostId)}";
        var target = finding.Target == ResourceTarget.Host ? host : $"{Root}/containers/{Uri.EscapeDataString(finding.TargetId)}";
        return lookup.Kind switch
        {
            Look.EventsKind => $"{target}/events",
            Look.TimelineKind => $"{target}/timeline",
            Look.TrendsKind => Query($"{target}/processes/top", ("sort", lookup.Sort), ("seconds", lookup.Seconds?.ToString(CultureInfo.InvariantCulture))),
            Look.ProcessesKind => Query($"{target}/processes", ("sort", lookup.Sort)),
            Look.MetricsKind => Query($"{target}/history/summary", ("metrics", lookup.Metrics)),
            Look.StorageKind => $"{target}/storage",
            Look.LogsKind => Query($"{target}/logs", ("level", lookup.Level)),
            Look.ConnectionsKind => Query($"{target}/connections", ("problemsOnly", lookup.ProblemsOnly ? "true" : null)),
            Look.NetworkKind => $"{target}/network/overview",
            Look.TraceKind => $"{target}/dependencies/trace",
            Look.ContainersKind => Query($"{Root}/containers", ("hostId", finding.HostId), ("sort", lookup.Sort)),
            _ => host,
        };
    }

    private static string Query(string path, params ReadOnlySpan<(string Name, string? Value)> parameters)
    {
        var separator = '?';
        foreach (var (name, value) in parameters)
        {
            if (value is null) continue;
            path = $"{path}{separator}{name}={Uri.EscapeDataString(value)}";
            separator = '&';
        }

        return path;
    }
}
