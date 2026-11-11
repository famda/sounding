using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Agent.Reporting;

public sealed record ContainerState(IReadOnlyList<ProcessUsage> Processes, NetworkActivity Network, ContainerDetail? Detail);

public sealed class AgentRepository(AgentConnection database, HostIdentity identity, IOptions<AgentOptions> options)
{
    private static readonly DateTimeOffset ProcessStarted = new(System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime());

    // A part written by another agent counts for three of its ticks, so one that stops sampling goes away quickly.
    private readonly TimeSpan _partsFresh = TimeSpan.FromMilliseconds(Math.Max(1000, options.Value.IntervalMilliseconds) * 3);

    private readonly TimeSpan _staleAfter = TimeSpan.FromMilliseconds(Math.Max(10_000, options.Value.IntervalMilliseconds * 5));

    // The process history (process_samples) every 5 s, besides the latest list every tick. Counted in ticks:
    // a deadline in time slips to 6 s whenever a tick fires a few milliseconds early.
    private readonly int _processHistoryTicks = (int)Math.Max(1, Math.Round(5000.0 / Math.Max(250, options.Value.IntervalMilliseconds)));

    private long _ticks;
    private string? _latestWritten;
    private bool _registered;

    // A container's sample: its figures, and out of its detail a column for each value charted over time.
    private static readonly string ContainerSampleSql = $"""
        with sample as materialized (select @detail::jsonb as detail)
        insert into container_samples (container_id, ts, cpu_percent_of_host, cpu_cores, cpu_limit_cores,
                                       memory_bytes, memory_limit_bytes, io_read_bps, io_write_bps,
                                       net_rx_bps, net_tx_bps, gpu_percent, gpu_memory_bytes, disk_used_bytes,
                                       {string.Join(", ", SampleColumns.ContainerDetail.Select(c => c.Column))})
        select @container, now(), @cpuPercent, @cpuCores, @cpuLimit::float8, @memory, @memoryLimit::bigint, @ioRead, @ioWrite,
               @netRx, @netTx, @gpuPercent::float8, @gpuMemory::bigint, @diskUsed::bigint,
               {string.Join(", ", SampleColumns.ContainerDetail.Select(c => c.Sql))}
        from sample
        on conflict do nothing
        """;

    // The host's sample. The parts other agents sampled (network, GPU, host disk) replace the snapshot's
    // placeholders while fresh, and the containers' totals are added. The whole of it is kept as the host's latest;
    // its history gets a row with a column for each value charted over time.
    private static readonly string HostSampleSql = $$"""
        with snapshot as materialized (
            select @snapshot::jsonb as j
        ),
        parts as (
            select part, data from host_parts where host_id = @host and ts > now() - @partsFresh
        ),
        latest as (
            select distinct on (s.container_id) s.cpu_percent_of_host, s.cpu_cores, s.memory_bytes
            from container_samples s
            join containers c on c.container_id = s.container_id
            where c.host_id = @host
              and c.stopped_at is null
              and c.last_seen > now() - @stale
              and s.ts > now() - @stale
            order by s.container_id, s.ts desc
        ),
        merged as materialized (
            select jsonb_set(jsonb_set(jsonb_set(jsonb_set((select j from snapshot),
                '{containers}', jsonb_build_object(
                    'count', count(*),
                    'cpuPercentOfHost', round(coalesce(sum(cpu_percent_of_host), 0)::numeric, 2),
                    'cpuCores', round(coalesce(sum(cpu_cores), 0)::numeric, 3),
                    'memoryBytes', coalesce(sum(memory_bytes), 0))),
                '{network}', coalesce((select data from parts where part = 'network'), (select j -> 'network' from snapshot))),
                '{gpu}', coalesce((select data from parts where part = 'gpu'), (select j -> 'gpu' from snapshot))),
                '{disk,volumes}', coalesce((select data from parts where part = 'disk'), (select j #> '{disk,volumes}' from snapshot))) as s
            from latest
        ),
        sample as (
            insert into host_samples (host_id, ts, container_id,
                                      {{string.Join(", ", SampleColumns.Host.Select(c => c.Column))}},
                                      cores, gpus, temperatures)
            select @host, now(), @container,
                   {{string.Join(", ", SampleColumns.Host.Select(c => c.Sql))}},
                   {{SampleColumns.HostCoresSql}},
                   {{SampleColumns.HostGpusSql}},
                   {{SampleColumns.HostTemperaturesSql}}
            from merged
            on conflict do nothing
        )
        update hosts
        set last_seen = now(), leader_container_id = @container, latest = merged.s, latest_ts = now(), latest_container_id = @container
        from merged
        where host_id = @host
        """;

    public async Task RegisterAsync(string? kernel, CancellationToken cancellationToken)
    {
        _latestWritten = null;
        await using var batch = new NpgsqlBatch(await database.GetAsync(cancellationToken));
        batch.BatchCommands.Add(Command("""
            insert into hosts (host_id, display_name, kernel, retention)
            values (@host, @name, @kernel, @retention)
            on conflict (host_id) do update
                set display_name = case when @named then excluded.display_name else hosts.display_name end,
                    kernel = coalesce(excluded.kernel, hosts.kernel),
                    retention = coalesce(hosts.retention, excluded.retention)
            """,
            ("host", identity.HostId), ("name", identity.HostDisplayName), ("named", identity.HostNameSet), ("kernel", kernel),
            ("retention", TimeSpan.FromMinutes(Math.Max(1, options.Value.RetentionMinutes)))));
        batch.BatchCommands.Add(Command("""
            insert into containers (container_id, host_id, name, started_at, last_seen, process_started_at)
            values (@container, @host, @name, now(), now(), @process)
            on conflict (container_id) do update
                set host_id = excluded.host_id,
                    name = excluded.name,
                    started_at = case when containers.process_started_at is distinct from excluded.process_started_at
                        then excluded.started_at else containers.started_at end,
                    last_seen = excluded.last_seen,
                    stopped_at = null,
                    restarts = containers.restarts
                        + case when containers.process_started_at is not null and containers.process_started_at <> excluded.process_started_at then 1 else 0 end,
                    process_started_at = excluded.process_started_at
            """,
            ("container", identity.ContainerId), ("host", identity.HostId), ("name", identity.Name), ("process", ProcessStarted)));
        if (!_registered)
        {
            batch.BatchCommands.Add(Event(new AgentEvent("lifecycle", "info", "Started")));
            batch.BatchCommands.Add(Command("""
                update captures set status = 'failed', finished_at = now()
                where container_id = @container and status = 'running'
                """, ("container", identity.ContainerId)));
        }

        batch.BatchCommands.Add(new NpgsqlBatchCommand($"listen {DatabaseSchema.CaptureChannel}"));
        await batch.ExecuteNonQueryAsync(cancellationToken);
        _registered = true;
    }

    public async Task WriteTickAsync(
        ContainerUsage? usage, ContainerState state, HostReport host, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(await database.GetAsync(cancellationToken));
        var processes = JsonSerializer.Serialize(state.Processes, SoundingJson.Stored.IReadOnlyListProcessUsage);
        // What a sample holds besides its columns is kept for the latest one only, on the container's row.
        var detail = usage?.Detail is null ? null : JsonSerializer.Serialize(PerSample(usage.Detail), SoundingJson.Stored.ContainerDetail);
        batch.BatchCommands.Add(Command("""
            update containers
            set last_seen = now(), stopped_at = null, processes = @processes::jsonb, network = @network::jsonb,
                detail = case when @sampled then @detail::jsonb else detail end
            where container_id = @container
            """,
            ("container", identity.ContainerId), ("processes", processes),
            ("network", JsonSerializer.Serialize(state.Network, SoundingJson.Stored.NetworkActivity)),
            ("sampled", usage is not null), ("detail", detail)));
        var latest = state.Detail is null ? null : JsonSerializer.Serialize(Latest(state.Detail), SoundingJson.Stored.ContainerDetail);
        if (latest != _latestWritten)
        {
            batch.BatchCommands.Add(Command(
                "update containers set latest = @latest::jsonb where container_id = @container",
                ("container", identity.ContainerId), ("latest", latest)));
        }

        if (_ticks++ % _processHistoryTicks == 0 && state.Processes.Count > 0)
        {
            batch.BatchCommands.Add(Command("""
                insert into process_samples (container_id, ts, pid, started_at, name, command, user_name, state, threads, cpu_cores, cpu_seconds,
                                             memory_bytes, gpu_percent, gpu_memory_bytes, read_bps, write_bps, read_bytes, write_bytes, parent_pid)
                select @container, now(), x.pid, x."startedAt", x.name, x."commandLine", x."user", x.state, x.threads, x."cpuCores", x."cpuSeconds",
                       x."memoryBytes", x."gpuPercent", x."gpuMemoryBytes", x."readBytesPerSecond", x."writeBytesPerSecond", x."readBytesTotal", x."writeBytesTotal",
                       x."parentPid"
                from jsonb_to_recordset(@processes::jsonb) as x(
                    pid int, "startedAt" timestamptz, name text, "commandLine" text, "user" text, state text, threads int, "cpuCores" float8,
                    "cpuSeconds" float8, "memoryBytes" bigint, "gpuPercent" float8, "gpuMemoryBytes" bigint, "readBytesPerSecond" float8,
                    "writeBytesPerSecond" float8, "readBytesTotal" bigint, "writeBytesTotal" bigint, "parentPid" int)
                on conflict do nothing
                """,
                ("container", identity.ContainerId), ("processes", processes)));
        }

        if (usage is not null)
        {
            batch.BatchCommands.Add(Command(ContainerSampleSql,
                ("container", identity.ContainerId), ("cpuPercent", usage.CpuPercentOfHost), ("cpuCores", usage.CpuCores),
                ("cpuLimit", usage.CpuLimitCores), ("memory", usage.MemoryBytes), ("memoryLimit", usage.MemoryLimitBytes),
                ("ioRead", usage.IoReadBytesPerSecond), ("ioWrite", usage.IoWriteBytesPerSecond),
                ("netRx", usage.NetReceiveBytesPerSecond), ("netTx", usage.NetTransmitBytesPerSecond),
                ("gpuPercent", usage.GpuPercent), ("gpuMemory", usage.GpuMemoryBytes), ("diskUsed", usage.DiskUsedBytes),
                ("detail", detail)));
        }

        foreach (var (part, data) in host.Parts)
        {
            batch.BatchCommands.Add(Command("""
                insert into host_parts (host_id, part, ts, container_id, data)
                values (@host, @part, now(), @container, @data::jsonb)
                on conflict (host_id, part) do update
                    set ts = excluded.ts, container_id = excluded.container_id, data = excluded.data
                """,
                ("host", identity.HostId), ("part", part), ("container", identity.ContainerId), ("data", data)));
        }

        if (host.Snapshot is { } snapshot)
        {
            batch.BatchCommands.Add(Command(HostSampleSql,
                ("host", identity.HostId), ("container", identity.ContainerId), ("stale", _staleAfter), ("partsFresh", _partsFresh),
                ("snapshot", JsonSerializer.Serialize(snapshot, SoundingJson.Stored.HostSnapshot))));
        }

        // After the containers update, in the order the schema statements lock the tables.
        foreach (var e in events)
        {
            batch.BatchCommands.Add(Event(e));
        }

        batch.BatchCommands.Add(Notify());
        await batch.ExecuteNonQueryAsync(cancellationToken);
        _latestWritten = latest;
    }

    // Each sample keeps what is charted over time; the rest is only shown as it is now, stored on the container's
    // row (latest) and merged back by the dashboard.
    private static ContainerDetail PerSample(ContainerDetail d) => d with
    {
        Cpuset = null, TasksLimit = null, MemoryEvents = null, GpuMemoryPeakBytes = null, LargestPaths = null, FileCount = null,
        GpuKernels = null, GpuAllocationFailures = null, Mounts = null, DiskGrowthBytesPerHour = null, DiskFreeBytes = null,
        OpenFilesUncounted = null, DeletedFiles = null, GrowingPaths = null, LargestFiles = null, RecentLogs = null,
    };

    private static ContainerDetail Latest(ContainerDetail d) => new(
        Pressure: null, Throttling: null, d.Cpuset, Tasks: null, d.TasksLimit, Memory: null, d.MemoryEvents, IoDevices: [], Gpus: [], d.GpuMemoryPeakBytes,
        LargestPaths: d.LargestPaths, FileCount: d.FileCount, GpuKernels: d.GpuKernels, GpuAllocationFailures: d.GpuAllocationFailures,
        Mounts: d.Mounts, DiskGrowthBytesPerHour: d.DiskGrowthBytesPerHour, DiskFreeBytes: d.DiskFreeBytes, OpenFilesUncounted: d.OpenFilesUncounted,
        DeletedFiles: d.DeletedFiles, GrowingPaths: d.GrowingPaths, LargestFiles: d.LargestFiles, RecentLogs: d.RecentLogs);

    private NpgsqlBatchCommand Event(AgentEvent e) => Command(
        "insert into events (host_id, container_id, ts, kind, level, message) values (@host, @container, now(), @kind, @level, @message)",
        ("host", identity.HostId), ("container", identity.ContainerId), ("kind", e.Kind), ("level", e.Level), ("message", e.Message));

    // The oldest capture the dashboard asked this container for, now marked as running.
    public async Task<(long Id, int Seconds)?> TakeCaptureRequestAsync(CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            with expired as ({DatabaseSchema.ExpireCapturesSql} returning capture_id)
            update captures set status = 'running', started_at = now()
            where capture_id = (
                select capture_id from captures
                where container_id = @id and status = 'requested' and requested_at >= now() - interval '2 minutes'
                order by capture_id limit 1 for update skip locked)
              and status = 'requested'
            returning capture_id, seconds
            """, await database.GetAsync(cancellationToken));
        command.Parameters.AddWithValue("id", identity.ContainerId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetInt64(0), reader.GetInt32(1)) : null;
    }

    public async Task FinishCaptureAsync(long id, ReadOnlyMemory<byte>? data, int packets, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            "update captures set status = @status, finished_at = now(), packets = @packets, data = @data where capture_id = @id and status = 'running'",
            await database.GetAsync(cancellationToken));
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("status", data is null ? "failed" : "done");
        command.Parameters.AddWithValue("packets", packets);
        command.Parameters.Add(data is { } file
            ? new NpgsqlParameter<ReadOnlyMemory<byte>>("data", NpgsqlTypes.NpgsqlDbType.Bytea) { TypedValue = file }
            : new NpgsqlParameter("data", NpgsqlTypes.NpgsqlDbType.Bytea) { Value = DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PruneAsync(TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(await database.GetAsync(cancellationToken));
        // Persist this host's policy so another host never applies its own retention to our data.
        batch.BatchCommands.Add(Command("update hosts set retention = @retention where host_id = @host",
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command(
            "delete from host_samples where host_id = @host and ts < now() - @retention",
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command("""
            delete from container_samples s using containers c
            where c.container_id = s.container_id and c.host_id = @host and s.ts < now() - @retention
            """,
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command("""
            delete from process_samples s using containers c
            where c.container_id = s.container_id and c.host_id = @host and s.ts < now() - @retention
            """,
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command(
            "delete from events where host_id = @host and ts < now() - @retention",
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command("""
            delete from captures p using containers c
            where c.container_id = p.container_id and c.host_id = @host and p.requested_at < now() - @retention
            """,
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command(
            "delete from containers where host_id = @host and last_seen < now() - @retention",
            ("host", identity.HostId), ("retention", retention)));
        batch.BatchCommands.Add(Command("""
            delete from hosts h
            where h.host_id <> @host and h.retention is not null and h.last_seen < now() - h.retention
              and not exists (select 1 from containers c where c.host_id = h.host_id and c.last_seen >= now() - h.retention)
            """, ("host", identity.HostId)));
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkStoppedAsync(ExitStatus? exit, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(await database.GetAsync(cancellationToken));
        batch.BatchCommands.Add(Command(
            exit is null
                ? "update containers set stopped_at = now() where container_id = @container"
                : "update containers set stopped_at = now(), exit_code = @code, exit_signal = @signal, stop_seconds = @stop where container_id = @container",
            ("container", identity.ContainerId), ("code", exit?.Code), ("signal", exit?.Signal), ("stop", exit?.StopSeconds)));
        batch.BatchCommands.Add(Event(new AgentEvent("lifecycle", exit is { Code: not 0 and not null } or { Signal: not null } ? "warning" : "info",
            exit is null ? "Stopped" : exit.Signal is { } signal ? $"Stopped by signal {signal}" : $"Exited with code {exit.Code}")));
        batch.BatchCommands.Add(Command(
            "update hosts set leader_container_id = null where host_id = @host and leader_container_id = @container",
            ("host", identity.HostId), ("container", identity.ContainerId)));
        batch.BatchCommands.Add(Notify());
        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private NpgsqlBatchCommand Notify() =>
        Command("select pg_notify(@channel, @host)", ("channel", DatabaseSchema.NotifyChannel), ("host", identity.HostId));

    private static NpgsqlBatchCommand Command(string sql, params (string Name, object? Value)[] parameters)
    {
        var command = new NpgsqlBatchCommand(sql);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}
