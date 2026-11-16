using Sounding.Dashboard.Application.Diagnostics;

namespace Sounding.Dashboard.Mcp;

// A diagnosis as a model reads it: what to look at next is the tool call to make, with the finding's own id filled in,
// and the report says how far its answer reaches. That wording belongs to this server, not to the diagnosis: the REST
// API gives the same findings with links instead.
public sealed record DiagnosisView(
    DateTimeOffset At,
    string Status,
    int Critical,
    int Warnings,
    int Omitted,
    string Checked,
    string? Guidance,
    IReadOnlyList<string> Measured,
    IReadOnlyList<string> Gaps,
    IReadOnlyList<FindingView> Findings)
{
    // "Healthy" covers the checks and the event window above, now: not what recovered earlier, and not what the gaps
    // left out. For a question about now it is the answer, and a small model told so stops; for one about earlier,
    // one look back is enough.
    private const string Healthy =
        "Nothing is wrong now, in any container checked, including whichever the question names. If the question is about now, this is the answer: reply without more calls that Sounding shows no current problem, quote 'measured', and ask what exactly was observed and when. "
        + "Only if it is about something that already stopped (earlier than the last 15 minutes), call what_changed once, with minutes reaching back to that time.";

    private const string Calm =
        "Only minor warnings, and no request is failing or slow now. If none of them explains the question and it is about now, this is the answer: reply without more calls that Sounding shows no current cause and mention the warnings as separate. "
        + "Only if it is about something that already stopped, call what_changed once, with minutes reaching back to that time.";

    private const string Partial = " Some checks were incomplete (see 'gaps'): do not read their absence as proof.";

    public static DiagnosisView From(DiagnosisReport report)
    {
        var guidance = report.Findings.Count == 0 ? Healthy
            : report.Critical == 0 && !report.Findings.Any(static f => FindingCode.IsAboutCalls(f.Code)) ? Calm
            : null;
        if (guidance is not null && report.Gaps.Count > 0) guidance += Partial;
        return new DiagnosisView(report.At, report.Status, report.Critical, report.Warnings, report.Omitted, report.Checked, guidance, report.Measured, report.Gaps,
            [.. report.Findings.Select(static f => new FindingView(f.Severity, f.Code, f.TargetName ?? f.TargetId, f.TargetId, f.Message, f.Related, NextStep(f)))]);
    }

    // The call names the finding's exact id: a display name may carry a note ("(no longer shown)") or, for an old
    // container, resolve to the one that replaced it.
    private static string? NextStep(Finding finding)
    {
        var calls = (finding.Next ?? []).Select(lookup => Call(lookup, finding)).ToList();
        var step = calls.Count == 0 ? null : $"Call {string.Join(", and ", calls)}.";
        return finding.Advice is null ? step : step is null ? finding.Advice : $"{step} {finding.Advice}";
    }

    private static string Call(Lookup lookup, Finding finding)
    {
        var target = $"target \"{finding.TargetId}\"";
        return lookup.Kind switch
        {
            Look.EventsKind => $"get_events with {target}",
            Look.TimelineKind => $"get_timeline with {target}",
            Look.TrendsKind => $"get_process_trends with {target}" + Option("sort", lookup.Sort) + (lookup.Seconds is { } seconds ? $" and minutes {seconds / 60}" : ""),
            Look.ProcessesKind => $"get_processes with {target}" + Option("sort", lookup.Sort),
            Look.MetricsKind => $"get_metrics with {target}" + Option("metrics", lookup.Metrics),
            Look.StorageKind => $"get_storage with {target}",
            Look.LogsKind => $"get_logs with {target}" + Option("level", lookup.Level),
            Look.ConnectionsKind => $"get_connections with {target}" + (lookup.ProblemsOnly ? " and problemsOnly true" : ""),
            Look.NetworkKind => $"get_network with {target}",
            Look.TraceKind => $"trace_failure with {target}",
            Look.ContainersKind => $"list_containers with host \"{finding.HostId}\"" + Option("sort", lookup.Sort),
            _ => $"get_host with target \"{finding.HostId}\"",
        };
    }

    private static string Option(string name, string? value) => value is null ? "" : $" and {name} \"{value}\"";
}

// Target is the name to show; TargetId is the id to call tools with.
public sealed record FindingView(string Severity, string Code, string Target, string TargetId, string Message, string? Related, string? NextStep);
