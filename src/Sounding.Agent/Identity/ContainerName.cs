using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace Sounding.Agent.Identity;

public static class ContainerName
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(1);

    public static string? Resolve()
    {
        try
        {
            var address = NetworkInterface.GetAllNetworkInterfaces()
                .Where(i => i.OperationalStatus == OperationalStatus.Up && i.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(i => i.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            var resolver = File.ReadLines("/etc/resolv.conf")
                .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .FirstOrDefault(f => f is ["nameserver", _, ..])?[1];
            if (address is null || resolver is null || !IPAddress.TryParse(resolver, out var server))
            {
                return null;
            }

            var name = ReverseLookup(address, server);
            return name?.Split('.')[0] is { Length: > 0 } first && first != Environment.MachineName ? first : null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or NetworkInformationException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ReverseLookup(IPAddress address, IPAddress server)
    {
        var octets = address.GetAddressBytes();
        var question = $"{octets[3]}.{octets[2]}.{octets[1]}.{octets[0]}.in-addr.arpa";
        var id = (ushort)Random.Shared.Next(ushort.MaxValue);

        var query = new List<byte> { (byte)(id >> 8), (byte)id, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0 };
        foreach (var label in question.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(Encoding.ASCII.GetBytes(label));
        }

        query.AddRange([0, 0, 12, 0, 1]);

        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.ReceiveTimeout = (int)Timeout.TotalMilliseconds;
        socket.SendTo(query.ToArray(), new IPEndPoint(server, 53));
        var reply = new byte[512];
        var length = socket.Receive(reply);
        if (length < 12 || reply[0] != (byte)(id >> 8) || reply[1] != (byte)id || (reply[3] & 0x0f) != 0)
        {
            return null;
        }

        var answers = (reply[6] << 8) | reply[7];
        var position = 12;
        SkipName(reply, ref position);
        position += 4;
        for (var i = 0; i < answers && position + 10 <= length; i++)
        {
            SkipName(reply, ref position);
            var type = (reply[position] << 8) | reply[position + 1];
            var dataLength = (reply[position + 8] << 8) | reply[position + 9];
            position += 10;
            if (type == 12)
            {
                return ReadName(reply, position);
            }

            position += dataLength;
        }

        return null;
    }

    private static void SkipName(byte[] message, ref int position)
    {
        while (position < message.Length)
        {
            var length = message[position];
            if (length == 0)
            {
                position++;
                return;
            }

            if ((length & 0xc0) == 0xc0)
            {
                position += 2;
                return;
            }

            position += length + 1;
        }
    }

    private static string ReadName(byte[] message, int position)
    {
        var labels = new List<string>();
        for (var hops = 0; position < message.Length && hops < 16;)
        {
            var length = message[position];
            if (length == 0)
            {
                break;
            }

            if ((length & 0xc0) == 0xc0)
            {
                position = ((length & 0x3f) << 8) | message[position + 1];
                hops++;
                continue;
            }

            labels.Add(Encoding.ASCII.GetString(message, position + 1, length));
            position += length + 1;
        }

        return string.Join('.', labels);
    }
}
