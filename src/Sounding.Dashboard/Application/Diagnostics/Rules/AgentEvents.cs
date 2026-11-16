using Sounding.Dashboard.Application.Events;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// What a lifecycle event says, read from the agent's own wording ("Started", "Stopped by signal 9", "Exited with code
// 1"): events carry their facts only as text, and this is the one place that knows how it is written.
internal static class AgentEvents
{
    private const string SignalPrefix = "by signal ";

    public static bool IsStart(HostEvent e) => e.Kind == EventKind.Lifecycle && e.Message.StartsWith("Started", StringComparison.Ordinal);

    // An end that is not a clean stop: a signal or a non-zero exit code (the agent writes those as warnings).
    public static bool IsFailedEnd(HostEvent e) => e.Kind == EventKind.Lifecycle && !IsStart(e) && e.Level != EventLevel.Info;

    public static int? Signal(HostEvent e) => e.Kind == EventKind.Lifecycle ? NumberAfter(e.Message, SignalPrefix) : null;

    // "CPU throttled: held back in 33% of periods by its limit".
    public static int? ThrottledPercent(HostEvent e) => e.Kind == EventKind.Cpu ? NumberAfter(e.Message, ThrottledPrefix) : null;

    private const string ThrottledPrefix = "held back in ";

    private static int? NumberAfter(string message, string prefix)
    {
        var at = message.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0) return null;
        var digits = message.AsSpan(at + prefix.Length);
        var length = 0;
        while (length < digits.Length && char.IsAsciiDigit(digits[length])) length++;
        return int.TryParse(digits[..length], out var number) ? number : null;
    }
}
