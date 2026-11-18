using System.Net;

namespace Sounding.Dashboard.Application.Network;

// Who is at the other end of a connection. Container addresses are private to a host's networks, so an address is
// matched only to a container on the observer's own host: the same address on another host is another container. When
// two reports on one host claim an address, or several containers fit a name, the peer stays unresolved rather than
// one of them being picked.
//
// Kubernetes pod and service addresses are unique across the cluster's nodes: an agent declares those ranges
// (ClusterNetworks), and a peer it reports in one of them is matched to its owner on any host.
public sealed class PeerDirectory
{
    private readonly Dictionary<(string HostId, string Address), string?> _byAddress = [];
    private readonly Dictionary<string, string?> _anyHost = [];
    // Parsed once per report: each container's list is one instance for the directory's lifetime.
    private readonly Dictionary<IReadOnlyList<string>, IPNetwork[]> _clusterNetworks = new(ReferenceEqualityComparer.Instance);

    public PeerDirectory(IReadOnlyList<ContainerNetwork> networks)
    {
        foreach (var network in networks)
        {
            if (network.HostId is not { } hostId) continue;
            foreach (var address in network.Network.Addresses ?? [])
            {
                var key = (hostId, address);
                _byAddress[key] = _byAddress.TryGetValue(key, out var existing) && existing != network.ContainerId ? null : network.ContainerId;
                _anyHost[address] = _anyHost.TryGetValue(address, out var anywhere) && anywhere != network.ContainerId ? null : network.ContainerId;
            }
        }
    }

    public string? ContainerAt(string? hostId, string address, IReadOnlyList<string>? clusterNetworks = null)
    {
        if (hostId is not null && _byAddress.TryGetValue((hostId, address), out var id)) return id;
        return InCluster(clusterNetworks, address) && _anyHost.TryGetValue(address, out var anywhere)
            ? anywhere
            : null;
    }

    public bool InCluster(IReadOnlyList<string>? clusterNetworks, string address)
    {
        if (clusterNetworks is not { Count: > 0 }) return false;
        if (!IPAddress.TryParse(address, out var ip)) return false;
        if (!_clusterNetworks.TryGetValue(clusterNetworks, out var parsed))
        {
            parsed = [.. clusterNetworks.Select(n => IPNetwork.TryParse(n, out var network) ? (IPNetwork?)network : null).OfType<IPNetwork>()];
            _clusterNetworks[clusterNetworks] = parsed;
        }

        foreach (var network in parsed)
        {
            if (network.Contains(ip)) return true;
        }

        return false;
    }

    // How to name a destination no container owns: the name the caller used, or its address. A Kubernetes Service is
    // named "<service>.<namespace>.svc", whichever way the caller wrote it: "web" in the caller's own namespace,
    // "web.shop", or "web.shop.svc.<cluster domain>". Only a name the caller resolved to a cluster address is read
    // that way; the name tells which Service was called, not which pods answered.
    public string Called(NetworkActivity caller, string address, string? name)
    {
        if (name is null) return address;
        if (caller.ClusterDomain is not { Length: > 0 } domain || !InCluster(caller.ClusterNetworks, address)) return name;

        var dns = name.AsSpan().TrimEnd('.');
        var suffix = $".svc.{domain}";
        if (dns.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return dns[..^(suffix.Length - 4)].ToString().ToLowerInvariant();
        if (dns.EndsWith(".svc", StringComparison.OrdinalIgnoreCase)) return dns.ToString().ToLowerInvariant();
        return dns.Count('.') switch
        {
            0 when caller.Namespace is { Length: > 0 } ns => $"{dns}.{ns}.svc".ToLowerInvariant(),
            1 => $"{dns}.svc".ToLowerInvariant(),
            _ => name,
        };
    }

    // The one candidate a name the observer used stands for, or null when none or several do.
    public static T? Named<T>(IEnumerable<T> candidates, Func<T, string?> name, string dnsName) where T : class
    {
        T? match = null;
        foreach (var candidate in candidates)
        {
            if (!NameMatches(name(candidate), dnsName)) continue;
            if (match is not null) return null;
            match = candidate;
        }

        return match;
    }

    // Compose names a service's containers "<project>-<service>-<n>" and resolves "<service>" (or a full container
    // name) to them, so "catalog-api" stands for "shop-catalog-api-1" but not for "shop-catalog-api-v2-1" or
    // "orders-api". The domain of a name is left out.
    //
    // A Kubernetes container ("<namespace>/<workload>[/<container>]") matches no name: a Service selects its pods by
    // label, and its name need not be the workload's.
    public static bool NameMatches(string? containerName, string dnsName)
    {
        if (containerName is null || containerName.Contains('/') || IPAddress.TryParse(dnsName, out _)) return false;
        var label = dnsName.AsSpan().TrimEnd('.');
        if (label.IndexOf('.') is > 0 and var dot) label = label[..dot];
        if (label.Length < 2) return false;

        var name = containerName.AsSpan();
        if (name.Equals(label, StringComparison.OrdinalIgnoreCase)) return true;
        var digits = name.Length;
        while (digits > 0 && char.IsAsciiDigit(name[digits - 1])) digits--;
        if (digits < name.Length && digits > 1 && name[digits - 1] is '-' or '_') name = name[..(digits - 1)];

        return name.Equals(label, StringComparison.OrdinalIgnoreCase)
            || name.Length > label.Length && name.EndsWith(label, StringComparison.OrdinalIgnoreCase) && name[^(label.Length + 1)] is '-' or '_';
    }
}
