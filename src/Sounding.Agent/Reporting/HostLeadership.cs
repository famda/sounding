using Npgsql;

namespace Sounding.Agent.Reporting;

// What one agent per host samples. Sampler is open to every agent; the others only to agents that can see that
// part of the host (a /host/sys or /host/proc mount, nvidia-smi, the host disk mount).
public enum HostRole { Sampler, Network, Gpu, Disk }

// One session-level advisory lock per role and host, on the agent's connection: when the agent stops or its
// session ends, the locks go and another agent takes the role on its next tick.
public sealed class HostLeadership(AgentConnection database, HostIdentity identity, ILogger<HostLeadership> logger)
{
    private NpgsqlConnection? _session;
    private readonly HashSet<HostRole> _held = [];

    public async Task<IReadOnlySet<HostRole>> RefreshAsync(IReadOnlyCollection<HostRole> wanted, CancellationToken cancellationToken)
    {
        var connection = await database.GetAsync(cancellationToken);
        if (!ReferenceEquals(connection, _session))
        {
            // A new session holds none of the old one's locks.
            _session = connection;
            _held.Clear();
        }

        var release = _held.Where(r => !wanted.Contains(r)).ToArray();
        var acquire = wanted.Where(r => !_held.Contains(r)).ToArray();
        if (release.Length == 0 && acquire.Length == 0) return _held.ToHashSet();

        // One round trip for all changes; held locks must not be acquired again (locks are reentrant).
        await using var batch = new NpgsqlBatch(connection);
        foreach (var role in release) batch.BatchCommands.Add(LockCommand("pg_advisory_unlock", role));
        foreach (var role in acquire) batch.BatchCommands.Add(LockCommand("pg_try_advisory_lock", role));
        var acquired = new List<HostRole>();
        try
        {
            await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
            foreach (var role in release.Concat(acquire))
            {
                await reader.ReadAsync(cancellationToken);
                if (reader.GetBoolean(0) && acquire.Contains(role)) acquired.Add(role);
                await reader.NextResultAsync(cancellationToken);
            }
        }
        catch
        {
            // Session locks survive transaction rollback. Discard an uncertain session before retrying.
            await database.DisposeAsync();
            throw;
        }
        _held.ExceptWith(release);
        foreach (var role in acquired)
        {
            _held.Add(role);
            logger.LogInformation("Container {ContainerId} now samples the {Role} of host {HostId}", identity.ContainerId, role, identity.HostId);
        }

        return _held.ToHashSet();
    }

    private NpgsqlBatchCommand LockCommand(string function, HostRole role)
    {
        var command = new NpgsqlBatchCommand($"select {function}(hashtextextended(@key, 0))");
        command.Parameters.AddWithValue("key", $"host-{role}:{identity.HostId}".ToLowerInvariant());
        return command;
    }
}
