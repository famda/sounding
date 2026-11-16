using System.Globalization;

namespace Sounding.Dashboard.Application.Network;

// What a caller's own connections show about whether a peer answers: requests sent and never answered on connections
// the caller has since closed (an open one may still be waiting), with no answered request after them, or connections
// left unanswered, refused or unreachable. It is the evidence that a peer which stopped reporting has also stopped
// answering, as opposed to only its monitoring having stopped.
public static class CallEvidence
{
    public const int RecentSeconds = 120;

    public static string? Unanswered(ContainerNetwork caller, Func<Connection, bool> toPeer)
    {
        int unanswered = 0, failed = 0;
        double newestUnanswered = double.MaxValue, newestAnswered = double.MaxValue;
        string? problem = null;
        foreach (var c in caller.Network.Connections)
        {
            if (!toPeer(c)) continue;
            if (c.HttpStatus is not null) newestAnswered = Math.Min(newestAnswered, c.LastSeenSecondsAgo);
            if (c.LastSeenSecondsAgo > RecentSeconds) continue;
            if (c.HttpRequest is not null && c.HttpStatus is null && c.State is not "ESTABLISHED")
            {
                unanswered++;
                newestUnanswered = Math.Min(newestUnanswered, c.LastSeenSecondsAgo);
            }

            foreach (var p in c.Problems ?? [])
            {
                if (p is not ("no answer" or "refused" or "unreachable")) continue;
                failed++;
                problem ??= p;
                break;
            }
        }

        // An answer after the unanswered requests means the peer answers again.
        if (unanswered > 0 && newestAnswered < newestUnanswered) unanswered = 0;
        if (unanswered > 0)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{unanswered} request(s) to it in the last {RecentSeconds / 60} min got no answer (the latest {newestUnanswered:0} s ago)")
                + (newestAnswered == double.MaxValue ? "" : string.Create(CultureInfo.InvariantCulture, $", and its last answer was {newestAnswered:0} s ago"));
        }

        return failed == 0 ? null : $"{failed} connection(s) to it in the last {RecentSeconds / 60} min show \"{problem}\"";
    }
}
