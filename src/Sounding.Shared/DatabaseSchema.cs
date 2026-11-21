using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Sounding.Shared;

// One step of the schema. Never edit a released migration: its checksum is recorded when it is applied, and a
// database whose recorded steps differ from the dashboard's refuses to start. AgentsFrom is the oldest schema version
// an agent may be built for once this step is applied: raise it only for a change older agents can't write to.
public sealed record SchemaMigration(int Version, string Name, string Sql, int AgentsFrom = 1)
{
    public string Checksum { get; } = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Sql.ReplaceLineEndings("\n"))));
}

public enum SchemaState { Missing, Ready, AgentTooOld, DatabaseTooOld }

public readonly record struct SchemaStatus(SchemaState State, int Version);

// A database the dashboard must not run against: its schema is newer, was changed outside the migrations, or
// holds tables Sounding didn't create.
public sealed class SchemaException(string message) : Exception(message);

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

    // Sample tables are partitioned by the hour of ts: retention drops whole partitions instead of deleting rows.
    public static readonly string[] PartitionedTables = ["host_samples", "container_samples", "process_samples"];

    // The oldest schema version this agent writes to: version 1 has every table it uses, and the columns added since
    // are written only where the database has them (SampleColumn.Since).
    public const int AgentMinimum = 1;

    private const long MigrationLockKey = 0x536F_756E_6469_6E67;

    public static readonly SchemaMigration[] Migrations =
    [
        new(1, "Hosts, containers, samples, events and captures", Initial()),
    ];

    public static int Version => Migrations[^1].Version;

    // Fixed-width columns first, widest first, so rows carry no alignment padding; variable-width ones last.
    private static string Initial() => $"""
        create table hosts (
            first_seen              timestamptz not null default now(),
            last_seen               timestamptz not null default now(),
            latest_ts               timestamptz,
            host_id                 text primary key,
            display_name            text not null,
            kernel                  text,
            leader_container_id     text,
            latest_container_id     text,
            latest                  jsonb
        );

        create table containers (
            started_at              timestamptz not null,
            last_seen               timestamptz not null,
            stopped_at              timestamptz,
            process_started_at      timestamptz,
            stop_seconds            double precision,
            restarts                int not null default 0,
            exit_code               int,
            exit_signal             int,
            container_id            text primary key,
            host_id                 text not null references hosts (host_id) on delete cascade,
            name                    text,
            processes               jsonb,
            network                 jsonb,
            detail                  jsonb,
            latest                  jsonb
        );
        create index containers_host_id on containers (host_id);

        -- What one agent per host samples of the host for the others (network, GPU, host disk): the latest only.
        create table host_parts (
            ts                      timestamptz not null,
            host_id                 text not null references hosts (host_id) on delete cascade,
            part                    text not null,
            container_id            text not null,
            data                    jsonb not null,
            primary key (host_id, part)
        );

        -- No foreign keys on the samples: a check on every insert, and attaching a partition would lock the hosts and
        -- containers the agents are updating. Samples go with their partition, before their host or container does.
        create table host_samples (
            ts                      timestamptz not null,
            {Columns(SampleColumns.Host, 1)},
            host_id                 text not null,
            cores                   double precision[],
            gpus                    jsonb,
            temperatures            jsonb,
            primary key (host_id, ts)
        ) partition by range (ts);

        create table container_samples (
            ts                      timestamptz not null,
            cpu_percent_of_host     double precision not null,
            cpu_cores               double precision not null,
            cpu_limit_cores         double precision,
            io_read_bps             double precision not null,
            io_write_bps            double precision not null,
            net_rx_bps              double precision not null,
            net_tx_bps              double precision not null,
            gpu_percent             double precision,
            {Columns(SampleColumns.ContainerDetail, 1)},
            memory_bytes            bigint not null,
            memory_limit_bytes      bigint,
            gpu_memory_bytes        bigint,
            disk_used_bytes         bigint,
            container_id            text not null,
            primary key (container_id, ts)
        ) partition by range (ts);

        create table process_samples (
            ts                      timestamptz not null,
            started_at              timestamptz,
            cpu_cores               double precision not null,
            cpu_seconds             double precision,
            gpu_percent             double precision,
            read_bps                double precision,
            write_bps               double precision,
            memory_bytes            bigint not null,
            gpu_memory_bytes        bigint,
            read_bytes              bigint,
            write_bytes             bigint,
            pid                     int not null,
            parent_pid              int,
            threads                 int not null,
            container_id            text not null,
            name                    text not null,
            command                 text,
            user_name               text,
            state                   text,
            primary key (container_id, ts, pid)
        ) partition by range (ts);

        -- Rows outside every hourly partition (a clock far off, or a dashboard that hasn't run for hours) land here
        -- instead of failing; the dashboard moves them into their partitions.
        create table host_samples_default partition of host_samples default;
        create table container_samples_default partition of container_samples default;
        create table process_samples_default partition of process_samples default;

        create table events (
            ts                      timestamptz not null,
            host_id                 text not null references hosts (host_id) on delete cascade,
            container_id            text,
            kind                    text not null,
            level                   text not null,
            message                 text not null
        );
        create index events_host_ts on events (host_id, ts);

        create table captures (
            capture_id              bigint generated always as identity primary key,
            requested_at            timestamptz not null default now(),
            started_at              timestamptz,
            finished_at             timestamptz,
            seconds                 int not null,
            packets                 int,
            status                  text not null,
            container_id            text not null references containers (container_id) on delete cascade,
            data                    bytea
        );
        create index captures_container on captures (container_id, capture_id);
        create unique index captures_one_active on captures (container_id) where status in ('requested', 'running');
        """;

    private static string Columns(IEnumerable<SampleColumn> columns, int version) =>
        string.Join(",\n            ", columns.Where(c => c.Since == version).Select(c => $"{c.Column,-23} double precision"));

    // Brings the database to this build's schema, under a lock so that dashboard replicas starting together migrate
    // it once. Only the dashboard (and the documentation's demo seeder) calls this: agents never change the schema.
    public static async Task MigrateAsync(NpgsqlDataSource dataSource, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var command = new NpgsqlCommand("select pg_advisory_xact_lock(@key)", connection, transaction))
        {
            command.Parameters.AddWithValue("key", MigrationLockKey);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = new NpgsqlCommand(
            "select to_regclass('schema_migrations') is null and to_regclass('hosts') is not null", connection, transaction))
        {
            if (await command.ExecuteScalarAsync(cancellationToken) is true)
            {
                throw new SchemaException(
                    "The database has a 'hosts' table that Sounding's migrations didn't create. Point Sounding at an empty database, or drop its tables.");
            }
        }

        await using (var command = new NpgsqlCommand("""
            create table if not exists schema_migrations (
                applied_at  timestamptz not null default now(),
                version     int primary key,
                agents_from int not null,
                name        text not null,
                checksum    text not null
            )
            """, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var applied = new Dictionary<int, string>();
        await using (var command = new NpgsqlCommand("select version, checksum from schema_migrations", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                applied[reader.GetInt32(0)] = reader.GetString(1);
            }
        }

        if (applied.Keys.Where(v => v > Version).DefaultIfEmpty().Max() is > 0 and var newer)
        {
            throw new SchemaException(
                $"The database schema is at version {newer}, newer than this dashboard's ({Version}). Upgrade the dashboard.");
        }

        foreach (var migration in Migrations)
        {
            if (applied.TryGetValue(migration.Version, out var checksum))
            {
                if (checksum != migration.Checksum)
                {
                    throw new SchemaException(
                        $"Schema version {migration.Version} in the database differs from the one this dashboard applies: the schema was changed outside Sounding.");
                }

                continue;
            }

            await using var command = new NpgsqlCommand($"""
                {migration.Sql};
                insert into schema_migrations (version, agents_from, name, checksum) values (@version, @agentsFrom, @name, @checksum);
                """, connection, transaction);
            command.Parameters.AddWithValue("version", migration.Version);
            command.Parameters.AddWithValue("agentsFrom", migration.AgentsFrom);
            command.Parameters.AddWithValue("name", migration.Name);
            command.Parameters.AddWithValue("checksum", migration.Checksum);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    // Whether an agent of this build can write to the database, and the schema version it writes to.
    // Asked once per connection, and every tick while the schema is missing: so without an error each time.
    public static async Task<SchemaStatus> CheckAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand("select to_regclass('schema_migrations') is not null", connection))
        {
            if (await exists.ExecuteScalarAsync(cancellationToken) is not true) return new SchemaStatus(SchemaState.Missing, 0);
        }

        await using var command = new NpgsqlCommand("select max(version), max(agents_from) from schema_migrations", connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (reader.IsDBNull(0)) return new SchemaStatus(SchemaState.Missing, 0);
        var version = reader.GetInt32(0);
        return version < AgentMinimum ? new SchemaStatus(SchemaState.DatabaseTooOld, version)
            : Version < reader.GetInt32(1) ? new SchemaStatus(SchemaState.AgentTooOld, version)
            : new SchemaStatus(SchemaState.Ready, Math.Min(version, Version));
    }

    // What an agent needs when it connects as its own restricted role: it writes its samples, keeps its host's and
    // container's rows current and claims captures, but can't change the schema or delete anything. Partitions are
    // reached through their parent table, whose privileges are the ones checked.
    private static readonly (string Privileges, string[] Tables)[] AgentPrivileges =
    [
        ("select", ["schema_migrations"]),
        ("select, insert, update", ["hosts", "containers", "host_parts"]),
        ("select, insert", ["host_samples", "container_samples", "process_samples"]),
        ("insert", ["events"]),
        ("select, update", ["captures"]),
    ];

    public static async Task GrantAgentAsync(NpgsqlDataSource dataSource, string role, CancellationToken cancellationToken)
    {
        string schema;
        await using (var current = dataSource.CreateCommand("select current_schema()"))
        {
            schema = (string)(await current.ExecuteScalarAsync(cancellationToken))!;
        }

        // Under the migration lock: PostgreSQL fails concurrent grants on one object ("tuple concurrently updated").
        var grantee = Identifier(role);
        var sql = new StringBuilder("begin;\nselect pg_advisory_xact_lock(").Append(MigrationLockKey).Append(");\n")
            .Append("grant usage on schema ").Append(Identifier(schema)).Append(" to ").Append(grantee).Append(";\n");
        foreach (var (privileges, tables) in AgentPrivileges)
        {
            sql.Append("grant ").Append(privileges).Append(" on ").AppendJoin(", ", tables).Append(" to ").Append(grantee).Append(";\n");
        }

        sql.Append("commit;");

        await using var command = dataSource.CreateCommand(sql.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Identifier(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";
}
