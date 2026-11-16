namespace Sounding.Dashboard.Application.Common;

// The fleet as last read by FleetMonitor: current-state queries answer from it without touching the database.
public sealed class FleetSnapshot(Broadcaster<FleetView> fleet)
{
    private const int MinimumIdPrefix = 4;
    private const int MaxCandidates = 10;

    public static readonly Failure NotReady = new(ResultStatus.NotReady,
        "The fleet has not been read from the database yet. Retry in a few seconds.");

    public FleetState? Current => fleet.Latest?.State;

    // A host by id or display name, so callers can use the names people see.
    public static Failure? ResolveHost(FleetState state, string key, out HostState host)
    {
        host = null!;
        var matches = new List<HostState>();
        foreach (var candidate in state.Hosts)
        {
            if (candidate.HostId == key)
            {
                host = candidate;
                return null;
            }

            if (string.Equals(candidate.DisplayName, key, StringComparison.OrdinalIgnoreCase)) matches.Add(candidate);
        }

        if (matches.Count == 0)
        {
            foreach (var candidate in state.Hosts)
            {
                if (candidate.HostId.StartsWith(key, StringComparison.Ordinal) && key.Length >= MinimumIdPrefix
                    || candidate.DisplayName.Contains(key, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(candidate);
                }
            }
        }

        return Pick(matches, static h => h.Online, static h => h.DisplayName, static h => $"{h.DisplayName} ({h.HostId})", key, "host", HostNotFound(key), out host);
    }

    // A container by id, id prefix, exact name or a unique part of its name ("orders-api" finds "shop-orders-api-1").
    // Several matches resolve to the running one only when they are one lineage, the same name on the same host: a
    // recreated container keeps its name. Different containers that share part of a name are ambiguous.
    public static Failure? ResolveContainer(FleetState state, string key, out HostState host, out ContainerState container)
    {
        host = null!;
        container = null!;
        var exact = new List<(HostState Host, ContainerState Container)>();
        var partial = new List<(HostState Host, ContainerState Container)>();
        foreach (var h in state.Hosts)
        {
            foreach (var c in h.Containers)
            {
                if (c.ContainerId == key)
                {
                    (host, container) = (h, c);
                    return null;
                }

                if (string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase)) exact.Add((h, c));
                else if (key.Length >= MinimumIdPrefix && c.ContainerId.StartsWith(key, StringComparison.Ordinal)
                    || c.Name?.Contains(key, StringComparison.OrdinalIgnoreCase) == true)
                {
                    partial.Add((h, c));
                }
            }
        }

        var failure = Pick(exact.Count > 0 ? exact : partial, static m => m.Container.Status == ContainerStatus.Online,
            static m => m.Host.HostId + "/" + (m.Container.Name?.ToLowerInvariant() ?? m.Container.ContainerId),
            static m => $"{m.Container.Name ?? "?"} ({m.Container.ContainerId}, {m.Container.Status}, host {m.Host.DisplayName})",
            key, "container", ContainerNotFound(key), out var match);
        if (failure is null) (host, container) = match;
        return failure;
    }

    // What a query looks at: the whole fleet, one host, or one container. With required, one of the two must be given.
    public static Failure? ResolveScope(FleetState state, string? hostId, string? containerId, bool required, out Scope scope)
    {
        scope = Scope.Fleet;
        if ((Validate.OptionalId(hostId, "hostId") ?? Validate.OptionalId(containerId, "containerId")) is { } invalid) return invalid;
        if (containerId is not null)
        {
            HostState? given = null;
            if (hostId is not null && ResolveHost(state, hostId, out given) is { } unknownHost) return unknownHost;
            if (ResolveContainer(state, containerId, out var host, out var container) is { } failure) return failure;
            if (given is not null && given.HostId != host.HostId)
            {
                return Failure.Invalid("hostId", $"Container '{containerId}' is on host '{host.DisplayName}', not on '{given.DisplayName}'.");
            }

            scope = new Scope(host, container);
            return null;
        }

        if (hostId is not null)
        {
            if (ResolveHost(state, hostId, out var host) is { } failure) return failure;
            scope = new Scope(host, null);
            return null;
        }

        return required ? Failure.Invalid("target", "Give 'hostId' or 'containerId'.") : null;
    }

    public static HostState? FindHostById(FleetState state, string hostId)
    {
        foreach (var host in state.Hosts)
        {
            if (host.HostId == hostId) return host;
        }

        return null;
    }

    public static ContainerState? FindContainerById(FleetState state, string containerId)
    {
        foreach (var host in state.Hosts)
        {
            foreach (var container in host.Containers)
            {
                if (container.ContainerId == containerId) return container;
            }
        }

        return null;
    }

    public static Failure HostNotFound(string hostId) => Failure.NotFound($"No host is known by '{hostId}'.");

    public static Failure ContainerNotFound(string containerId) => Failure.NotFound($"No container is known by '{containerId}'.");

    private static Failure? Pick<T>(List<T> matches, Func<T, bool> preferred, Func<T, string> lineage, Func<T, string> describe, string key, string kind,
        Failure notFound, out T match)
    {
        match = default!;
        if (matches.Count == 0) return notFound;
        if (matches.Count == 1)
        {
            match = matches[0];
            return null;
        }

        // Preferring the running one is only right within one lineage: it is then the same thing, recreated.
        var first = lineage(matches[0]);
        var oneLineage = true;
        foreach (var candidate in matches) oneLineage &= lineage(candidate) == first;

        var preferredCount = 0;
        foreach (var candidate in matches)
        {
            if (!oneLineage || !preferred(candidate)) continue;
            match = candidate;
            preferredCount++;
        }

        if (preferredCount == 1) return null;
        return Failure.Invalid(kind == "host" ? "hostId" : "containerId",
            $"'{key}' is ambiguous: it matches {matches.Count} {kind}s: {string.Join("; ", matches.Take(MaxCandidates).Select(describe))}. "
            + "Use an id or a longer name.");
    }
}
