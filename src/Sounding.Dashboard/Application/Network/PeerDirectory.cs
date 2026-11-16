using System.Net;

namespace Sounding.Dashboard.Application.Network;

// Who is at the other end of a connection. Container addresses are private to a host's networks, so an address is
// matched only to a container on the observer's own host: the same address on another host is another container. When
// two reports on one host claim an address, or several containers fit a name, the peer stays unresolved rather than
// one of them being picked.
public sealed class PeerDirectory
{
    private readonly Dictionary<(string HostId, string Address), string?> _byAddress = [];

    public PeerDirectory(IReadOnlyList<ContainerNetwork> networks)
    {
        foreach (var network in networks)
        {
            if (network.HostId is not { } hostId) continue;
            foreach (var address in network.Network.Addresses ?? [])
            {
                var key = (hostId, address);
                _byAddress[key] = _byAddress.TryGetValue(key, out var existing) && existing != network.ContainerId ? null : network.ContainerId;
            }
        }
    }

    public string? ContainerAt(string? hostId, string address) =>
        hostId is not null && _byAddress.TryGetValue((hostId, address), out var id) ? id : null;

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
    public static bool NameMatches(string? containerName, string dnsName)
    {
        if (containerName is null || IPAddress.TryParse(dnsName, out _)) return false;
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
