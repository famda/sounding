using Npgsql;

namespace Sounding.Shared;

public static class DatabaseSchema
{
    public static bool IsMissing(Exception exception) => exception is PostgresException
    {
        SqlState: PostgresErrorCodes.UndefinedTable or PostgresErrorCodes.UndefinedColumn or PostgresErrorCodes.InvalidSchemaName,
    };

    public const string NotifyChannel = "sounding_samples";

    // The dashboard asks an agent for a packet capture: the payload is the container id.
    public const string CaptureChannel = "sounding_captures";

    // Durable deadlines also recover requests whose agent or notification disappeared.
    public const string ExpireCapturesSql = """
        update captures set status = 'failed', finished_at = now()
        where container_id = @id and (
            (status = 'requested' and requested_at < now() - interval '2 minutes') or
            (status = 'running' and coalesce(started_at, requested_at) + make_interval(secs => seconds + 30) < now()))
        """;

    private const long SchemaLockKey = 0x536F_756E_6469_6E67;

    // Kept as the hosts table's comment: when it matches, the statements below aren't run again. Even an
    // "if not exists" index takes a table lock, which every agent registering would otherwise contend for.
    private const string Version = "sounding schema 4";

    private static readonly string Sql = $"""
        create table if not exists hosts (
            host_id                 text primary key,
            display_name            text not null,
            kernel                  text,
            leader_container_id     text,
            first_seen              timestamptz not null default now(),
            last_seen               timestamptz not null default now()
        );

        create table if not exists containers (
            container_id            text primary key,
            host_id                 text not null references hosts (host_id) on delete cascade,
            name                    text,
            started_at              timestamptz not null,
            last_seen               timestamptz not null,
            stopped_at              timestamptz,
            process_started_at      timestamptz,
            restarts                int not null default 0,
            exit_code               int,
            exit_signal             int,
            stop_seconds            double precision,
            processes               jsonb,
            network                 jsonb,
            latest                  jsonb
        );
        alter table hosts add column if not exists retention interval;
        -- The latest sample whole, for what is shown as it is now: the samples tables keep what is charted over time.
        alter table hosts add column if not exists latest jsonb;
        alter table hosts add column if not exists latest_ts timestamptz;
        alter table hosts add column if not exists latest_container_id text;
        alter table containers add column if not exists detail jsonb;
        create index if not exists containers_host_id on containers (host_id);

        -- Samples stored before every charted value had a column of its own (schema 3 and older) are dropped.
        do $$
        begin
            if exists (select 1 from information_schema.columns
                       where table_schema = current_schema() and table_name = 'host_samples' and column_name = 'snapshot') then
                drop table host_samples;
            end if;
            if exists (select 1 from information_schema.columns
                       where table_schema = current_schema() and table_name = 'container_samples' and column_name = 'detail') then
                drop table container_samples;
            end if;
        end
        $$;

        -- Fixed-width columns first: a row has no padding between them.
        create table if not exists host_samples (
            ts                      timestamptz not null,
            {Columns(SampleColumns.Host)},
            host_id                 text not null references hosts (host_id) on delete cascade,
            container_id            text not null,
            cores                   double precision[],
            gpus                    jsonb,
            temperatures            jsonb,
            primary key (host_id, ts)
        );

        create table if not exists host_parts (
            host_id                 text not null references hosts (host_id) on delete cascade,
            part                    text not null,
            ts                      timestamptz not null,
            container_id            text not null,
            data                    jsonb not null,
            primary key (host_id, part)
        );

        create table if not exists container_samples (
            ts                      timestamptz not null,
            cpu_percent_of_host     double precision not null,
            cpu_cores               double precision not null,
            cpu_limit_cores         double precision,
            memory_bytes            bigint not null,
            memory_limit_bytes      bigint,
            io_read_bps             double precision not null,
            io_write_bps            double precision not null,
            net_rx_bps              double precision not null,
            net_tx_bps              double precision not null,
            gpu_percent             double precision,
            gpu_memory_bytes        bigint,
            disk_used_bytes         bigint,
            {Columns(SampleColumns.ContainerDetail)},
            container_id            text not null references containers (container_id) on delete cascade,
            primary key (container_id, ts)
        );

        create table if not exists process_samples (
            container_id            text not null references containers (container_id) on delete cascade,
            ts                      timestamptz not null,
            pid                     int not null,
            started_at              timestamptz,
            name                    text not null,
            command                 text,
            user_name               text,
            state                   text,
            threads                 int not null,
            cpu_cores               double precision not null,
            cpu_seconds             double precision,
            memory_bytes            bigint not null,
            gpu_percent             double precision,
            gpu_memory_bytes        bigint,
            read_bps                double precision,
            write_bps               double precision,
            read_bytes              bigint,
            write_bytes             bigint,
            primary key (container_id, ts, pid)
        );
        alter table process_samples add column if not exists parent_pid int;

        create table if not exists events (
            host_id                 text not null references hosts (host_id) on delete cascade,
            container_id            text,
            ts                      timestamptz not null,
            kind                    text not null,
            level                   text not null,
            message                 text not null
        );
        create index if not exists events_host_ts on events (host_id, ts);

        create table if not exists captures (
            capture_id              bigint generated always as identity primary key,
            container_id            text not null references containers (container_id) on delete cascade,
            requested_at            timestamptz not null default now(),
            seconds                 int not null,
            status                  text not null,
            finished_at             timestamptz,
            packets                 int,
            data                    bytea
        );
        alter table captures add column if not exists started_at timestamptz;
        -- Keep the oldest active request when upgrading data created before concurrency was constrained.
        with active as (
            select capture_id, row_number() over (partition by container_id order by capture_id) as position
            from captures where status in ('requested', 'running')
        )
        update captures set status = 'failed', finished_at = now()
        where capture_id in (select capture_id from active where position > 1);
        create unique index if not exists captures_one_active on captures (container_id)
            where status in ('requested', 'running');
        """;

    private static string Columns(IEnumerable<SampleColumn> columns) =>
        string.Join(",\n            ", columns.Select(c => $"{c.Column,-23} double precision"));

    public static async Task EnsureAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var check = new NpgsqlCommand("select obj_description(to_regclass('hosts'), 'pg_class')", connection))
        {
            if (await check.ExecuteScalarAsync(cancellationToken) is Version)
            {
                return;
            }
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var lockCommand = new NpgsqlCommand("select pg_advisory_xact_lock(@key)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("key", SchemaLockKey);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        // Another registering agent may have migrated while this session waited for the lock.
        await using (var check = new NpgsqlCommand("select obj_description(to_regclass('hosts'), 'pg_class')", connection, transaction))
        {
            if (await check.ExecuteScalarAsync(cancellationToken) is Version)
            {
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        await using (var command = new NpgsqlCommand($"{Sql}\ncomment on table hosts is '{Version}';", connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
