using System.Buffers.Binary;
using System.Net;
using System.Text;
using System.Text.Json;
using Npgsql;
using Sounding.Shared;

const long GiB = 1024L * 1024 * 1024;
var connectionString = Environment.GetEnvironmentVariable("SOUNDING_CONNECTION_STRING")
    ?? throw new InvalidOperationException("SOUNDING_CONNECTION_STRING is not configured");
if (new NpgsqlConnectionStringBuilder(connectionString).Database != "sounding_docs")
{
    throw new InvalidOperationException("The demo seeder only writes to a database named sounding_docs.");
}

await using var database = NpgsqlDataSource.Create(connectionString);
await DatabaseSchema.EnsureAsync(database, CancellationToken.None);
var hosts = new[]
{
    new DemoHost("demo-db", "db-01", 16, 64 * GiB, 0, ["postgres-1", "redis-1", "backup-1"]),
    new DemoHost("demo-edge", "edge-01", 8, 16 * GiB, 1, ["nginx-1", "api-1", "client-1", "auth-1", "frontend-1", "jobs-1"]),
    new DemoHost("demo-gpu", "gpu-01", 32, 128 * GiB, 2, ["trainer-1", "inference-1", "worker-1"]),
};
var containers = hosts.SelectMany(host => host.Workloads.Select((name, index) => new DemoContainer(host, name, index))).ToArray();
var channelDemands = new[] { Trace(48_271), Trace(55_109), Trace(71_317), Trace(39_443), Trace(63_091) };
var traces = Enumerable.Range(0, 5).Select(channel =>
    Enumerable.Range(0, containers.Max(container => container.Profile) + 2)
        .Select(profile => Trace(7_019 + profile * 397 + channel * 7_919)).ToArray()).ToArray();
var anchor = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
var started = anchor.AddDays(-2);

await using (var connection = await database.OpenConnectionAsync())
await using (var transaction = await connection.BeginTransactionAsync())
{
    await using var batch = new NpgsqlBatch(connection, transaction);
    Add(batch, "delete from hosts where host_id = any($1)", values: [hosts.Select(host => host.Id).ToArray()]);
    foreach (var host in hosts)
    {
        Add(batch, """
            insert into hosts (host_id, display_name, kernel, leader_container_id, first_seen, last_seen)
            values ($1, $2, '6.8.0-48-generic', $3, $4, $5)
            """, host.Id, host.Name, containers.First(container => container.Host == host).Id, started, anchor);
    }
    foreach (var container in containers)
    {
        Add(batch, """
            insert into containers (container_id, host_id, name, started_at, last_seen, process_started_at, processes, network, latest)
            values ($1, $2, $3, $4, $5, $4, $6::jsonb, $7::jsonb, $8::jsonb)
            """, container.Id, container.Host.Id, container.Name, started, anchor,
            Json(Processes(container, 360)), Json(Network(container, 360)), Json(Usage(container, 360).Detail!));
    }
    Events(batch, anchor);
    // A finished capture on api-1, so the packet capture panel has a file to open.
    var (capture, packets) = Capture(anchor.AddSeconds(-89));
    Add(batch, """
        insert into captures (container_id, requested_at, started_at, seconds, status, finished_at, packets, data)
        values ($1, $2, $2, 10, 'done', $3, $4, $5)
        """, "demo-edge-api-1", anchor.AddSeconds(-90), anchor.AddSeconds(-79), packets, capture);
    for (var seconds = 0; seconds <= 360; seconds++)
    {
        Samples(batch, anchor.AddSeconds(seconds - 360), seconds);
    }
    await batch.ExecuteNonQueryAsync();
    await transaction.CommitAsync();
}

var lastSecond = 360;
await CatchUpAsync(CancellationToken.None);
Console.WriteLine("Synthetic fleet ready: db-01, edge-01, gpu-01; twelve containers and six minutes of one-second history.");

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, args) => { args.Cancel = true; stop.Cancel(); };
using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
try
{
    while (await timer.WaitForNextTickAsync(stop.Token))
    {
        await CatchUpAsync(stop.Token);
    }
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
}

async Task CatchUpAsync(CancellationToken cancellationToken)
{
    while (true)
    {
        var targetSecond = 360 + (int)(DateTimeOffset.UtcNow - anchor).TotalSeconds;
        if (targetSecond <= lastSecond)
        {
            return;
        }

        await using var batch = database.CreateBatch();
        while (lastSecond < targetSecond)
        {
            lastSecond++;
            Samples(batch, anchor.AddSeconds(lastSecond - 360), lastSecond);
        }

        var latestTime = anchor.AddSeconds(lastSecond - 360);
        Add(batch, "update hosts set last_seen = $1 where host_id = any($2)", latestTime, hosts.Select(host => host.Id).ToArray());
        foreach (var container in containers)
        {
            Add(batch, "update containers set last_seen = $1, processes = $2::jsonb, network = $3::jsonb, latest = $4::jsonb where container_id = $5",
                latestTime, Json(Processes(container, lastSecond)), Json(Network(container, lastSecond)), Json(Usage(container, lastSecond).Detail!), container.Id);
        }

        Events(batch, latestTime);
        Add(batch, "select pg_notify($1, '')", DatabaseSchema.NotifyChannel);
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }
}

void Samples(NpgsqlBatch batch, DateTimeOffset time, double seconds)
{
    foreach (var host in hosts)
    {
        // As the agents store it: the whole snapshot as the host's latest, and a row of the charted values.
        Add(batch, $$"""
            with merged as materialized (select $4::jsonb as s),
            sample as (
                insert into host_samples (host_id, ts, container_id, {{string.Join(", ", SampleColumns.Host.Select(c => c.Column))}},
                                          cores, gpus, temperatures)
                select $1, $2, $3, {{string.Join(", ", SampleColumns.Host.Select(c => c.Sql))}},
                       {{SampleColumns.HostCoresSql}}, {{SampleColumns.HostGpusSql}}, {{SampleColumns.HostTemperaturesSql}}
                from merged
            )
            update hosts set latest = merged.s, latest_ts = $2, latest_container_id = $3 from merged where host_id = $1
            """,
            host.Id, time, containers.First(container => container.Host == host).Id, Json(Snapshot(host, seconds)));
    }
    foreach (var container in containers)
    {
        var usage = Usage(container, seconds);
        Add(batch, $"""
            with sample as materialized (select $15::jsonb as detail),
            stored as (
                insert into container_samples (container_id, ts, cpu_percent_of_host, cpu_cores, cpu_limit_cores,
                    memory_bytes, memory_limit_bytes, io_read_bps, io_write_bps, net_rx_bps, net_tx_bps,
                    gpu_percent, gpu_memory_bytes, disk_used_bytes, {string.Join(", ", SampleColumns.ContainerDetail.Select(c => c.Column))})
                select $1, $2, $3, $4, $5::float8, $6, $7::bigint, $8, $9, $10, $11, $12::float8, $13::bigint, $14::bigint,
                       {string.Join(", ", SampleColumns.ContainerDetail.Select(c => c.Sql))}
                from sample
            )
            update containers set detail = sample.detail from sample where container_id = $1
            """, container.Id, time, usage.CpuPercentOfHost, usage.CpuCores, usage.CpuLimitCores,
            usage.MemoryBytes, usage.MemoryLimitBytes, usage.IoReadBytesPerSecond, usage.IoWriteBytesPerSecond,
            usage.NetReceiveBytesPerSecond, usage.NetTransmitBytesPerSecond, usage.GpuPercent, usage.GpuMemoryBytes,
            usage.DiskUsedBytes, Json(usage.Detail!));
        foreach (var process in Processes(container, seconds))
        {
            Add(batch, """
                insert into process_samples (container_id, ts, pid, started_at, name, command, user_name, state,
                    threads, cpu_cores, cpu_seconds, memory_bytes, gpu_percent, gpu_memory_bytes,
                    read_bps, write_bps, read_bytes, write_bytes, parent_pid)
                values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19)
                """, container.Id, time, process.Pid, process.StartedAt, process.Name, process.CommandLine, process.User,
                process.State, process.Threads, process.CpuCores, process.CpuSeconds, process.MemoryBytes,
                process.GpuPercent, process.GpuMemoryBytes, process.ReadBytesPerSecond, process.WriteBytesPerSecond,
                process.ReadBytesTotal, process.WriteBytesTotal, process.ParentPid);
        }
    }
}

// The events of the last few minutes, written again every second relative to now, so they stay in the screenshots'
// five-minute range however long the capture takes. Messages follow the agent's own wording.
void Events(NpgsqlBatch batch, DateTimeOffset now)
{
    Add(batch, "delete from events where host_id = any($1)", values: [hosts.Select(host => host.Id).ToArray()]);
    foreach (var container in containers)
    {
        Event(container.Name, 290, "lifecycle", "info", "Started");
    }
    Event("jobs-1", 250, "process", "info", "Started report-builder (pid 311)");
    Event("jobs-1", 236, "destination", "info", "New internet destination updates.example.com (203.0.113.80):443/tcp");
    Event("nginx-1", 205, "port", "info", "Listening on 8443/tcp (nginx)");
    Event("jobs-1", 188, "process", "info", "report-builder (pid 311) exited after 62 s");
    Event("backup-1", 170, "process", "info", "Started pg_dump (pid 517)");
    Event("api-1", 150, "cpu", "warning", "CPU throttled: held back in 31% of periods by its limit");
    Event("auth-1", 96, "memory", "warning", "Reached its memory limit");
    Event("auth-1", 95, "oom", "error", "Out of memory: auth-worker (pid 87) was killed, using 1843.2 MB");
    Event("auth-1", 93, "process", "info", "Started auth-worker (pid 92)");

    void Event(string name, int secondsAgo, string kind, string level, string message)
    {
        var container = containers.Single(container => container.Name == name);
        Add(batch, "insert into events (host_id, container_id, ts, kind, level, message) values ($1, $2, $3, $4, $5, $6)",
            container.Host.Id, container.Id, now.AddSeconds(-secondsAgo), kind, level, message);
    }
}

HostSnapshot Snapshot(DemoHost host, double seconds)
{
    var workloads = containers.Where(container => container.Host == host).Select(container => Usage(container, seconds)).ToArray();
    var activity = Activity(seconds, host.Index * 6);
    var cpuCores = workloads.Sum(usage => usage.CpuCores) + host.Cores * (0.03 + 0.04 * activity);
    var percent = cpuCores / host.Cores * 100;
    var memory = workloads.Sum(usage => usage.MemoryBytes) + host.Memory / 10;
    var read = workloads.Sum(usage => usage.IoReadBytesPerSecond) + 1e6;
    var write = workloads.Sum(usage => usage.IoWriteBytesPerSecond) + 2e6;
    var receive = workloads.Sum(usage => usage.NetReceiveBytesPerSecond) + 200000;
    var transmit = workloads.Sum(usage => usage.NetTransmitBytesPerSecond) + 100000;
    var gpu = host.Index == 2 ? new GpuStats(true, null,
        Enumerable.Range(0, 2).Select(index =>
        {
            var busId = $"0000:0{index + 1}:00.0";
            var shares = workloads.SelectMany(usage => usage.Detail!.Gpus).Where(share => share.PciBusId == busId).ToArray();
            var utilization = shares.Sum(share => share.Percent);
            var used = shares.Sum(share => share.MemoryBytes);
            return new GpuDevice(index, "NVIDIA L4", "550.54", utilization,
                utilization * 0.6, 24 * GiB, used, (double)used / (24 * GiB) * 100,
                Math.Round(40 + utilization * 0.3), 22 + utilization * 0.5, 72, 0, 1800, 2040,
                PciBusId: busId, PerformanceState: "P0", PcieGeneration: 4, PcieWidth: 16,
                ClockEventReasons: [], EncoderPercent: 0, DecoderPercent: 0, ClockMemoryMhz: 6250,
                ClockMemoryMaxMhz: 6250, TemperatureHeadroomCelsius: 30, EccCorrected: 0, EccUncorrected: 0);
        }).ToArray())
        : new GpuStats(false, "No GPU in this demo host", []);
    var coreWeights = Enumerable.Range(0, host.Cores)
        .Select(index => 0.93 + 0.14 * Activity(seconds + index, host.Index * 6, index % 5)).ToArray();
    var averageWeight = coreWeights.Average();
    var diskSize = (host.Index switch { 0 => 2048L, 1 => 256L, _ => 1024L }) * GiB;
    var diskUsed = (host.Index switch { 0 => 1276L, 1 => 112L, _ => 742L }) * GiB;
    return new HostSnapshot(
        new SystemStats(host.Name, "6.8.0-48-generic", 604800 + host.Index * 194713, [cpuCores, cpuCores * 0.9, cpuCores * 0.8]),
        new CpuStats(host.Index switch { 0 => "AMD EPYC 7313P", 1 => "Intel Xeon E-2388G", _ => "AMD EPYC 7543P" }, host.Cores, percent,
            coreWeights.Select(weight => percent * weight / averageWeight).ToArray(),
            new CpuTimeSplit(percent * 0.7, 0, percent * 0.25, percent * 0.03, 0, percent * 0.02, 0),
            new CpuActivity(4, 0, 2500, 0.1)),
        new MemoryStats(host.Memory, memory, host.Memory - memory, 3 * GiB, 4 * GiB, GiB / 4,
            (double)memory / host.Memory * 100, MajorFaultsPerSecond: 0.2, SwapInBytesPerSecond: 0,
            SwapOutBytesPerSecond: 0, DirtyBytes: 16 * 1024 * 1024, WritebackBytes: 0,
            CommittedBytes: memory, CommitLimitBytes: host.Memory, SlabReclaimableBytes: GiB / 2,
            SlabUnreclaimableBytes: GiB / 4, ReclaimScannedBytesPerSecond: 1024, ReclaimedBytesPerSecond: 1024),
        gpu,
        new DiskStats([new DiskVolume("data", "/srv/data", "ext4", diskSize, diskUsed, diskSize - diskUsed,
                (double)diskUsed / diskSize * 100, null, 10000000, 8000000)],
            [new DiskDevice("nvme0n1", read, write, 8 + 55 * activity, read / 16384, write / 16384,
                0.2 + activity, 0.4 + activity * 2, 0.2 + activity * 3)], read, write),
        new NetworkStats(true, null,
            [new NetworkInterfaceStats("eth0", receive, transmit, receive / 1400, transmit / 1400, 8000000000, 6000000000, 0, 0)],
            receive, transmit, new TcpStats(0.1, 0, 0, 0, 12, 14, 42), new UdpStats(0, 0, 0),
            new Conntrack((int)(9000 + 34000 * Activity(seconds, host.Index * 6, 2)), host.Index == 1 ? 65536 : 262144)),
        new ContainerTotals(workloads.Length, workloads.Sum(usage => usage.CpuPercentOfHost),
            workloads.Sum(usage => usage.CpuCores), workloads.Sum(usage => usage.MemoryBytes)),
        new Pressure(12 * activity * activity, 0, activity, activity * 0.1, 3 * activity, activity * 0.2),
        [new TemperatureSensor(host.Index == 1 ? "coretemp" : "k10temp", "CPU", 38 + 25 * activity, 80, 95)], new FileHandles(2400, 1000000));
}

ContainerUsage Usage(DemoContainer container, double seconds)
{
    var activity = Activity(seconds, container.Profile);
    var cores = CpuCores(container, seconds);
    var memory = (long)(container.MemoryLimit * (0.6 + 0.32 * MemoryActivity(seconds, container.Profile)));
    var read = ReadRate(container, seconds);
    var write = WriteRate(container, seconds);
    var receive = ReceiveRate(container, seconds);
    var transmit = TransmitRate(container, seconds);
    var gpu = container.Host.Index == 2;
    var shares = gpu ? Enumerable.Range(0, 2).Select(index =>
    {
        var load = Activity(seconds - index, container.Profile, 4);
        return new GpuShare($"0000:0{index + 1}:00.0",
            (container.Index == 0 ? 46 : container.Index == 1 ? 32 : 14) * (0.25 + 0.75 * load),
            (long)(((container.Index switch { 0 => 9, 1 => 5, _ => 2 })
                + (container.Index switch { 0 => 2, 1 => 4, _ => 1 })
                * MemoryActivity(seconds, container.Profile + index)) * GiB),
            4e6 + 240e6 * load, 2e6 + 60e6 * load, 20 + 400 * load, 140 + 180 * load);
    }).ToArray() : [];
    var detail = new ContainerDetail(
        new Pressure(16 * activity * activity, activity, activity, 0.1, 4 * activity, 0.2),
        new CpuThrottling(8 * activity * activity, 80 * activity, 160),
        $"0-{container.Host.Cores - 1}", 24, 512,
        new MemoryBreakdown(memory - GiB / 4, GiB / 8, GiB / 8, 0, 1024 * 1024, memory + GiB / 4,
            0.1, 0.2, 0, memory + GiB / 8, GiB / 8, 1024 * 1024, 0, 1024, 1024),
        container.Name == "auth-1" ? new MemoryEvents(0, 3, 1, 1) : new MemoryEvents(0, 0, 0, 0),
        [new IoDevice("259:0", read, write, read / 16384, write / 16384)],
        shares, gpu ? 24 * GiB : null,
        Interfaces: [new NetworkInterfaceStats("eth0", receive, transmit, receive / 1400, transmit / 1400, 300000000, 200000000, 0, 0)],
        Traffic: new TrafficSplit(transmit * 0.08, receive * 0.08, transmit * 0.92, receive * 0.92),
        Tcp: new TcpStats(0.1, 0, 0, 0, 2, 3, 12),
        LargestPaths: [new PathSize("/app/data", 6 * GiB, 1400), new PathSize("/var/log/app", 2 * GiB + GiB / 3, 48),
            new PathSize("/app/cache", GiB, 320), new PathSize("/usr/lib", 310 * 1024 * 1024, 2100)],
        FileCount: 2200, TasksStartedPerSecond: 0.1,
        GpuKernels: gpu ? [new GpuKernel("matrix_multiply", 12 + 80 * activity, 20 + 160 * activity, 240),
            new GpuKernel("vector_reduce", 4 + 30 * activity, 10 + 60 * activity, 120)] : [],
        GpuAllocationFailures: gpu ? 0 : null,
        Mounts: [new MountUsage("/app/data", "application-data", "ext4", false, 64 * GiB, 48 * GiB, 1000000, 800000, 16 * GiB)],
        DiskGrowthBytesPerHour: 24e6, DiskFreeBytes: 48 * GiB, OpenFiles: 120, OpenFilesUncounted: 0,
        Udp: new UdpStats(0, 0, 0), Conntrack: new Conntrack(32, 262144), Dns: new DnsStats(2, 0, 0.8),
        // A log rotated without telling the program: deleted, but still held open and taking space.
        DeletedFiles: container.Name == "postgres-1"
            ? [new DeletedFile(21, "postgres", "/var/log/app/service.log.1", GiB + GiB / 4)]
            : [],
        GrowingPaths: [new PathSize("/app/data", 24 * 1024 * 1024, 12), new PathSize("/var/log/app", 6 * 1024 * 1024, 0)],
        LargestFiles: [new FileSize("/app/data/application.db", 4 * GiB), new FileSize("/var/log/app/service.log", GiB + GiB / 2),
            new FileSize("/app/cache/objects.bin", GiB)],
        Logs: new LogStats(2 + 40 * activity, container.Name == "auth-1" ? 0.2 + 0.6 * activity : 0.02 * activity,
            0.1 + 0.5 * activity, 320 + 6400 * activity),
        RecentLogs: Output(container, seconds));
    return new ContainerUsage(cores / container.Host.Cores * 100, cores, container.CpuLimit,
        memory, container.MemoryLimit, read, write, receive, transmit,
        gpu ? shares.Sum(share => share.Percent) / 2 : null,
        gpu ? shares.Sum(share => share.MemoryBytes) : null, 8 * GiB, detail);
}

// The container's last lines of output; auth-1 also printed a warning and an error when it ran out of memory.
List<LogLine> Output(DemoContainer container, double seconds)
{
    var now = anchor.AddSeconds(seconds - 360);
    List<LogLine> lines =
    [
        new(now, "stdout", "info", "Request completed: GET /api/orders 200 in 12 ms"),
        new(now.AddSeconds(-2), "stdout", "info", "Request completed: GET /api/orders/1042 200 in 8 ms"),
    ];
    if (container.Name == "auth-1")
    {
        lines.Add(new(now.AddSeconds(-95), "stderr", "error", "ERROR auth-worker killed: out of memory, restarting"));
        lines.Add(new(now.AddSeconds(-97), "stderr", "warning", "WARN memory use at 94% of the limit"));
    }
    return lines;
}

List<ProcessUsage> Processes(DemoContainer container, double seconds)
{
    var usage = Usage(container, seconds);
    var name = container.Name[..container.Name.LastIndexOf('-')];
    var cpuSeconds = 1000.0;
    var readBytes = 1e8;
    var writeBytes = 2e8;
    for (var time = 0.0; time < seconds; time++)
    {
        var step = Math.Min(1, seconds - time);
        cpuSeconds += CpuCores(container, time) * step;
        readBytes += ReadRate(container, time) * step;
        writeBytes += WriteRate(container, time) * step;
    }
    return [
        new ProcessUsage(21, name, $"/usr/bin/{name} --config /app/service.conf", "app",
            usage.CpuCores, usage.MemoryBytes, 12, usage.GpuPercent, usage.GpuMemoryBytes, usage.GpuMemoryBytes,
            State: "S", PssBytes: usage.MemoryBytes, OomScore: 100, ReadBytesPerSecond: usage.IoReadBytesPerSecond,
            WriteBytesPerSecond: usage.IoWriteBytesPerSecond, OpenFiles: 80, OpenFilesLimit: 65536, StartedAt: started,
            CpuSeconds: cpuSeconds, ReadBytesTotal: (long)readBytes,
            WriteBytesTotal: (long)writeBytes, ParentPid: 1, AnonBytes: usage.MemoryBytes,
            FileBytes: 0, ShmemBytes: 0, SwapBytes: 0, Sounding: false, Ports: [container.Port], Connections: 12,
            HotThreads: [new ThreadUsage(22, "worker", usage.CpuCores * 0.6)], Capabilities: [], Unconfined: false),
        new ProcessUsage(2, "sounding-agent", "/opt/sounding/agent/sounding-agent", "app", 0.01,
            24 * 1024 * 1024, 4, null, null, null, State: "S", StartedAt: started,
            CpuSeconds: 10 + seconds * 0.01, ParentPid: 1, Sounding: true)
    ];
}

NetworkActivity Network(DemoContainer container, double seconds)
{
    string[] names = container.Name switch
    {
        "nginx-1" => [],
        "api-1" => ["postgres-1", "redis-1", "inference-1", "jobs-1"],
        "client-1" => ["nginx-1"],
        "frontend-1" => ["nginx-1"],
        "auth-1" => ["postgres-1", "redis-1", "jobs-1"],
        "jobs-1" => ["postgres-1"],
        "redis-1" => ["postgres-1"],
        "backup-1" => ["postgres-1"],
        "trainer-1" => ["postgres-1", "worker-1"],
        "inference-1" => ["redis-1"],
        "worker-1" => ["postgres-1"],
        _ => [],
    };
    var peers = containers.Where(peer => names.Contains(peer.Name)).ToArray();
    var connectionCount = peers.Length + (container.Name is "jobs-1" or "nginx-1" ? 1 : 0);
    var activity = Activity(seconds, container.Profile);
    var usage = Usage(container, seconds);
    var rate = usage.NetTransmitBytesPerSecond / (Math.Max(peers.Length, 1) * 8192);
    var sent = 2e9;
    var received = 3e9;
    for (var time = 0.0; time < seconds; time++)
    {
        var step = Math.Min(1, seconds - time);
        sent += TransmitRate(container, time) * step;
        received += ReceiveRate(container, time) * step;
    }
    var connections = peers.Select(peer => new Connection("TCP", peer.Address, peer.Port, peer.Name, 42000, false, false,
            (long)(sent / connectionCount), (long)(received / connectionCount),
            usage.NetTransmitBytesPerSecond / connectionCount, usage.NetReceiveBytesPerSecond / connectionCount,
            48 + container.Profile * 13 + peer.Profile * 3, 0,
            "ESTABLISHED", 21, container.Name, "app", HandshakeMs: 0.3 + peer.Profile * 0.04,
            RetransmitsOut: 0, RetransmitsIn: 0, HttpRequest: peer.Port is 8080 or 8081 or 3000 ? "GET /api/orders" : null,
            HttpStatus: peer.Port is 8080 or 8081 or 3000 ? 200 : null,
            HttpResponseMs: peer.Port is 8080 or 8081 or 3000 ? 2 + 35 * Math.Pow(activity, 3) : null,
            SendQueueBytes: 0, ReceiveQueueBytes: 0, Zone: "internal",
            RttMs: 0.4 + activity, DeliveryBytesPerSecond: 8e6 + 32e6 * activity, LimitedBy: "application", Problems: [])).ToList();
    if (container.Name is "jobs-1" or "nginx-1")
    {
        var internet = container.Name == "jobs-1";
        connections.Add(new Connection("TCP", internet ? "203.0.113.80" : "10.20.0.1",
            internet ? 443 : 51080, internet ? "updates.example.com" : null,
            internet ? 42000 : container.Port, internet, false,
            (long)(sent / connectionCount), (long)(received / connectionCount),
            usage.NetTransmitBytesPerSecond / connectionCount, usage.NetReceiveBytesPerSecond / connectionCount,
            180, 0, "ESTABLISHED", 21, container.Name, "app",
            HandshakeMs: internet ? 18 : 0.2, TlsName: internet ? "updates.example.com" : null,
            HttpRequest: internet ? null : "GET /", HttpStatus: internet ? null : 200,
            Zone: internet ? "internet" : "host"));
    }
    return new NetworkActivity("packets", null,
        connections,
        connections.Count, [new Listener("TCP", "0.0.0.0", container.Port, false, 21, container.Name, "app", 0, 12, 0, 0, [container.Port])],
        peers.Select(peer => new DnsLookup($"{peer.Name}.example", "NOERROR", [peer.Address], 300, 0, 0.8, [])).ToArray(),
        new Dictionary<string, int> { ["ESTABLISHED"] = 12, ["LISTEN"] = 1 }, 4096,
        [container.Address], peers.Select(peer => new RequestStats(
            peer.Port == 5432 ? "postgres" : peer.Port == 6379 ? "redis" : "http", "client", peer.Address, peer.Name, peer.Port,
            peer.Port == 5432 ? "SELECT" : peer.Port == 6379 ? "GET" : "GET /api/orders",
            rate, 0, 1 + activity * 8, 3 + activity * 20, 120, 2048, 24, container.Name)).ToArray(),
        peers.Length > 0 ? new EphemeralPorts(0.1, 32, 28232, peers[0].Address) : null,
        ["10.20.0.0/24"]);
}

double CpuCores(DemoContainer container, double seconds) =>
    container.CpuLimit * (0.28 + 0.68 * Activity(seconds, container.Profile));

double ReadRate(DemoContainer container, double seconds) =>
    (0.22 + 0.78 * Math.Pow(Activity(seconds, container.Profile, 1), 1.5)) * (container.Host.Index == 0 ? 320e6 : 85e6);

double WriteRate(DemoContainer container, double seconds) =>
    (0.20 + 0.80 * Math.Pow(Activity(seconds - 3, container.Profile, 1), 1.4)) * (container.Index == 0 ? 200e6 : 100e6);

double ReceiveRate(DemoContainer container, double seconds) =>
    (0.24 + 0.76 * Math.Pow(Activity(seconds, container.Profile, 2), 1.5)) * (container.Name == "backup-1" ? 250e6 : 95e6);

double TransmitRate(DemoContainer container, double seconds) =>
    (0.22 + 0.78 * Math.Pow(Activity(seconds - 1, container.Profile, 2), 1.4)) * (container.Name == "postgres-1" ? 210e6 : 70e6);

double Activity(double seconds, int profile, int channel = 0) =>
    (channel == 4 ? 0.75 : channel == 0 ? 0.65 : 0.55) * At(channelDemands[channel], seconds)
    + (channel == 4 ? 0.25 : channel == 0 ? 0.35 : 0.45) * At(traces[channel][profile], seconds);

double MemoryActivity(double seconds, int profile) =>
    Enumerable.Range(0, 40).Average(offset => Activity(seconds - offset, profile, 3));

static double At(double[] values, double seconds)
{
    var time = Math.Clamp(seconds + 120, 0, values.Length - 1);
    var index = (int)time;
    return values[index] + (values[Math.Min(index + 1, values.Length - 1)] - values[index]) * (time - index);
}

static double[] Trace(int seed)
{
    var random = new Random(seed);
    var values = new double[7200];
    var level = 0.68;
    var target = level;
    var remaining = 0;
    var burst = 0.0;
    for (var index = 0; index < values.Length; index++)
    {
        if (remaining-- <= 0)
        {
            remaining = random.Next(4, 15);
            target = 0.46 + random.NextDouble() * 0.44;
        }
        burst *= 0.55;
        if (random.NextDouble() < 0.14) burst += 0.05 + random.NextDouble() * 0.28;
        level += (target - level) * 0.32 + (random.NextDouble() - 0.5) * 0.08;
        values[index] = Math.Clamp(level + burst + (random.NextDouble() - 0.5) * 0.14, 0.38, 0.98);
    }
    return values;
}

// A short capture on api-1 in the format net-meter writes: a pcap file of raw IPv4 packets, each cut at 128 bytes.
// api-1 looks up jobs-1, calls its HTTP API, then reads from Redis and PostgreSQL.
static (byte[] Data, int Packets) Capture(DateTimeOffset start)
{
    const string api = "10.20.0.21", jobs = "10.20.0.25", redis = "10.20.0.11", postgres = "10.20.0.10";
    const byte Fin = 0x01, Syn = 0x02, Psh = 0x08, Ack = 0x10;
    var request = Ascii("GET /api/orders HTTP/1.1\r\nHost: jobs-1:8080\r\nUser-Agent: api/2.4\r\nAccept: application/json\r\n\r\n");
    var body = "{\"orders\":[{\"id\":1042,\"status\":\"shipped\",\"total\":129.90},{\"id\":1043,\"status\":\"open\",\"total\":18.40}]}";
    var response = Ascii($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n\r\n{body}");
    var get = Ascii("*2\r\n$3\r\nGET\r\n$10\r\norder:1042\r\n");
    var value = "{\"id\":1042,\"customer\":77,\"items\":3}";
    var reply = Ascii($"${value.Length}\r\n{value}\r\n");
    // A PostgreSQL simple query: 'Q', its length (without the 'Q'), the SQL text.
    var sql = Ascii("SELECT id, status, total FROM orders WHERE id = 1042\0");
    byte[] query = [(byte)'Q', 0, 0, 0, 0, .. sql];
    BinaryPrimitives.WriteInt32BigEndian(query.AsSpan(1), query.Length - 1);
    byte[] rows = [(byte)'T', 0, 0, 0, 0x1b, 0, 1, .. Ascii("id\0"), 0, 0, 0x40, 0x01, 0, 1, 0, 0, 0, 0x17, 0, 4, 0xff, 0xff, 0xff, 0xff, 0, 0];

    // Sequence numbers: api-1 starts at 1000 with jobs-1 (which starts at 5000), 7000 with Redis, 3000 with PostgreSQL.
    var sent = (uint)(1001 + request.Length);
    var received = (uint)(5001 + response.Length);
    var packets = new List<(int Micros, byte[] Packet)>
    {
        (0, Ip(17, "127.0.0.1", "127.0.0.11", Udp(51234, 53, Dns(0x3f21, "jobs-1.example", null)), 1)),
        (310, Ip(17, "127.0.0.11", "127.0.0.1", Udp(53, 51234, Dns(0x3f21, "jobs-1.example", jobs)), 2)),
        (540, Ip(6, api, jobs, Tcp(42010, 8080, 1000, 0, Syn, []), 3)),
        (690, Ip(6, jobs, api, Tcp(8080, 42010, 5000, 1001, Syn | Ack, []), 4)),
        (720, Ip(6, api, jobs, Tcp(42010, 8080, 1001, 5001, Ack, []), 5)),
        (800, Ip(6, api, jobs, Tcp(42010, 8080, 1001, 5001, Psh | Ack, request), 6)),
        (960, Ip(6, jobs, api, Tcp(8080, 42010, 5001, sent, Ack, []), 7)),
        (12_400, Ip(6, jobs, api, Tcp(8080, 42010, 5001, sent, Psh | Ack, response), 8)),
        (12_520, Ip(6, api, jobs, Tcp(42010, 8080, sent, received, Ack, []), 9)),
        (13_100, Ip(6, api, redis, Tcp(42020, 6379, 7000, 9000, Psh | Ack, get), 10)),
        (13_480, Ip(6, redis, api, Tcp(6379, 42020, 9000, (uint)(7000 + get.Length), Psh | Ack, reply), 11)),
        (14_050, Ip(6, api, postgres, Tcp(42030, 5432, 3000, 8000, Psh | Ack, query), 12)),
        (15_900, Ip(6, postgres, api, Tcp(5432, 42030, 8000, (uint)(3000 + query.Length), Psh | Ack, rows), 13)),
        (16_300, Ip(6, api, jobs, Tcp(42010, 8080, sent, received, Fin | Ack, []), 14)),
        (16_480, Ip(6, jobs, api, Tcp(8080, 42010, received, sent + 1, Fin | Ack, []), 15)),
        (16_510, Ip(6, api, jobs, Tcp(42010, 8080, sent + 1, received + 1, Ack, []), 16)),
    };

    using var output = new MemoryStream();
    using var writer = new BinaryWriter(output);
    writer.Write(0xa1b2c3d4u); // little-endian pcap, microsecond timestamps
    writer.Write((ushort)2);
    writer.Write((ushort)4);
    writer.Write(0);
    writer.Write(0u);
    writer.Write(128u); // snapshot length
    writer.Write(101u); // LINKTYPE_RAW: packets start at the IP header
    foreach (var (micros, packet) in packets)
    {
        var time = start.AddTicks(micros * 10L);
        var captured = Math.Min(packet.Length, 128);
        writer.Write((uint)time.ToUnixTimeSeconds());
        writer.Write((uint)(time.ToUnixTimeMilliseconds() % 1000 * 1000 + micros % 1000));
        writer.Write((uint)captured);
        writer.Write((uint)packet.Length);
        writer.Write(packet, 0, captured);
    }

    writer.Flush();
    return (output.ToArray(), packets.Count);

    static byte[] Ip(byte protocol, string source, string destination, byte[] segment, ushort id)
    {
        var header = new byte[20];
        header[0] = 0x45;
        BigEndian(header, 2, 20 + segment.Length);
        BigEndian(header, 4, id);
        header[6] = 0x40; // don't fragment
        header[8] = 64;
        header[9] = protocol;
        IPAddress.Parse(source).GetAddressBytes().CopyTo(header, 12);
        IPAddress.Parse(destination).GetAddressBytes().CopyTo(header, 16);
        BigEndian(header, 10, Checksum(header));
        byte[] pseudo = [.. header.AsSpan(12, 8), 0, protocol, (byte)(segment.Length >> 8), (byte)segment.Length];
        BigEndian(segment, protocol == 6 ? 16 : 6, Checksum([.. pseudo, .. segment]));
        return [.. header, .. segment];
    }

    static byte[] Tcp(int source, int destination, uint sequence, uint acknowledgment, byte flags, byte[] payload)
    {
        var header = new byte[20];
        BigEndian(header, 0, source);
        BigEndian(header, 2, destination);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), sequence);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(8), acknowledgment);
        header[12] = 5 << 4;
        header[13] = flags;
        BigEndian(header, 14, 64240);
        return [.. header, .. payload];
    }

    static byte[] Udp(int source, int destination, byte[] payload)
    {
        var header = new byte[8];
        BigEndian(header, 0, source);
        BigEndian(header, 2, destination);
        BigEndian(header, 4, 8 + payload.Length);
        return [.. header, .. payload];
    }

    // A DNS query for an address, or with an answer, its reply.
    static byte[] Dns(ushort id, string name, string? answer)
    {
        var message = new List<byte>();
        message.AddRange([(byte)(id >> 8), (byte)id, answer is null ? (byte)0x01 : (byte)0x81, answer is null ? (byte)0x00 : (byte)0x80,
            0, 1, 0, answer is null ? (byte)0 : (byte)1, 0, 0, 0, 0]);
        foreach (var label in name.Split('.'))
        {
            message.Add((byte)label.Length);
            message.AddRange(Encoding.ASCII.GetBytes(label));
        }
        message.AddRange([0, 0, 1, 0, 1]);
        if (answer is not null)
        {
            message.AddRange([0xc0, 0x0c, 0, 1, 0, 1, 0, 0, 0x02, 0x58, 0, 4, .. IPAddress.Parse(answer).GetAddressBytes()]);
        }
        return [.. message];
    }

    static ushort Checksum(byte[] data)
    {
        uint sum = 0;
        for (var i = 0; i < data.Length; i += 2)
        {
            sum += (uint)(data[i] << 8 | (i + 1 < data.Length ? data[i + 1] : 0));
        }
        while (sum > 0xffff) sum = (sum & 0xffff) + (sum >> 16);
        return (ushort)~sum;
    }

    static void BigEndian(byte[] buffer, int at, int value) => BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(at), (ushort)value);

    static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}

static string Json<T>(T value) => JsonSerializer.Serialize(value, typeof(T), SoundingJson.Stored);

static void Add(NpgsqlBatch batch, string sql, params object?[] values)
{
    var command = new NpgsqlBatchCommand(sql);
    foreach (var value in values)
    {
        command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
    }
    batch.BatchCommands.Add(command);
}

sealed record DemoHost(string Id, string Name, int Cores, long Memory, int Index, string[] Workloads);

sealed record DemoContainer(DemoHost Host, string Name, int Index)
{
    public string Id => $"{Host.Id}-{Name}";
    public string Address => $"10.20.0.{10 + Host.Index * 10 + Index}";
    public int Profile => Host.Index * 6 + Index;
    public double CpuLimit => Host.Cores * (Host.Index == 1
        ? Index switch { 0 => 0.12, 1 => 0.28, 2 => 0.05, 3 => 0.12, 4 => 0.10, _ => 0.14 }
        : Index switch { 0 => 0.35, 1 => 0.25, _ => 0.15 });
    public long MemoryLimit => (long)(Host.Memory * (Host.Index == 1
        ? Index switch { 0 => 0.12, 1 => 0.28, 2 => 0.05, 3 => 0.12, 4 => 0.12, _ => 0.08 }
        : Index switch { 0 => 0.28, 1 => 0.20, _ => 0.12 }));
    public int Port => Name switch { "postgres-1" => 5432, "redis-1" => 6379, "auth-1" => 8081, "frontend-1" => 3000, _ => 8080 };
}
