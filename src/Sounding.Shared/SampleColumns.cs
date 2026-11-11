namespace Sounding.Shared;

// A value kept of every sample, as a number in its own column: its column, how the agent's insert reads it out
// of the sample's JSON, and the name of its series in the dashboard's detail history (none when it has no such
// series). The schema, the agents' inserts and the dashboard's queries are all written from these lists, so a
// value is read out of the JSON once, when it is stored, and never again.
public sealed record SampleColumn(string Column, string Sql, string? Series = null);

public static class SampleColumns
{
    // The sum of a field over one of the detail's arrays: 0 for an empty array, nothing without a detail.
    private static string SumOf(string array, string field) =>
        $"(case when detail is null then null else coalesce((select sum((x ->> '{field}')::float8) from jsonb_array_elements(detail -> '{array}') x), 0) end)";

    // A container sample's detail, read from "detail" (the sample's ContainerDetail).
    public static readonly SampleColumn[] ContainerDetail =
    [
        new("cpu_pressure", "(detail #>> '{pressure,cpuSome}')::float8", "cpuPressure"),
        new("cpu_pressure_full", "(detail #>> '{pressure,cpuFull}')::float8", "cpuPressureFull"),
        new("memory_pressure", "(detail #>> '{pressure,memorySome}')::float8", "memoryPressure"),
        new("memory_pressure_full", "(detail #>> '{pressure,memoryFull}')::float8", "memoryPressureFull"),
        new("io_pressure", "(detail #>> '{pressure,ioSome}')::float8", "ioPressure"),
        new("io_pressure_full", "(detail #>> '{pressure,ioFull}')::float8", "ioPressureFull"),
        new("throttled", "(detail #>> '{throttling,throttledPercent}')::float8", "throttled"),
        new("throttled_ms", "(detail #>> '{throttling,throttledMillisecondsPerSecond}')::float8", "throttledMs"),
        new("tasks", "(detail ->> 'tasks')::float8", "tasks"),
        new("anon", "(detail #>> '{memory,anon}')::float8", "anon"),
        new("file", "(detail #>> '{memory,file}')::float8", "file"),
        new("kernel", "(detail #>> '{memory,kernel}')::float8", "kernel"),
        new("read_ops", SumOf("ioDevices", "readOpsPerSecond"), "readOps"),
        new("write_ops", SumOf("ioDevices", "writeOpsPerSecond"), "writeOps"),
        new("to_device", SumOf("gpus", "hostToDeviceBytesPerSecond"), "toDevice"),
        new("to_host", SumOf("gpus", "deviceToHostBytesPerSecond"), "toHost"),
        new("rx_packets", SumOf("interfaces", "receivePacketsPerSecond"), "rxPackets"),
        new("tx_packets", SumOf("interfaces", "transmitPacketsPerSecond"), "txPackets"),
        new("internet_out", "(detail #>> '{traffic,internetOutBytesPerSecond}')::float8", "internetOut"),
        new("internet_in", "(detail #>> '{traffic,internetInBytesPerSecond}')::float8", "internetIn"),
        new("internal_out", "(detail #>> '{traffic,internalOutBytesPerSecond}')::float8", "internalOut"),
        new("internal_in", "(detail #>> '{traffic,internalInBytesPerSecond}')::float8", "internalIn"),
        new("network_out", "(detail #>> '{traffic,networkOutBytesPerSecond}')::float8", "networkOut"),
        new("network_in", "(detail #>> '{traffic,networkInBytesPerSecond}')::float8", "networkIn"),
        new("log_lines", "(detail #>> '{logs,linesPerSecond}')::float8", "logLines"),
        new("log_errors", "(detail #>> '{logs,errorsPerSecond}')::float8", "logErrors"),
        new("log_warnings", "(detail #>> '{logs,warningsPerSecond}')::float8", "logWarnings"),
        new("major_faults", "(detail #>> '{memory,majorFaultsPerSecond}')::float8", "majorFaults"),
        new("refaults", "(detail #>> '{memory,refaultsPerSecond}')::float8", "refaults"),
        new("launches", SumOf("gpus", "launchesPerSecond"), "launches"),
        new("retransmits", "(detail #>> '{tcp,retransmitsPerSecond}')::float8", "retransmits"),
        new("resets", "(detail #>> '{tcp,resetsSentPerSecond}')::float8", "resets"),
        new("failed_connects", "(detail #>> '{tcp,failedConnectsPerSecond}')::float8", "failedConnects"),
        new("tasks_started", "(detail ->> 'tasksStartedPerSecond')::float8", "tasksStarted"),
        new("memory_current", "(detail #>> '{memory,currentBytes}')::float8", "memoryCurrent"),
        new("dirty", "(detail #>> '{memory,dirtyBytes}')::float8", "dirty"),
        new("writeback", "(detail #>> '{memory,writebackBytes}')::float8", "writeback"),
        new("reclaim_scanned", "(detail #>> '{memory,reclaimScannedBytesPerSecond}')::float8", "reclaimScanned"),
        new("reclaimed", "(detail #>> '{memory,reclaimedBytesPerSecond}')::float8", "reclaimed"),
        new("dns_lookups", "(detail #>> '{dns,lookupsPerSecond}')::float8", "dnsLookups"),
        new("dns_failures", "(detail #>> '{dns,failuresPerSecond}')::float8", "dnsFailures"),
        new("dns_reply_ms", "(detail #>> '{dns,averageReplyMs}')::float8", "dnsReplyMs"),
        new("udp_errors", "(detail #>> '{udp,receiveErrorsPerSecond}')::float8", "udpErrors"),
        new("udp_buffer_errors", "(detail #>> '{udp,bufferErrorsPerSecond}')::float8", "udpBufferErrors"),
        new("udp_no_port", "(detail #>> '{udp,noPortPerSecond}')::float8", "udpNoPort"),
        new("conntrack", "(detail #>> '{conntrack,entries}')::float8", "conntrack"),
        new("open_files", "(detail ->> 'openFiles')::float8", "openFiles"),
    ];

    private const string Devices = "jsonb_array_elements(s -> 'disk' -> 'devices') d";
    private const string Interfaces = "jsonb_array_elements(s -> 'network' -> 'interfaces') i";
    private const string Gpus = "jsonb_array_elements(s -> 'gpu' -> 'devices') d";

    // A host sample, read from "s" (the whole HostSnapshot, with the parts other agents sampled merged in).
    public static readonly SampleColumn[] Host =
    [
        new("cpu_percent", "(s #>> '{cpu,usagePercent}')::float8"),
        new("cpu_logical_cores", "(s #>> '{cpu,logicalCores}')::float8"),
        new("gpu_percent", $"(select avg((d ->> 'utilizationPercent')::float8) from {Gpus})"),
        new("gpu_memory_used", $"(select sum((d ->> 'memoryUsedBytes')::float8) from {Gpus})"),
        new("containers_cpu_cores", "(s #>> '{containers,cpuCores}')::float8"),
        new("cpu_pressure", "(s #>> '{pressure,cpuSome}')::float8", "cpuPressure"),
        new("memory_pressure", "(s #>> '{pressure,memorySome}')::float8", "memoryPressure"),
        new("memory_pressure_full", "(s #>> '{pressure,memoryFull}')::float8", "memoryPressureFull"),
        new("io_pressure", "(s #>> '{pressure,ioSome}')::float8", "ioPressure"),
        new("io_pressure_full", "(s #>> '{pressure,ioFull}')::float8", "ioPressureFull"),
        new("disk_read", "(s #>> '{disk,readBytesPerSecond}')::float8", "diskRead"),
        new("disk_write", "(s #>> '{disk,writeBytesPerSecond}')::float8", "diskWrite"),
        new("net_rx", "case when (s #>> '{network,available}')::boolean then (s #>> '{network,receiveBytesPerSecond}')::float8 end", "netRx"),
        new("net_tx", "case when (s #>> '{network,available}')::boolean then (s #>> '{network,transmitBytesPerSecond}')::float8 end", "netTx"),
        new("memory_used", "(s #>> '{memory,usedBytes}')::float8", "memoryUsed"),
        new("memory_cached", "(s #>> '{memory,cachedBytes}')::float8", "memoryCached"),
        new("read_ops", $"(select sum((d ->> 'readOpsPerSecond')::float8) from {Devices})", "readOps"),
        new("write_ops", $"(select sum((d ->> 'writeOpsPerSecond')::float8) from {Devices})", "writeOps"),
        new("rx_packets", $"(select sum((i ->> 'receivePacketsPerSecond')::float8) from {Interfaces})", "rxPackets"),
        new("tx_packets", $"(select sum((i ->> 'transmitPacketsPerSecond')::float8) from {Interfaces})", "txPackets"),
        new("cpu_user", "(s #>> '{cpu,split,user}')::float8 + (s #>> '{cpu,split,nice}')::float8", "cpuUser"),
        new("cpu_system", "(s #>> '{cpu,split,system}')::float8", "cpuSystem"),
        new("cpu_io_wait", "(s #>> '{cpu,split,ioWait}')::float8", "cpuIoWait"),
        new("cpu_irq", "(s #>> '{cpu,split,irq}')::float8 + (s #>> '{cpu,split,softIrq}')::float8", "cpuIrq"),
        new("cpu_steal", "(s #>> '{cpu,split,steal}')::float8", "cpuSteal"),
        new("major_faults", "(s #>> '{memory,majorFaultsPerSecond}')::float8", "majorFaults"),
        new("swap_in", "(s #>> '{memory,swapInBytesPerSecond}')::float8", "swapIn"),
        new("swap_out", "(s #>> '{memory,swapOutBytesPerSecond}')::float8", "swapOut"),
        new("retransmits", "(s #>> '{network,tcp,retransmitsPerSecond}')::float8", "retransmits"),
        new("resets", "(s #>> '{network,tcp,resetsSentPerSecond}')::float8", "resets"),
        new("failed_connects", "(s #>> '{network,tcp,failedConnectsPerSecond}')::float8", "failedConnects"),
        new("running", "(s #>> '{cpu,activity,running}')::float8", "running"),
        new("blocked", "(s #>> '{cpu,activity,blocked}')::float8", "blocked"),
        new("context_switches", "(s #>> '{cpu,activity,contextSwitchesPerSecond}')::float8", "contextSwitches"),
        new("processes_started", "(s #>> '{cpu,activity,processesStartedPerSecond}')::float8", "processesStarted"),
        new("dirty", "(s #>> '{memory,dirtyBytes}')::float8", "dirty"),
        new("writeback", "(s #>> '{memory,writebackBytes}')::float8", "writeback"),
        new("committed", "(s #>> '{memory,committedBytes}')::float8", "committed"),
        new("reclaim_scanned", "(s #>> '{memory,reclaimScannedBytesPerSecond}')::float8", "reclaimScanned"),
        new("reclaimed", "(s #>> '{memory,reclaimedBytesPerSecond}')::float8", "reclaimed"),
        new("slab_reclaimable", "(s #>> '{memory,slabReclaimableBytes}')::float8", "slabReclaimable"),
        new("slab_unreclaimable", "(s #>> '{memory,slabUnreclaimableBytes}')::float8", "slabUnreclaimable"),
        new("udp_errors", "(s #>> '{network,udp,receiveErrorsPerSecond}')::float8", "udpErrors"),
        new("udp_buffer_errors", "(s #>> '{network,udp,bufferErrorsPerSecond}')::float8", "udpBufferErrors"),
        new("udp_no_port", "(s #>> '{network,udp,noPortPerSecond}')::float8", "udpNoPort"),
        new("conntrack", "(s #>> '{network,conntrack,entries}')::float8", "conntrack"),
        new("file_handles", "(s #>> '{files,open}')::float8", "fileHandles"),
    ];

    // What a host sample keeps per core, per GPU and per sensor, read from "s" like the columns above: each core's
    // use in order, and of every GPU and sensor only what is charted over time.
    public const string HostCoresSql =
        "array(select c.v::float8 from jsonb_array_elements(s -> 'cpu' -> 'coreUsagePercent') with ordinality c(v, i) order by c.i)";

    public const string HostGpusSql = """
        (select jsonb_agg(jsonb_strip_nulls(jsonb_build_object(
            'index', d -> 'index', 'utilizationPercent', d -> 'utilizationPercent', 'memoryUsagePercent', d -> 'memoryUsagePercent',
            'powerDrawWatts', d -> 'powerDrawWatts', 'clockSmMhz', d -> 'clockSmMhz')))
         from jsonb_array_elements(s -> 'gpu' -> 'devices') d)
        """;

    public const string HostTemperaturesSql = """
        (select jsonb_agg(jsonb_build_object('source', t -> 'source', 'label', t -> 'label', 'celsius', t -> 'celsius'))
         from jsonb_array_elements(s -> 'temperatures') t)
        """;
}
