namespace Sounding.Dashboard.Application.History;

public static class DetailUnits
{
    public static bool IsHostPart(string name) =>
        name.StartsWith("power:", StringComparison.Ordinal) || name.StartsWith("clock:", StringComparison.Ordinal)
        || name.StartsWith("temp:", StringComparison.Ordinal);

    public static string Of(string name)
    {
        if (name.StartsWith("power:", StringComparison.Ordinal)) return "W";
        if (name.StartsWith("clock:", StringComparison.Ordinal)) return "MHz";
        if (name.StartsWith("temp:", StringComparison.Ordinal)) return "°C";
        if (name.Contains("Pressure", StringComparison.Ordinal) || name.StartsWith("cpu", StringComparison.Ordinal) || name == "throttled") return "%";

        return name switch
        {
            "throttledMs" => "ms/s",
            "dnsReplyMs" => "ms",
            "tasks" or "conntrack" or "openFiles" or "fileHandles" or "running" or "blocked" => "count",
            "anon" or "file" or "kernel" or "memoryCurrent" or "memoryUsed" or "memoryCached" or "dirty" or "writeback"
                or "committed" or "slabReclaimable" or "slabUnreclaimable" or "diskUsed" => "bytes",
            "readOps" or "writeOps" => "ops/s",
            "rxPackets" or "txPackets" => "packets/s",
            "logLines" or "logErrors" or "logWarnings" => "lines/s",
            "toDevice" or "toHost" or "internetOut" or "internetIn" or "internalOut" or "internalIn" or "networkOut" or "networkIn"
                or "diskRead" or "diskWrite" or "netRx" or "netTx" or "swapIn" or "swapOut" or "reclaimScanned" or "reclaimed"
                or "read" or "write" or "rx" or "tx" => "bytes/s",
            _ => "per second",
        };
    }
}
