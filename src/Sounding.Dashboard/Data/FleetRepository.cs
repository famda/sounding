using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Dashboard.Data;

public sealed class FleetRepository(NpgsqlDataSource dataSource, IOptions<DashboardOptions> options)
{
    private const int MaxPoints = 180;

    private readonly TimeSpan _staleAfter = TimeSpan.FromSeconds(options.Value.StaleAfterSeconds);
    private readonly TimeSpan _hideHostsAfter = TimeSpan.FromMinutes(options.Value.HideOfflineHostsAfterMinutes);
    private readonly TimeSpan _hideOfflineContainersAfter = TimeSpan.FromMinutes(options.Value.HideOfflineContainersAfterMinutes);
    private readonly TimeSpan _hideStoppedContainersAfter = TimeSpan.FromMinutes(options.Value.HideStoppedContainersAfterMinutes);

    public async Task<FleetState> GetFleetAsync(CancellationToken cancellationToken)
    {
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(new NpgsqlBatchCommand("select now()"));
        batch.BatchCommands.Add(FleetCommand("""
            select h.host_id, h.display_name, h.last_seen > now() - @stale, h.last_seen, h.leader_container_id,
                   h.latest_ts, h.latest_container_id, h.latest::text
            from hosts h
            where h.last_seen > now() - @hideHosts
            order by h.display_name, h.host_id
            """));
        batch.BatchCommands.Add(FleetCommand("""
            select w.container_id, w.host_id,
                   case when w.stopped_at is not null then 'stopped'
                        when w.last_seen > now() - @stale then 'online'
                        else 'offline' end,
                   w.started_at, w.last_seen, s.ts,
                   s.cpu_percent_of_host, s.cpu_cores, s.cpu_limit_cores, s.memory_bytes, s.memory_limit_bytes,
                   s.io_read_bps, s.io_write_bps, s.net_rx_bps, s.net_tx_bps, s.gpu_percent, s.gpu_memory_bytes,
                   s.disk_used_bytes, (coalesce(w.latest, '{}'::jsonb) || w.detail)::text, w.name,
                   w.restarts, w.exit_code, w.exit_signal, w.stop_seconds
            from containers w
            left join lateral (
                select ts, cpu_percent_of_host, cpu_cores, cpu_limit_cores, memory_bytes, memory_limit_bytes,
                       io_read_bps, io_write_bps, net_rx_bps, net_tx_bps, gpu_percent, gpu_memory_bytes, disk_used_bytes
                from container_samples
                where container_id = w.container_id
                order by ts desc
                limit 1
            ) s on true
            where case when w.stopped_at is not null then w.stopped_at > now() - @hideStopped
                       else w.last_seen > now() - @hideOffline end
            order by w.host_id, w.stopped_at is not null, w.last_seen <= now() - @stale, w.container_id
            """));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

        await reader.ReadAsync(cancellationToken);
        var now = reader.GetFieldValue<DateTimeOffset>(0);

        await reader.NextResultAsync(cancellationToken);
        var hosts = new List<(string Id, string Name, bool Online, DateTimeOffset LastSeen, string? Leader, HostSample? Latest)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            HostSample? latest = reader.IsDBNull(5)
                ? null
                : new HostSample(reader.GetFieldValue<DateTimeOffset>(5), reader.GetString(6), Deserialize<HostSnapshot>(reader.GetString(7)));
            hosts.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2), reader.GetFieldValue<DateTimeOffset>(3),
                reader.IsDBNull(4) ? null : reader.GetString(4), latest));
        }

        await reader.NextResultAsync(cancellationToken);
        var containers = new List<(string HostId, ContainerState Container)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var status = reader.GetString(2);
            var sampled = status == ContainerStatus.Online && !reader.IsDBNull(5);
            var usage = sampled
                ? new ContainerUsage(
                    reader.GetDouble(6), reader.GetDouble(7), reader.IsDBNull(8) ? null : reader.GetDouble(8),
                    reader.GetInt64(9), reader.IsDBNull(10) ? null : reader.GetInt64(10),
                    reader.GetDouble(11), reader.GetDouble(12),
                    reader.GetDouble(13), reader.GetDouble(14),
                    reader.IsDBNull(15) ? null : reader.GetDouble(15),
                    reader.IsDBNull(16) ? null : reader.GetInt64(16),
                    reader.IsDBNull(17) ? null : reader.GetInt64(17),
                    reader.IsDBNull(18) ? null : Deserialize<ContainerDetail>(reader.GetString(18)))
                : null;
            containers.Add((reader.GetString(1), new ContainerState(
                reader.GetString(0),
                status,
                IsLeader: false,
                reader.GetFieldValue<DateTimeOffset>(3),
                reader.GetFieldValue<DateTimeOffset>(4),
                sampled ? reader.GetFieldValue<DateTimeOffset>(5) : null,
                usage,
                reader.IsDBNull(19) ? null : reader.GetString(19),
                reader.GetInt32(20),
                reader.IsDBNull(21) ? null : reader.GetInt32(21),
                reader.IsDBNull(22) ? null : reader.GetInt32(22),
                reader.IsDBNull(23) ? null : reader.GetDouble(23))));
        }

        return new FleetState(now, [.. hosts.Select(h => new HostState(
            h.Id,
            h.Name,
            h.Online,
            h.LastSeen,
            h.Leader,
            h.Latest,
            [.. containers
                .Where(w => w.HostId == h.Id)
                .Select(w => w.Container with { IsLeader = h.Online && w.Container.ContainerId == h.Leader && w.Container.Status == ContainerStatus.Online })]))]);
    }

    public async Task<HostHistory> GetHostHistoryAsync(string hostId, TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = Bucket(window);
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, ts, timestamptz '2000-01-01'),
                   avg(cpu_percent), avg(memory_used), avg(disk_read), avg(disk_write), avg(net_rx), avg(net_tx)
            from host_samples
            where host_id = @id and ts > now() - @window
            group by 1
            order by 1
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, h.ts, timestamptz '2000-01-01'), (c.i - 1)::int, avg(c.v)
            from host_samples h
            cross join lateral unnest(h.cores) with ordinality c(v, i)
            where h.host_id = @id and h.ts > now() - @window
            group by 1, 2
            order by 1, 2
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, h.ts, timestamptz '2000-01-01'), (d ->> 'index')::int,
                   avg((d ->> 'utilizationPercent')::float8),
                   avg((d ->> 'memoryUsagePercent')::float8)
            from host_samples h
            cross join lateral jsonb_array_elements(h.gpus) d
            where h.host_id = @id and h.ts > now() - @window
            group by 1, 2
            order by 1, 2
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, s.ts, timestamptz '2000-01-01'), s.container_id,
                   avg(s.cpu_percent_of_host), avg(s.memory_bytes)::float8
            from container_samples s
            join containers w on w.container_id = s.container_id
            where w.host_id = @id and s.ts > now() - @window
            group by 1, 2
            order by 2, 1
            """, hostId, window, bucket));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);

        var rows = new List<(DateTimeOffset T, double?[] Values)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((reader.GetFieldValue<DateTimeOffset>(0), Enumerable.Range(1, 6).Select(i => Nullable(reader, i)).ToArray()));
        }

        var index = rows.Select((row, i) => (row.T, i)).ToDictionary(x => x.T, x => x.i);
        double?[] Column(int i) => rows.Select(r => r.Values[i]).ToArray();
        double?[] Empty() => new double?[rows.Count];

        await reader.NextResultAsync(cancellationToken);
        var cores = new double?[]?[rows.Count];
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!index.TryGetValue(reader.GetFieldValue<DateTimeOffset>(0), out var i))
            {
                continue;
            }

            var core = reader.GetInt32(1);
            var values = cores[i] ??= [];
            if (values.Length <= core)
            {
                Array.Resize(ref values, core + 1);
                cores[i] = values;
            }

            values[core] = Nullable(reader, 2);
        }

        await reader.NextResultAsync(cancellationToken);
        var gpus = new SortedDictionary<int, GpuSeries>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!index.TryGetValue(reader.GetFieldValue<DateTimeOffset>(0), out var i))
            {
                continue;
            }

            var gpuIndex = reader.GetInt32(1);
            if (!gpus.TryGetValue(gpuIndex, out var gpu))
            {
                gpus[gpuIndex] = gpu = new GpuSeries(gpuIndex, Empty(), Empty());
            }

            gpu.Utilization[i] = Nullable(reader, 2);
            gpu.MemoryUsage[i] = Nullable(reader, 3);
        }

        await reader.NextResultAsync(cancellationToken);
        var containers = new Dictionary<string, ContainerSeries>();
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!index.TryGetValue(reader.GetFieldValue<DateTimeOffset>(0), out var i))
            {
                continue;
            }

            var containerId = reader.GetString(1);
            if (!containers.TryGetValue(containerId, out var container))
            {
                containers[containerId] = container = new ContainerSeries(containerId, Empty(), Empty());
            }

            container.CpuPercentOfHost[i] = Nullable(reader, 2);
            container.MemoryBytes[i] = Nullable(reader, 3);
        }

        return new HostHistory(
            (int)bucket.TotalSeconds,
            rows.Select(r => r.T.ToUnixTimeMilliseconds()).ToArray(),
            Column(0), Column(1), Column(2), Column(3), Column(4), Column(5),
            cores,
            gpus.Values.ToList(),
            containers.Values.ToList());
    }

    public async Task<FleetHistory> GetFleetHistoryAsync(TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = Bucket(window);
        await using var command = dataSource.CreateCommand("""
            select date_bin(@bucket, h.ts, timestamptz '2000-01-01'), h.host_id,
                   avg(h.cpu_percent * h.cpu_logical_cores / 100), avg(h.memory_used), avg(h.gpu_percent), avg(h.gpu_memory_used),
                   avg(h.disk_read), avg(h.disk_write), avg(h.net_rx), avg(h.net_tx), avg(h.containers_cpu_cores)
            from host_samples h
            join hosts on hosts.host_id = h.host_id and hosts.last_seen > now() - @hideHosts
            where h.ts > now() - @window
            group by 1, 2
            order by 2, 1
            """);
        command.Parameters.AddWithValue("bucket", bucket);
        command.Parameters.AddWithValue("window", window);
        command.Parameters.AddWithValue("hideHosts", _hideHostsAfter);

        var rows = new List<(DateTimeOffset T, string HostId, double?[] Values)>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                rows.Add((reader.GetFieldValue<DateTimeOffset>(0), reader.GetString(1), Enumerable.Range(2, 9).Select(i => Nullable(reader, i)).ToArray()));
            }
        }

        var times = rows.Select(r => r.T).Distinct().Order().ToArray();
        var index = times.Select((t, i) => (t, i)).ToDictionary(x => x.t, x => x.i);
        var hosts = new List<HostSeries>();
        foreach (var group in rows.GroupBy(r => r.HostId))
        {
            var columns = Enumerable.Range(0, 9).Select(_ => new double?[times.Length]).ToArray();
            foreach (var row in group)
            {
                for (var c = 0; c < columns.Length; c++)
                {
                    columns[c][index[row.T]] = row.Values[c];
                }
            }

            hosts.Add(new HostSeries(group.Key, columns[0], columns[1], columns[2], columns[3], columns[4], columns[5], columns[6], columns[7], columns[8]));
        }

        return new FleetHistory((int)bucket.TotalSeconds, times.Select(t => t.ToUnixTimeMilliseconds()).ToArray(), hosts);
    }

    public async Task<ContainerHistory> GetContainerHistoryAsync(string containerId, TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = Bucket(window);
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, ts, timestamptz '2000-01-01'),
                   avg(cpu_cores), avg(memory_bytes)::float8,
                   avg(io_read_bps), avg(io_write_bps), avg(net_rx_bps), avg(net_tx_bps),
                   avg(gpu_percent), avg(gpu_memory_bytes)::float8
            from container_samples
            where container_id = @id and ts > now() - @window
            group by 1
            order by 1
            """, containerId, window, bucket));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var rows = new List<(long T, double[] Values, double? GpuPct, double? GpuMem)>();
        while (await reader.ReadAsync(cancellationToken))
        {
            rows.Add((
                reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds(),
                Enumerable.Range(1, 6).Select(reader.GetDouble).ToArray(),
                Nullable(reader, 7),
                Nullable(reader, 8)));
        }

        double[] Column(int i) => rows.Select(r => r.Values[i]).ToArray();
        return new ContainerHistory(
            (int)bucket.TotalSeconds,
            rows.Select(r => r.T).ToArray(),
            Column(0), Column(1), Column(2), Column(3), Column(4), Column(5),
            rows.Select(r => r.GpuPct).ToArray(),
            rows.Select(r => r.GpuMem).ToArray());
    }

    public async Task<SeriesHistory> GetContainerDetailHistoryAsync(string containerId, TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = Bucket(window);
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(HistoryCommand($"""
            select date_bin(@bucket, ts, timestamptz '2000-01-01'), {string.Join(", ", ContainerDetailColumns.Select(c => $"avg({c.Sql})"))}
            from container_samples
            where container_id = @id and ts > now() - @window
            group by 1
            order by 1
            """, containerId, window, bucket));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var t = new List<long>();
        var values = ContainerDetailColumns.Select(_ => new List<double?>()).ToArray();
        while (await reader.ReadAsync(cancellationToken))
        {
            t.Add(reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds());
            for (var c = 0; c < values.Length; c++)
            {
                values[c].Add(Nullable(reader, c + 1));
            }
        }

        return new SeriesHistory(
            (int)bucket.TotalSeconds,
            t.ToArray(),
            ContainerDetailColumns.Select((c, i) => (c.Name, values[i].ToArray())).ToDictionary(x => x.Name, x => x.Item2));
    }

    public async Task<SeriesHistory> GetHostDetailHistoryAsync(string hostId, TimeSpan window, CancellationToken cancellationToken)
    {
        var bucket = Bucket(window);
        await using var batch = dataSource.CreateBatch();
        batch.BatchCommands.Add(HistoryCommand($"""
            select date_bin(@bucket, ts, timestamptz '2000-01-01'), {string.Join(", ", HostDetailColumns.Select(c => $"avg({c.Column})"))}
            from host_samples
            where host_id = @id and ts > now() - @window
            group by 1
            order by 1
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, h.ts, timestamptz '2000-01-01'), (d ->> 'index')::int,
                   avg((d ->> 'powerDrawWatts')::float8),
                   avg((d ->> 'clockSmMhz')::float8)
            from host_samples h
            cross join lateral jsonb_array_elements(h.gpus) d
            where h.host_id = @id and h.ts > now() - @window
            group by 1, 2
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand("""
            select date_bin(@bucket, h.ts, timestamptz '2000-01-01'), (t ->> 'source') || ' ' || (t ->> 'label'),
                   avg((t ->> 'celsius')::float8)
            from host_samples h
            cross join lateral jsonb_array_elements(h.temperatures) t
            where h.host_id = @id and h.ts > now() - @window
            group by 1, 2
            """, hostId, window, bucket));
        batch.BatchCommands.Add(HistoryCommand($"""
            select date_bin(@bucket, s.ts, timestamptz '2000-01-01'), s.container_id,
                   {string.Join(", ", HostContainerColumns.Select(c => $"avg({c.Sql})"))}
            from container_samples s
            join containers w on w.container_id = s.container_id
            where w.host_id = @id and s.ts > now() - @window
            group by 1, 2
            """, hostId, window, bucket));

        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        var hostColumns = HostDetailColumns.Select(c => c.Series!).ToArray();
        var t = new List<long>();
        var host = hostColumns.Select(_ => new List<double?>()).ToArray();
        while (await reader.ReadAsync(cancellationToken))
        {
            t.Add(reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds());
            for (var c = 0; c < host.Length; c++)
            {
                host[c].Add(Nullable(reader, c + 1));
            }
        }

        var columns = hostColumns.Select((name, i) => (name, host[i].ToArray())).ToDictionary(x => x.name, x => x.Item2);
        var index = t.Select((time, i) => (time, i)).ToDictionary(x => x.time, x => x.i);
        void Set(string name, long time, double? value)
        {
            if (index.TryGetValue(time, out var i))
            {
                (columns.TryGetValue(name, out var column) ? column : columns[name] = new double?[t.Count])[i] = value;
            }
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var time = reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds();
            var gpu = reader.GetInt32(1);
            Set($"power:{gpu}", time, Nullable(reader, 2));
            Set($"clock:{gpu}", time, Nullable(reader, 3));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            Set($"temp:{reader.GetString(1)}", reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds(), Nullable(reader, 2));
        }

        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var time = reader.GetFieldValue<DateTimeOffset>(0).ToUnixTimeMilliseconds();
            var containerId = reader.GetString(1);
            for (var c = 0; c < HostContainerColumns.Length; c++)
            {
                Set($"{HostContainerColumns[c].Name}:{containerId}", time, Nullable(reader, c + 2));
            }
        }

        return new SeriesHistory((int)bucket.TotalSeconds, t.ToArray(), columns);
    }

    public async Task<IReadOnlyList<ContainerProcesses>> GetHostProcessesAsync(string hostId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select container_id, processes::text
            from containers
            where host_id = @id and stopped_at is null and last_seen > now() - @stale and processes is not null
            order by container_id
            """);
        command.Parameters.AddWithValue("id", hostId);
        command.Parameters.AddWithValue("stale", _staleAfter);

        var result = new List<ContainerProcesses>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ContainerProcesses(reader.GetString(0), Deserialize<List<ProcessUsage>>(reader.GetString(1))));
        }

        return result;
    }

    public async Task<IReadOnlyList<ContainerProcessWindow>> GetHostProcessWindowAsync(string hostId, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            with totals as (
                select s.container_id, s.pid, s.started_at,
                       min(s.ts) as first_ts, max(s.ts) as last_ts,
                       max(s.cpu_seconds) as cpu_max, min(s.cpu_seconds) as cpu_min,
                       max(s.cpu_cores) as peak_cores,
                       max(s.memory_bytes) as peak_memory,
                       avg(s.gpu_percent) as gpu,
                       max(s.gpu_memory_bytes) as gpu_memory,
                       max(s.read_bytes) as read_max, min(s.read_bytes) as read_min,
                       max(s.write_bytes) as write_max, min(s.write_bytes) as write_min,
                       regr_slope(s.memory_bytes, extract(epoch from s.ts)) * 3600 as growth,
                       count(*) as samples
                from process_samples s
                join containers w on w.container_id = s.container_id
                where w.host_id = @id and s.ts > now() - @window
                group by s.container_id, s.pid, s.started_at
            )
            -- What is shown as it is now comes from each process's last sample, found by its key: sorting every
            -- sample of the window for it took most of the time over hours.
            select t.container_id, t.pid, t.started_at, l.name, l.command, l.user_name,
                   t.first_ts, t.last_ts, t.last_ts = max(t.last_ts) over (partition by t.container_id),
                   t.cpu_max - case when t.started_at > now() - @window then 0 else t.cpu_min end,
                   t.peak_cores, t.peak_memory, l.memory_bytes, t.gpu, t.gpu_memory,
                   t.read_max - case when t.started_at > now() - @window then 0 else t.read_min end,
                   t.write_max - case when t.started_at > now() - @window then 0 else t.write_min end,
                   l.threads, coalesce(t.started_at > now() - @window, false), l.parent_pid, t.growth, t.samples
            from totals t
            join process_samples l on l.container_id = t.container_id and l.ts = t.last_ts and l.pid = t.pid
            order by t.container_id
            """);
        command.Parameters.AddWithValue("id", hostId);
        command.Parameters.AddWithValue("window", window);

        var result = new Dictionary<string, List<ProcessWindow>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var firstSeen = reader.GetFieldValue<DateTimeOffset>(6);
            var lastSeen = reader.GetFieldValue<DateTimeOffset>(7);
            DateTimeOffset? started = reader.IsDBNull(2) ? null : reader.GetFieldValue<DateTimeOffset>(2);
            var peakCores = reader.GetDouble(10);
            // A process that started within the window is averaged from its start.
            var since = reader.GetBoolean(18) && started is { } s ? s : firstSeen;
            var seconds = (lastSeen - since).TotalSeconds;
            double? average = reader.IsDBNull(9) ? null : seconds >= 1 ? Math.Round(reader.GetDouble(9) / seconds, 3) : peakCores;
            peakCores = Math.Max(peakCores, average ?? 0);
            var containerId = reader.GetString(0);
            (result.TryGetValue(containerId, out var list) ? list : result[containerId] = []).Add(new ProcessWindow(
                reader.GetInt32(1),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                started,
                firstSeen,
                lastSeen,
                reader.GetBoolean(8),
                average,
                peakCores,
                reader.GetInt64(11),
                reader.GetInt64(12),
                reader.IsDBNull(13) ? null : Math.Round(reader.GetDouble(13), 1),
                reader.IsDBNull(14) ? null : reader.GetInt64(14),
                reader.IsDBNull(15) ? null : reader.GetInt64(15),
                reader.IsDBNull(16) ? null : reader.GetInt64(16),
                reader.GetInt32(17),
                reader.IsDBNull(19) ? null : reader.GetInt32(19),
                // A trend needs a few minutes of samples (one every 5 s).
                reader.IsDBNull(20) || reader.GetInt64(21) < 24 ? null : Math.Round(reader.GetDouble(20))));
        }

        return result.Select(x => new ContainerProcessWindow(x.Key, x.Value)).ToList();
    }

    public async Task<IReadOnlyList<ContainerProcesses>> GetHostProcessesAtAsync(string hostId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            with pick as (
                select distinct on (s.container_id) s.container_id, s.ts
                from process_samples s
                join containers w on w.container_id = s.container_id
                where w.host_id = @id and s.ts between @at - interval '6 seconds' and @at + interval '6 seconds'
                order by s.container_id, abs(extract(epoch from s.ts - @at))
            )
            select s.container_id, s.pid, s.name, s.command, s.user_name, s.cpu_cores, s.memory_bytes, s.threads,
                   s.gpu_percent, s.gpu_memory_bytes, s.state, s.read_bps, s.write_bps, s.started_at, s.parent_pid
            from process_samples s
            join pick p on p.container_id = s.container_id and p.ts = s.ts
            order by s.container_id
            """);
        command.Parameters.AddWithValue("id", hostId);
        command.Parameters.AddWithValue("at", at);

        var result = new Dictionary<string, List<ProcessUsage>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var containerId = reader.GetString(0);
            (result.TryGetValue(containerId, out var list) ? list : result[containerId] = []).Add(new ProcessUsage(
                reader.GetInt32(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.GetInt64(6),
                reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetDouble(8),
                reader.IsDBNull(9) ? null : reader.GetInt64(9),
                GpuMemoryPeakBytes: null,
                State: reader.IsDBNull(10) ? null : reader.GetString(10),
                ReadBytesPerSecond: reader.IsDBNull(11) ? null : reader.GetDouble(11),
                WriteBytesPerSecond: reader.IsDBNull(12) ? null : reader.GetDouble(12),
                StartedAt: reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                ParentPid: reader.IsDBNull(14) ? null : reader.GetInt32(14)));
        }

        return result.Select(x => new ContainerProcesses(x.Key, x.Value)).ToList();
    }

    // One host's containers, or every host's when hostId is null (the map).
    public async Task<IReadOnlyList<ContainerNetwork>> GetNetworkAsync(string? hostId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select container_id, network::text, host_id
            from containers
            where (@id::text is null or host_id = @id) and stopped_at is null and last_seen > now() - @stale and network is not null
            order by container_id
            """);
        command.Parameters.AddWithValue("id", NpgsqlTypes.NpgsqlDbType.Text, (object?)hostId ?? DBNull.Value);
        command.Parameters.AddWithValue("stale", _staleAfter);

        var result = new List<ContainerNetwork>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new ContainerNetwork(reader.GetString(0), Deserialize<NetworkActivity>(reader.GetString(1)), reader.GetString(2)));
        }

        return result;
    }

    public async Task<IReadOnlyList<HostEvent>> GetHostEventsAsync(string hostId, TimeSpan window, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("""
            select ts, container_id, kind, level, message
            from events
            where host_id = @id and ts > now() - @window
            order by ts desc
            limit 1000
            """);
        command.Parameters.AddWithValue("id", hostId);
        command.Parameters.AddWithValue("window", window);

        var result = new List<HostEvent>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new HostEvent(reader.GetFieldValue<DateTimeOffset>(0), reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2), reader.GetString(3), reader.GetString(4)));
        }

        return result;
    }

    // Asks the container's agent for a capture: it hears the notification on its own connection.
    public async Task<long?> RequestCaptureAsync(string containerId, int seconds, CancellationToken cancellationToken)
    {
        await ExpireCapturesAsync(containerId, cancellationToken);
        await using var batch = dataSource.CreateBatch();
        var insert = new NpgsqlBatchCommand("""
            insert into captures (container_id, seconds, status)
            select container_id, @seconds, 'requested' from containers
            where container_id = @id and stopped_at is null and last_seen > now() - @stale
              and not exists (select 1 from captures where container_id = @id and status in ('requested', 'running'))
            on conflict do nothing
            returning capture_id
            """);
        insert.Parameters.AddWithValue("id", containerId);
        insert.Parameters.AddWithValue("seconds", seconds);
        insert.Parameters.AddWithValue("stale", _staleAfter);
        batch.BatchCommands.Add(insert);
        var notify = new NpgsqlBatchCommand("select pg_notify(@channel, @id)");
        notify.Parameters.AddWithValue("channel", DatabaseSchema.CaptureChannel);
        notify.Parameters.AddWithValue("id", containerId);
        batch.BatchCommands.Add(notify);
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? reader.GetInt64(0) : null;
    }

    public async Task<IReadOnlyList<CaptureInfo>> GetCapturesAsync(string containerId, CancellationToken cancellationToken)
    {
        await ExpireCapturesAsync(containerId, cancellationToken);
        await using var command = dataSource.CreateCommand("""
            select capture_id, requested_at, seconds, status, finished_at, packets, length(data)
            from captures where container_id = @id
            order by capture_id desc
            limit 10
            """);
        command.Parameters.AddWithValue("id", containerId);
        var result = new List<CaptureInfo>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new CaptureInfo(reader.GetInt64(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetInt32(2), reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetFieldValue<DateTimeOffset>(4), reader.IsDBNull(5) ? null : reader.GetInt32(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6)));
        }

        return result;
    }

    private async Task ExpireCapturesAsync(string containerId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(DatabaseSchema.ExpireCapturesSql);
        command.Parameters.AddWithValue("id", containerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<(string ContainerId, DateTimeOffset At, byte[] Data)?> GetCaptureFileAsync(long id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("select container_id, requested_at, data from captures where capture_id = @id and data is not null");
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? (reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1), reader.GetFieldValue<byte[]>(2))
            : null;
    }

    // The series of the detail histories and the columns they average (SampleColumns: the agents store them).
    private static readonly (string Name, string Sql)[] ContainerDetailColumns =
    [
        .. SampleColumns.ContainerDetail.Select(c => (c.Series!, c.Column)),
        ("diskUsed", "disk_used_bytes::float8"),
    ];

    private static readonly SampleColumn[] HostDetailColumns = [.. SampleColumns.Host.Where(c => c.Series is not null)];

    private static readonly (string Name, string Sql)[] HostContainerColumns =
    [
        ("throttledMs", "s.throttled_ms"),
        ("read", "s.io_read_bps"),
        ("write", "s.io_write_bps"),
        ("rx", "s.net_rx_bps"),
        ("tx", "s.net_tx_bps"),
        ("toDevice", "s.to_device"),
        ("toHost", "s.to_host"),
        ("internetOut", "s.internet_out"),
        ("internetIn", "s.internet_in"),
        ("internalOut", "s.internal_out"),
        ("internalIn", "s.internal_in"),
        ("networkOut", "s.network_out"),
        ("networkIn", "s.network_in"),
        ("logLines", "s.log_lines"),
        ("logErrors", "s.log_errors"),
        ("logWarnings", "s.log_warnings"),
        ("launches", "s.launches"),
        ("tasksStarted", "s.tasks_started"),
        ("dnsLookups", "s.dns_lookups"),
        ("dnsFailures", "s.dns_failures"),
        ("dnsReplyMs", "s.dns_reply_ms"),
        ("openFiles", "s.open_files"),
        ("diskUsed", "s.disk_used_bytes::float8"),
    ];

    private static TimeSpan Bucket(TimeSpan window) => TimeSpan.FromSeconds(Math.Max(1, Math.Ceiling(window.TotalSeconds / MaxPoints)));

    private static double? Nullable(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private NpgsqlBatchCommand FleetCommand(string sql)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("stale", _staleAfter);
        command.Parameters.AddWithValue("hideHosts", _hideHostsAfter);
        command.Parameters.AddWithValue("hideOffline", _hideOfflineContainersAfter);
        command.Parameters.AddWithValue("hideStopped", _hideStoppedContainersAfter);
        return command;
    }

    private static NpgsqlBatchCommand HistoryCommand(string sql, string id, TimeSpan window, TimeSpan bucket)
    {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("window", window);
        command.Parameters.AddWithValue("bucket", bucket);
        return command;
    }

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, SoundingJson.Options) ?? throw new JsonException($"Empty {typeof(T).Name} payload");
}
