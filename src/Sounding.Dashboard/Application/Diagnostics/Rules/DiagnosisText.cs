using System.Globalization;

namespace Sounding.Dashboard.Application.Diagnostics.Rules;

// How findings write figures, and the sentences several areas share.
internal static class DiagnosisText
{
    // What a small model needs told where it reads: the stop is the finding, and its reason is not in the data, so it
    // answers instead of searching every other tool for one.
    public const string StoppedWhy = "Sounding records that it stopped, not why: the reason is on its host (docker inspect, docker logs), not in more Sounding calls. Whatever calls it fails with it.";

    public const string NotAnsweringWhy = "Sounding shows that it does not answer, not why: check it on its host (docker inspect shows whether it is paused). More Sounding calls will not show the reason.";

    public static string NotReporting(DateTimeOffset since) =>
        $"Not reporting: nothing has been received from it since {since:u}, and no stop or exit was recorded. Whether it still runs and answers is not known from this alone: it may be paused, frozen or cut off from the network, or only its monitoring may have stopped.";

    // A silent container shown not to answer by what its callers saw.
    public static string NotAnswering(DateTimeOffset since, string evidence) =>
        $"Not answering: nothing has been received from it since {since:u}, and {evidence}. No stop or exit was recorded: it is most likely paused, frozen or cut off from the network.";

    // Adds a finding at the severity the value reaches, or none below the warning level.
    public static void Threshold(double value, double critical, double warning, string code, string message, Lookup[]? next,
        Action<string, string, string, Lookup[]?> add)
    {
        if (value >= critical) add(Severity.Critical, code, message, next);
        else if (value >= warning) add(Severity.Warning, code, message, next);
    }

    public static string SignalName(int signal) => signal switch
    {
        9 => " (SIGKILL)",
        15 => " (SIGTERM)",
        2 => " (SIGINT)",
        6 => " (SIGABRT)",
        11 => " (SIGSEGV)",
        _ => "",
    };

    public static string Duration(double hours) => hours < 1
        ? (hours * 60).ToString("0", CultureInfo.InvariantCulture) + " min"
        : hours.ToString("0.#", CultureInfo.InvariantCulture) + " h";

    public static string Pct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + " %";

    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.#", CultureInfo.InvariantCulture) + " MiB",
        _ => (bytes / 1024d).ToString("0", CultureInfo.InvariantCulture) + " KiB",
    };
}
