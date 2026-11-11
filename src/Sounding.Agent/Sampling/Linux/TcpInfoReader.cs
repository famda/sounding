using System.Buffers.Binary;
using System.Net;
using System.Runtime.InteropServices;

namespace Sounding.Agent.Sampling;

// The kernel's own statistics of every TCP connection in this network namespace (what "ss -ti" shows), from one
// sock_diag netlink request per address family. Needs no capability.
public static class TcpInfoReader
{
    public readonly record struct TcpInfo(
        double? RttMs, double? DeliveryBytesPerSecond, bool AppLimited, long? BusyUs, long? ReceiveWindowLimitedUs, long? SendBufferLimitedUs,
        uint? PeerWindow, uint Unacked);

    private const int AfNetlink = 16, SockDgram = 2, SockCloexec = 0x80000, NetlinkSockDiag = 4;
    private const ushort SockDiagByFamily = 20, NlmFRequest = 1, NlmFDump = 0x300, NlmsgError = 2, NlmsgDone = 3;
    private const int InetDiagInfo = 2;

    // Every state but LISTEN, TIME_WAIT and CLOSE: the connections that carry data.
    private const uint States = (1 << 1) | (1 << 2) | (1 << 3) | (1 << 4) | (1 << 5) | (1 << 8) | (1 << 9) | (1 << 11);

    public static Dictionary<(int LocalPort, string Remote, int RemotePort), TcpInfo> Read()
    {
        var result = new Dictionary<(int, string, int), TcpInfo>();
        var fd = socket(AfNetlink, SockDgram | SockCloexec, NetlinkSockDiag);
        if (fd < 0)
        {
            return result;
        }

        try
        {
            Dump(fd, 2, result);  // AF_INET
            Dump(fd, 10, result); // AF_INET6
        }
        finally
        {
            close(fd);
        }

        return result;
    }

    // Read is only called from the sampling loop, one tick at a time: its buffers are kept between calls.
    private static readonly byte[] Request = CreateRequest();
    private static readonly byte[] Kernel = CreateKernelAddress();
    private static readonly byte[] Received = new byte[64 * 1024];

    private static byte[] CreateRequest()
    {
        var request = new byte[72];
        BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), SockDiagByFamily);
        BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), NlmFRequest | NlmFDump);
        request[17] = 6;                      // IPPROTO_TCP
        request[18] = 1 << (InetDiagInfo - 1); // ask for tcp_info
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), States);
        return request;
    }

    private static byte[] CreateKernelAddress()
    {
        var kernel = new byte[12];
        BinaryPrimitives.WriteUInt16LittleEndian(kernel, AfNetlink);
        return kernel;
    }

    private static void Dump(int fd, byte family, Dictionary<(int, string, int), TcpInfo> result)
    {
        var request = Request;
        request[16] = family;
        if (sendto(fd, request, request.Length, 0, Kernel, Kernel.Length) < 0)
        {
            return;
        }

        var buffer = Received;
        while (true)
        {
            var received = (int)recv(fd, buffer, buffer.Length, 0);
            if (received <= 0)
            {
                return;
            }

            for (var at = 0; at + 16 <= received;)
            {
                var length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(at));
                var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(at + 4));
                if (type is NlmsgDone or NlmsgError || length < 16 || at + length > received)
                {
                    return;
                }

                Parse(buffer.AsSpan(at + 16, length - 16), result);
                at += (length + 3) & ~3;
            }
        }
    }

    private static void Parse(ReadOnlySpan<byte> message, Dictionary<(int, string, int), TcpInfo> result)
    {
        if (message.Length < 72)
        {
            return;
        }

        var v4 = message[0] == 2;
        var localPort = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        var remotePort = BinaryPrimitives.ReadUInt16BigEndian(message[6..]);
        var remote = new IPAddress(message.Slice(24, v4 ? 4 : 16));
        var address = (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();

        for (var at = 72; at + 4 <= message.Length;)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(message[at..]);
            var type = BinaryPrimitives.ReadUInt16LittleEndian(message[(at + 2)..]);
            if (length < 4 || at + length > message.Length)
            {
                return;
            }

            if (type == InetDiagInfo)
            {
                result[(localPort, address, remotePort)] = Info(message.Slice(at + 4, length - 4));
            }

            at += (length + 3) & ~3;
        }
    }

    // struct tcp_info, as far as this kernel fills it.
    private static TcpInfo Info(ReadOnlySpan<byte> i) => new(
        U32(i, 68) is { } rtt ? Math.Round(rtt / 1000.0, 2) : null,
        U64(i, 160),
        i.Length > 7 && (i[7] & 1) != 0,
        U64(i, 168),
        U64(i, 176),
        U64(i, 184),
        U32(i, 228),
        U32(i, 24) ?? 0);

    private static uint? U32(ReadOnlySpan<byte> i, int offset) => i.Length >= offset + 4 ? BinaryPrimitives.ReadUInt32LittleEndian(i[offset..]) : null;

    private static long? U64(ReadOnlySpan<byte> i, int offset) => i.Length >= offset + 8 ? (long)BinaryPrimitives.ReadUInt64LittleEndian(i[offset..]) : null;

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern nint sendto(int fd, byte[] buffer, nint length, int flags, byte[] address, int addressLength);

    [DllImport("libc", SetLastError = true)]
    private static extern nint recv(int fd, byte[] buffer, nint length, int flags);

    [DllImport("libc")]
    private static extern int close(int fd);
}
