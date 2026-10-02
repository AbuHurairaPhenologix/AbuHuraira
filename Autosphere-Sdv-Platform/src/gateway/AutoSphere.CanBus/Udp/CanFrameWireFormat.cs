using System.Buffers.Binary;

namespace AutoSphere.CanBus.Udp;

/// <summary>
/// Compact binary encoding of a CAN frame for the UDP bridge.
/// Layout: <c>'A' 'S'</c> · version (1) · flags (bit0 extended, bit1 FD) · id (uint32 BE) · length (1) · data.
/// </summary>
public static class CanFrameWireFormat
{
    public const int HeaderLength = 9;
    private const byte Version = 1;
    private const byte ExtendedFlag = 0x01;
    private const byte FdFlag = 0x02;

    public static int GetEncodedLength(CanFrame frame) => HeaderLength + frame.Length;

    public static int Encode(CanFrame frame, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var length = GetEncodedLength(frame);
        if (destination.Length < length)
        {
            throw new ArgumentException("Destination buffer is too small.", nameof(destination));
        }

        destination[0] = (byte)'A';
        destination[1] = (byte)'S';
        destination[2] = Version;
        destination[3] = (byte)((frame.IsExtended ? ExtendedFlag : 0) | (frame.IsFd ? FdFlag : 0));
        BinaryPrimitives.WriteUInt32BigEndian(destination[4..], frame.Id);
        destination[8] = (byte)frame.Length;
        frame.Data.Span.CopyTo(destination[HeaderLength..]);
        return length;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, DateTimeOffset timestamp, out CanFrame? frame)
    {
        frame = null;
        if (source.Length < HeaderLength || source[0] != 'A' || source[1] != 'S' || source[2] != Version)
        {
            return false;
        }

        var flags = source[3];
        var id = BinaryPrimitives.ReadUInt32BigEndian(source[4..]);
        int length = source[8];
        if (source.Length != HeaderLength + length)
        {
            return false;
        }

        try
        {
            frame = new CanFrame(id, source.Slice(HeaderLength, length).ToArray(), timestamp,
                isExtended: (flags & ExtendedFlag) != 0, isFd: (flags & FdFlag) != 0);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
