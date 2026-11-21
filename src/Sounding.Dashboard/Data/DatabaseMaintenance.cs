using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Sounding.Dashboard.Data;

// The dashboard owns the database: it creates the hourly sample partitions ahead of time, drops those past the
// retention, and deletes what has aged out of the other tables. One replica works at a time (an advisory lock);
// every time comes from the database's clock.
public sealed class DatabaseMaintenance(NpgsqlDataSource dataSource, IOptions<DashboardOptions> options, ILogger<DatabaseMaintenance> logger)
{
    private const long LockKey = 0x536F_756E_6469_6E68;
    private static readonly TimeSpan Hour = TimeSpan.FromHours(1);
    private static readonly TimeSpan Ahead = TimeSpan.FromHours(3);
    private const string Format = "yyyyMMddHH";

    private readonly TimeSpan _retention = TimeSpan.FromHours(Math.Max(1, options.Value.RetentionHours));

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        if (!await ScalarAsync<bool>(connection, $"select pg_try_advisory_lock({LockKey})", cancellationToken)) return;
        try
        {
            // A partition is attached and dropped under a short lock wait: rather than queue the agents' writes behind
            // a long dashboard query, try again next time.
            await ExecuteAsync(connection, "set lock_timeout = '2s'", cancellationToken);
            var now = await ScalarAsync<DateTime>(connection, "select now()", cancellationToken);
            var cutoff = now - _retention;
            foreach (var table in DatabaseSchema.PartitionedTables)
            {
                try
                {
                    await PartitionAsync(connection, table, now, cutoff, cancellationToken);
                }
                catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.LockNotAvailable)
                {
                    logger.LogDebug("Partitions of {Table} wait for a busy table: {Message}", table, ex.MessageText);
                }
            }

            await PruneAsync(connection, cutoff, cancellationToken);
        }
        finally
        {
            await ExecuteAsync(connection, $"select pg_advisory_unlock({LockKey}); reset lock_timeout", CancellationToken.None);
        }
    }

    private async Task PartitionAsync(NpgsqlConnection connection, string table, DateTime now, DateTime cutoff, CancellationToken cancellationToken)
    {
        var existing = new HashSet<DateTime>();
        await using (var command = new NpgsqlCommand("""
            select c.relname from pg_inherits i join pg_class c on c.oid = i.inhrelid
            where i.inhparent = to_regclass(@table)
            """, connection))
        {
            command.Parameters.AddWithValue("table", table);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (HourOf(table, reader.GetString(0)) is { } hour) existing.Add(hour);
            }
        }

        // Rows that arrived before their partition existed.
        var stray = new List<DateTime>();
        await using (var command = new NpgsqlCommand($"select distinct date_trunc('hour', ts, 'UTC') from {table}_default", connection))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken)) stray.Add(reader.GetFieldValue<DateTime>(0));
        }

        foreach (var hour in stray.Where(h => !existing.Contains(h)))
        {
            await CreateAsync(connection, table, hour, cancellationToken);
            existing.Add(hour);
            logger.LogInformation("Moved samples of {Hour:u} into a partition of {Table}", hour, table);
        }

        for (var hour = Floor(cutoff); hour <= Floor(now + Ahead); hour += Hour)
        {
            if (existing.Add(hour)) await CreateAsync(connection, table, hour, cancellationToken);
        }

        foreach (var hour in existing.Where(h => h + Hour <= cutoff).Order())
        {
            await ExecuteAsync(connection, $"drop table if exists {Partition(table, hour)}", cancellationToken);
        }
    }

    // Created detached and then attached: attaching doesn't block the parent's readers and writers, as creating a
    // partition of it in place would. The default partition is locked first, so no row of that hour reaches it between
    // moving its rows and attaching; agents lock the parent before it, and attaching needs no parent lock they hold.
    private static async Task CreateAsync(NpgsqlConnection connection, string table, DateTime hour, CancellationToken cancellationToken)
    {
        var partition = Partition(table, hour);
        var (from, to) = (Literal(hour), Literal(hour + Hour));
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = new NpgsqlCommand($"""
            lock table {table}_default in access exclusive mode;
            create table if not exists {partition} (like {table} including defaults including constraints);
            with moved as (delete from {table}_default where ts >= {from} and ts < {to} returning *) insert into {partition} select * from moved;
            alter table {table} attach partition {partition} for values from ({from}) to ({to});
            """, connection, transaction);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Partitions take care of the samples; the smaller tables are deleted from, and hosts and containers go once
    // nothing of theirs is left within the retention.
    private static async Task PruneAsync(NpgsqlConnection connection, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var batch = new NpgsqlBatch(connection);
        foreach (var sql in (string[])[
            "delete from events e using hosts h where e.host_id = h.host_id and e.ts < @cutoff",
            "delete from captures where requested_at < @cutoff",
            "delete from containers where last_seen < @cutoff",
            "delete from hosts h where h.last_seen < @cutoff and not exists (select 1 from containers c where c.host_id = h.host_id)",
        ])
        {
            var command = new NpgsqlBatchCommand(sql);
            command.Parameters.AddWithValue("cutoff", cutoff);
            batch.BatchCommands.Add(command);
        }

        await batch.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTime Floor(DateTime time) => new(time.Ticks - time.Ticks % Hour.Ticks, DateTimeKind.Utc);

    private static string Partition(string table, DateTime hour) => $"{table}_p{hour.ToString(Format, CultureInfo.InvariantCulture)}";

    private static DateTime? HourOf(string table, string partition) =>
        partition.StartsWith(table + "_p", StringComparison.Ordinal)
        && DateTime.TryParseExact(partition.AsSpan(table.Length + 2), Format, CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var hour)
            ? hour
            : null;

    private static string Literal(DateTime time) => $"'{time.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}+00'";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return reader.GetFieldValue<T>(0);
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
