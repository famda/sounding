using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Sounding.Agent.Sampling;

// This network namespace's own addresses, the subnets it reaches directly and its gateways: what tells a peer
// on the same Docker network from the host, the LAN or the internet. On a Docker network the gateway is the
// host's end of it, so what the host sends (published ports, among them) arrives from there.
public sealed class LocalNetwork(string procPath)
{
    private readonly string _netPath = Path.Combine(procPath, "self/net");

    public IReadOnlyList<string> Addresses { get; private set; } = [];

    // The subnets, as CIDR ("172.19.0.0/16"): the Docker networks the container is attached to.
    public IReadOnlyList<string> Networks { get; private set; } = [];

    private List<(uint Network, uint Mask)> _subnets = [];
    private HashSet<string> _gateways = [];

    public void Refresh()
    {
        var addresses = new List<string>();
        string? last = null;
        foreach (var line in Lines("fib_trie"))
        {
            var text = line.Trim();
            if (text.StartsWith("|-- ", StringComparison.Ordinal))
            {
                last = text[4..];
            }
            else if (text.StartsWith("/32 host LOCAL", StringComparison.Ordinal) && last is not null && !last.StartsWith("127.", StringComparison.Ordinal)
                && !addresses.Contains(last))
            {
                addresses.Add(last);
            }
        }

        foreach (var line in Lines("if_inet6"))
        {
            // address, interface index, prefix length, scope (00 = global), flags, name
            if (line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [{ Length: 32 } hex, _, _, "00", ..])
            {
                addresses.Add(new IPAddress(Convert.FromHexString(hex)).ToString());
            }
        }

        var subnets = new List<(uint Network, uint Mask)>();
        var gateways = new HashSet<string>();
        foreach (var line in Lines("route").Skip(1))
        {
            // Interface, destination, gateway, flags, refcount, use, metric, mask (little-endian hex)
            if (line.Split('\t', StringSplitOptions.RemoveEmptyEntries) is not [_, var destination, var gateway, _, _, _, _, var mask, ..])
            {
                continue;
            }

            if (gateway == "00000000" && destination != "00000000")
            {
                subnets.Add((Hex(destination), Hex(mask)));
            }
            else if (gateway != "00000000")
            {
                gateways.Add(new IPAddress(Hex(gateway)).ToString());
            }
        }

        Addresses = addresses;
        Networks = subnets.Select(s => $"{new IPAddress(s.Network)}/{uint.PopCount(s.Mask)}").ToList();
        _subnets = subnets;
        _gateways = gateways;
    }

    // "internet", "host" (the gateway), "network" (a subnet this container is on) or "private" (the LAN,
    // other networks).
    public string Zone(string address) => Zone(address, TrafficSampler.IsInternet(address), V4(address));

    // The same, for an address whose parts were worked out before: whether it is on the internet, and V4 of it.
    public string Zone(string address, bool internet, uint? v4)
    {
        if (internet)
        {
            return "internet";
        }

        if (_gateways.Contains(address))
        {
            return "host";
        }

        if (v4 is { } value)
        {
            foreach (var (network, mask) in _subnets)
            {
                if ((value & mask) == network)
                {
                    return "network";
                }
            }
        }

        return "private";
    }

    // An IPv4 address as a number, in the same byte order as /proc/net/route; null for anything else.
    public static uint? V4(string address) =>
        IPAddress.TryParse(address, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? BitConverter.ToUInt32(ip.GetAddressBytes()) : null;

    private static uint Hex(string value) => uint.Parse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private string[] Lines(string file) => KernelFile.TryReadAllLines(Path.Combine(_netPath, file)) ?? [];
}
