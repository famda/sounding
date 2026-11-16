namespace Sounding.Dashboard.Application.Common;

// Names for container ids as a reader should see them. A container recreated under the same name on its host (Compose
// up, a deploy) leaves its old id in events and history: labelling it as replaced stops a reader from taking an old
// stop for the state of the container running now. The same name on another host is another container.
public static class ContainerLabels
{
    public const string ReplacedSuffix = " (an earlier container: a newer one with this name is running)";
    public const string GoneSuffix = " (no longer shown)";

    public static async Task<Dictionary<string, (string Label, bool Replaced)>> ResolveAsync(
        FleetState fleet, FleetRepository repository, IEnumerable<string> containerIds, CancellationToken cancellationToken)
    {
        var labels = new Dictionary<string, (string, bool)>();
        var shown = new Dictionary<string, string?>(StringComparer.Ordinal);
        var running = new HashSet<(string HostId, string Name)>();
        foreach (var host in fleet.Hosts)
        {
            foreach (var container in host.Containers)
            {
                shown[container.ContainerId] = container.Name;
                if (container.Status == ContainerStatus.Online && container.Name is { } name) running.Add((host.HostId, name));
            }
        }

        var unknown = new List<string>();
        foreach (var id in containerIds)
        {
            if (labels.ContainsKey(id)) continue;
            if (shown.TryGetValue(id, out var name)) labels[id] = (name ?? id, false);
            else if (!unknown.Contains(id)) unknown.Add(id);
        }

        foreach (var (id, (hostId, name)) in await repository.GetContainerNamesAsync(unknown, cancellationToken))
        {
            var replaced = name is not null && running.Contains((hostId, name));
            labels[id] = ((name ?? id) + (replaced ? ReplacedSuffix : GoneSuffix), replaced);
        }

        foreach (var id in unknown) labels.TryAdd(id, (id + GoneSuffix, false));
        return labels;
    }
}
