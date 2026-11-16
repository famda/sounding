using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sounding.Dashboard.Application.Network;

// Sounding's own processes in every monitored container (the agent writing to its database, its helpers): their
// traffic is monitoring overhead, not a dependency of the application, so it is left out of what agents reason on.
public static class SoundingTraffic
{
    private const string Launcher = "run";
    private const string InstallPath = "/opt/sounding/";
    private static readonly HashSet<string> Processes = new(StringComparer.Ordinal) { "sounding-agent", "net-meter", "gpu-meter" };

    public static bool IsOwn(string? process) => process is not null && Processes.Contains(process);

    // The launcher is only Sounding's when it runs from Sounding's folder: an application may have its own "run".
    public static bool IsOwn(string? process, string? commandLine) =>
        IsOwn(process) || process == Launcher && commandLine?.StartsWith(InstallPath, StringComparison.Ordinal) == true;

    public static bool IsOwn(RequestStats request) => IsOwn(request.Process);

    // Peers (aggregated over time, without a process) whose current connections all belong to Sounding.
    public static HashSet<(string Address, int Port)> OwnPeers(NetworkActivity network)
    {
        var own = new HashSet<(string, int)>();
        var other = new HashSet<(string, int)>();
        foreach (var connection in network.Connections)
        {
            (IsOwn(connection.Process) ? own : other).Add((connection.RemoteAddress, connection.RemotePort));
        }

        own.ExceptWith(other);
        return own;
    }
}

// The database Sounding writes to, as the dashboard reaches it. Every agent resolves this name to report, so a failed
// lookup of it (Docker's resolver answers some query types with SERVFAIL) is Sounding's own, not the application's.
public sealed class SoundingDatabase(string? connectionString)
{
    private readonly HashSet<string> _hosts = connectionString is null ? []
        : [.. (new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Host ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(h => h.Split(':')[0].ToLowerInvariant())];

    public bool IsOwnLookup(DnsLookup lookup) => _hosts.Contains(lookup.Name.TrimEnd('.').ToLowerInvariant());
}

// Agents write their network once a second, and one question (a diagnosis, a dependency map, the dashboard's polls)
// reads it several times: readers within the same second share one database read.
public sealed class NetworkReader(FleetRepository repository)
{
    private static readonly long Lifetime = Stopwatch.Frequency;
    private readonly ConcurrentDictionary<string, (long At, Task<IReadOnlyList<ContainerNetwork>> Read)> _reads = new();

    public Task<IReadOnlyList<ContainerNetwork>> GetAsync(string? hostId, CancellationToken cancellationToken)
    {
        var key = hostId ?? "";
        var now = Stopwatch.GetTimestamp();
        if (_reads.TryGetValue(key, out var cached) && now - cached.At < Lifetime && !cached.Read.IsFaulted)
        {
            return cached.Read.WaitAsync(cancellationToken);
        }

        foreach (var (stale, entry) in _reads)
        {
            if (now - entry.At >= Lifetime) _reads.TryRemove(stale, out _);
        }

        // Not the caller's token: the read is shared, and one caller leaving must not fail the others.
        var read = repository.GetNetworkAsync(hostId, CancellationToken.None);
        _reads[key] = (now, read);
        return read.WaitAsync(cancellationToken);
    }
}
