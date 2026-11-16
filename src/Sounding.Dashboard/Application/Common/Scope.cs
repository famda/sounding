namespace Sounding.Dashboard.Application.Common;

public sealed record Scope(HostState? Host, ContainerState? Container)
{
    public static readonly Scope Fleet = new(null, null);

    public bool IsFleet => Host is null;

    public IReadOnlyList<HostState> Hosts(FleetState state) => Host is null ? state.Hosts : [Host];

    public bool Includes(string? containerId) => Container is null || containerId == Container.ContainerId;

    public bool IncludesHost(string? hostId) => Host is null || hostId == Host.HostId;

    public string Describe() => Container is { } c ? $"container {c.Name ?? c.ContainerId}"
        : Host is { } h ? $"host {h.DisplayName}" : "fleet";
}
