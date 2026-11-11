using System.Buffers.Binary;
using System.Globalization;

namespace Sounding.Agent.Sampling;

// A capture the dashboard asked for: net-meter's "P" lines (the first 128 bytes of each packet) as a pcap file
// Wireshark opens. The packets are raw IP: net-meter's socket has no link-layer header.
public sealed class PacketCapture
{
    private const int Snap = 128;
    private const uint LinkTypeRaw = 101;

    private readonly MemoryStream _file = new();

    public PacketCapture()
    {
        Span<byte> header = stackalloc byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 0xa1b2c3d4);
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header[6..], 4);
        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], Snap);
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], LinkTypeRaw);
        _file.Write(header);
    }

    public int Packets { get; private set; }

    // P <seconds> <microseconds> <length> <hex>
    public void Add(string[] f)
    {
        if (f is not ["P", var seconds, var micros, var length, var hex])
        {
            return;
        }

        // At most Snap bytes, decoded on the stack; anything else as before.
        Span<byte> data = hex.Length <= Snap * 2 ? stackalloc byte[hex.Length / 2] : new byte[hex.Length / 2];
        if (Convert.FromHexString(hex, data, out _, out var decoded) != System.Buffers.OperationStatus.Done || decoded != data.Length)
        {
            throw new FormatException("A captured packet isn't in hexadecimal");
        }

        Span<byte> record = stackalloc byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(record, uint.Parse(seconds, CultureInfo.InvariantCulture));
        BinaryPrimitives.WriteUInt32LittleEndian(record[4..], uint.Parse(micros, CultureInfo.InvariantCulture));
        BinaryPrimitives.WriteUInt32LittleEndian(record[8..], (uint)data.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(record[12..], uint.Parse(length, CultureInfo.InvariantCulture));
        _file.Write(record);
        _file.Write(data);
        Packets++;
    }

    // The file as it stands in the stream's own buffer, without a copy of it.
    public ReadOnlyMemory<byte> Data => _file.GetBuffer().AsMemory(0, (int)_file.Length);
}
