namespace Sounding.Dashboard.Application.Common;

// What a caller named, resolved to exact ids: nothing (everything), a host, or a container.
public sealed record TargetRef(string? HostId, string? ContainerId);

// One name that may be a container or a host, for callers that do not say which.
public sealed record ResolveTargetQuery(string? Target);

public static class ResolveTargetHandler
{
    public static async Task<Result<TargetRef>> Handle(ResolveTargetQuery query, FleetSnapshot snapshot, FleetRepository repository, CancellationToken cancellationToken)
    {
        if (query.Target is not { } key) return new TargetRef(null, null);
        if (Validate.Id(key, "target") is { } invalid) return invalid;
        if (snapshot.Current is not { } fleet) return FleetSnapshot.NotReady;

        var containerFailure = FleetSnapshot.ResolveContainer(fleet, key, out _, out var container);
        var hostFailure = FleetSnapshot.ResolveHost(fleet, key, out var host);
        var containerExact = containerFailure is null && (container.ContainerId == key || string.Equals(container.Name, key, StringComparison.OrdinalIgnoreCase));
        var hostExact = hostFailure is null && (host.HostId == key || string.Equals(host.DisplayName, key, StringComparison.OrdinalIgnoreCase));

        if (containerExact != hostExact) return containerExact ? new TargetRef(null, container.ContainerId) : new TargetRef(host.HostId, null);
        if (containerFailure is null && hostFailure is { Status: ResultStatus.NotFound }) return new TargetRef(null, container.ContainerId);
        if (hostFailure is null && containerFailure is { Status: ResultStatus.NotFound }) return new TargetRef(host.HostId, null);
        if (containerFailure is null && hostFailure is null)
        {
            return Failure.Invalid("target", $"'{key}' is ambiguous: it matches the container {container.Name ?? container.ContainerId} ({container.ContainerId}) and the host {host.DisplayName} ({host.HostId}). Use an id or a longer name.");
        }

        if (containerFailure is { Status: ResultStatus.Invalid }) return containerFailure;
        if (hostFailure is { Status: ResultStatus.Invalid }) return hostFailure;

        var (failure, scope) = await Scopes.ResolveAsync(fleet, repository, null, key, required: true, cancellationToken);
        if (failure is null) return new TargetRef(null, scope.Container!.ContainerId);
        if (failure.Status != ResultStatus.NotFound) return failure;

        (failure, scope) = await Scopes.ResolveAsync(fleet, repository, key, null, required: true, cancellationToken);
        if (failure is null) return new TargetRef(scope.Host!.HostId, null);
        return failure.Status == ResultStatus.NotFound
            ? Failure.NotFound($"No container or host is known by '{key}'.")
            : failure;
    }
}

public static class Scopes
{
    private const int MaxCandidates = 10;
    private const int LineagesRead = 100;

    // Like FleetSnapshot.ResolveScope, but a host or container no longer shown (stopped or silent beyond the hide
    // delays) is found in the database, so its events, timeline and history stay reachable by id or name. A key that
    // names nothing is NotFound, never an empty answer.
    public static async Task<(Failure? Failure, Scope Scope)> ResolveAsync(
        FleetState fleet, FleetRepository repository, string? hostId, string? containerId, bool required, CancellationToken cancellationToken)
    {
        var failure = FleetSnapshot.ResolveScope(fleet, hostId, containerId, required, out var scope);
        if (failure is not { Status: ResultStatus.NotFound }) return (failure, scope);

        if (containerId is null)
        {
            var hosts = await repository.FindHostRecordsAsync(hostId!, cancellationToken);
            return Single(hosts, hostId!, static h => h.HostId, static h => h.DisplayName, static h => h.HostId,
                static h => $"{h.DisplayName} ({h.HostId}, last seen {h.LastSeen:u})", "hostId", "host", out var host)
                is { } ambiguous ? (ambiguous, Scope.Fleet) : host is null ? (failure, Scope.Fleet) : (null, new Scope(host, null));
        }

        // The host was given and resolved, so it is the container that is not shown.
        HostState? given = null;
        if (hostId is not null && FleetSnapshot.ResolveHost(fleet, hostId, out given) is { } hostFailure) return (hostFailure, Scope.Fleet);

        // One record per lineage, read past the candidates shown, so that the uniqueness check sees every one of them.
        var records = await repository.FindContainerRecordsAsync(containerId, LineagesRead, cancellationToken);
        if (given is not null) records = [.. records.Where(r => r.HostId == given.HostId)];
        if (Single(records, containerId, static r => r.Container.ContainerId, static r => r.Container.Name, static r => r.HostId + "/" + (r.Container.Name?.ToLowerInvariant() ?? r.Container.ContainerId),
            static r => $"{r.Container.Name ?? "?"} ({r.Container.ContainerId}, {r.Container.Status}, host {r.HostName})", "containerId", "container", out var record) is { } several)
        {
            return (several, Scope.Fleet);
        }

        if (record.Container is null) return (failure, Scope.Fleet);
        var owner = FleetSnapshot.FindHostById(fleet, record.HostId)
            ?? new HostState(record.HostId, record.HostName, Online: false, record.Container.LastSeen, LeaderContainerId: null, Latest: null, Containers: []);
        return (null, new Scope(owner, record.Container));
    }

    // Records come exact matches first, then most recently seen. Exact matches win over partial ones; what remains
    // must be one lineage, or the caller has to say which.
    private static Failure? Single<T>(IReadOnlyList<T> records, string key, Func<T, string> id, Func<T, string?> name, Func<T, string> lineage,
        Func<T, string> describe, string field, string kind, out T? match)
    {
        match = default;
        if (records.Count == 0) return null;

        // An exact id names one container, whatever else matches.
        foreach (var record in records)
        {
            if (id(record) != key) continue;
            match = record;
            return null;
        }

        var candidates = new List<T>(records.Count);
        foreach (var record in records)
        {
            if (id(record) == key || string.Equals(name(record), key, StringComparison.OrdinalIgnoreCase)) candidates.Add(record);
        }

        if (candidates.Count == 0) candidates.AddRange(records);
        var first = lineage(candidates[0]);
        foreach (var candidate in candidates)
        {
            if (lineage(candidate) == first) continue;
            return Failure.Invalid(field, $"'{key}' is ambiguous: it matches {(candidates.Count >= LineagesRead ? "at least " : "")}{candidates.Count} {kind}s no longer shown: "
                + $"{string.Join("; ", candidates.Take(MaxCandidates).Select(describe))}. Use an id or a longer name.");
        }

        match = candidates[0];
        return null;
    }
}
