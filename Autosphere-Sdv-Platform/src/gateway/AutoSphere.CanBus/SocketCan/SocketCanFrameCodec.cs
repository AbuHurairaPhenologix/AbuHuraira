using System.Buffers.Binary;

namespace AutoSphere.CanBus.SocketCan;

/// <summary>
/// Converts between <see cref="CanFrame"/> and the Linux kernel structures <c>struct can_frame</c>
/// (16 bytes) and <c>struct canfd_frame</c> (72 bytes) from <c>&lt;linux/can.h&gt;</c>.
/// Pure and platform-independent so it can be unit-tested on any OS.
/// </summary>
public static class SocketCanFrameCodec
{
    public const int CanMtu = 16;
    public const int CanFdMtu = 72;

    public const uint EffFlag = 0x8000_0000;
    public const uint RtrFlag = 0x4000_0000;
    public const uint ErrFlag = 0x2000_0000;
    public const uint EffMask = 0x1FFF_FFFF;
    public const uint SffMask = 0x0000_07FF;

    /// <summary><c>CANFD_BRS</c>: bit rate switch for the data phase.</summary>
    public const byte FdBitRateSwitch = 0x01;

    /// <summary><c>CANFD_FDF</c>: mark CAN-FD frame (Linux 5.14+, ignored by older kernels).</summary>
    public const byte FdFormatFlag = 0x04;

    /// <summary>Writes the frame in kernel layout. Returns the number of bytes to pass to <c>write(2)</c>.</summary>
    public static int Encode(CanFrame frame, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var mtu = frame.IsFd ? CanFdMtu : CanMtu;
        if (destination.Length < mtu)
        {
            throw new ArgumentException("Destination is smaller than the CAN MTU.", nameof(destination));
        }

        destination[..mtu].Clear();
        var canId = frame.IsExtended ? (frame.Id & EffMask) | EffFlag : frame.Id & SffMask;

        // can_id is in host byte order; every supported .NET Linux target (x64, arm64) is little-endian.
        BinaryPrimitives.WriteUInt32LittleEndian(destination, canId);
        destination[4] = (byte)frame.Length;
        if (frame.IsFd)
        {
            destination[5] = FdBitRateSwitch | FdFormatFlag;
        }

        frame.Data.Span.CopyTo(destination[8..]);
        return mtu;
    }

    /// <summary>Parses a buffer returned by <c>read(2)</c>. Error and remote frames are ignored.</summary>
    public static bool TryDecode(ReadOnlySpan<byte> source, DateTimeOffset timestamp, out CanFrame? frame)
    {
        frame = null;
        if (source.Length != CanMtu && source.Length != CanFdMtu)
        {
            return false;
        }

        var canId = BinaryPrimitives.ReadUInt32LittleEndian(source);
        if ((canId & (ErrFlag | RtrFlag)) != 0)
        {
            return false;
        }

        var isExtended = (canId & EffFlag) != 0;
        var isFd = source.Length == CanFdMtu;
        int length = source[4];
        if (length > (isFd ? CanFrame.MaxFdLength : CanFrame.MaxClassicLength))
        {
            return false;
        }

        try
        {
            frame = new CanFrame(
                isExtended ? canId & EffMask : canId & SffMask,
                source.Slice(8, length).ToArray(),
                timestamp,
                isExtended,
                isFd);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    /// <summary>Encodes acceptance filters as an array of <c>struct can_filter { canid_t can_id; canid_t can_mask; }</c>.</summary>
    public static byte[] EncodeFilters(IReadOnlyList<CanFilter> filters)
    {
        var buffer = new byte[filters.Count * 8];
        for (var i = 0; i < filters.Count; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(i * 8), filters[i].Id);
            BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan((i * 8) + 4), filters[i].Mask);
        }

        return buffer;
    }
}
