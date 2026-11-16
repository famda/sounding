using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;

namespace Sounding.Dashboard.Mcp;

// Investigations a client can offer its user (often as slash commands). Each is a short path of exact tool calls with
// the user's own names filled in, branching on what the results show: a small model follows every step it is given,
// so a step it may not need is written as a condition, not as a step. The conclusions are the model's to draw.
[McpServerPromptType]
public static class SoundingPrompts
{
    private const string Rules = """
        Rules:
        - Use about 6 tool calls at most. Each call must tell you something you do not know yet: never repeat a tool
          with only another filter, and do not look up every container in turn.
        - Only state what a tool result shows, and quote its numbers and UTC times.
        - "Not reporting" alone does not mean down; what a result says is not known stays unknown: put it under
          Uncertain instead of searching for it.
        - Blame only what explains the problem described, after checking that the affected service depends on it.
        - If a tool returns an error, read it and correct the call. Never invent a name. If a name matches several
          containers, ask which one is meant.

        Answer with these sections:
        1. Cause: one or two sentences.
        2. Evidence: the results that support it, with numbers and times.
        3. Impact: what is affected, and since when.
        4. What to do: concrete next actions.
        5. Uncertain: what the data could not confirm, and how to check it.
        """;

    [McpServerPrompt(Name = "investigate", Title = "Investigate a problem")]
    [Description("Finds the cause of a problem you describe, following the containers and services involved, and explains it with evidence.")]
    public static string Investigate(
        [Description("What you observed, for example \"placing orders fails\" or \"the API is slow\".")] string problem,
        [Description("The container or host involved, if you know it.")] string? target = null,
        [Description("When it started, as a UTC time such as 2026-01-31T14:03:00Z, if you know it.")] string? since = null)
    {
        var named = string.IsNullOrWhiteSpace(target) ? null : target.Trim();
        var moment = string.IsNullOrWhiteSpace(since) ? null : since.Trim();
        var on = named is null ? "" : $" with target \"{named}\"";
        var steps = new StringBuilder();
        var step = 0;
        void Add(string text) => steps.Append(++step).Append(". ").AppendLine(text);

        Add($"Call diagnose{on}. Note the findings that could explain the problem.");
        Add("If no current finding explains it, the problem may already be over: call what_changed once, with minutes reaching back to when it was seen (default 30).");
        Add($"If requests fail or are slow, call trace_failure once{(named is null ? ", for the container that reports them" : on)}: the service it names as unreachable or furthest is where to look.");
        if (moment is not null) Add($"If it helps, call get_processes with at \"{moment}\"{(named is null ? "" : $" and target \"{named}\"")}, to see what was running then.");
        Add("For the one finding that explains the problem, make the call it suggests, then confirm with one check. Then answer.");

        return $"""
            Investigate this problem with the Sounding tools: {problem.Trim()}
            {(named is null ? "" : $"It concerns \"{named}\".\n")}{(moment is null ? "" : $"It started at {moment}.\n")}
            Steps:
            {steps}
            {Rules}
            """;
    }

    [McpServerPrompt(Name = "check_health", Title = "Check everything")]
    [Description("Checks every host and container for problems and explains the most important ones.")]
    public static string CheckHealth() => $"""
        Check the health of everything Sounding monitors.

        Steps:
        1. Call diagnose with no arguments: what is wrong now.
        2. Call what_changed: what failed in the last 30 minutes and has already recovered (diagnose does not show it).
        3. If neither shows a problem, say that nothing is wrong now or in the last 30 minutes, and stop.
        4. Otherwise, for each critical problem (at most three), make the one call its finding suggests. Mention warnings
           briefly, without investigating them.

        {Rules}
        """;

    [McpServerPrompt(Name = "review_container", Title = "Review a container")]
    [Description("Reviews one container: whether it is healthy, how close it is to its limits, and what it depends on.")]
    public static string ReviewContainer(
        [Description("The container to review, by name.")] string target) => $"""
        Review the container "{target.Trim()}" with the Sounding tools.

        Steps:
        1. Call get_container with target "{target.Trim()}": is it running, how close to its limits, how did it last end?
        2. Call get_timeline with target "{target.Trim()}" and minutes 60: did it restart, get killed or spike within the
           hour, even if it is healthy now?
        3. Call diagnose with target "{target.Trim()}": its current problems and their next calls.
        4. Call trace_failure with target "{target.Trim()}": do the services it calls fail or run slowly?
        5. Only if steps 1 to 3 show its disk growing, call get_storage with target "{target.Trim()}".

        {Rules}
        """;
}
