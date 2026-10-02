using AutoSphere.VehicleSignals.Database;

namespace AutoSphere.VehicleSignals.Codec;

/// <summary>
/// Bit-level extraction and insertion of raw signal values using DBC conventions.
/// Bit <c>n</c> is bit <c>n % 8</c> (0 = LSB) of byte <c>n / 8</c>.
/// </summary>
public static class BitCodec
{
    public static ulong Extract(ReadOnlySpan<byte> data, int startBit, int length, ByteOrder byteOrder)
    {
        Validate(length);
        ulong value = 0;
        if (byteOrder == ByteOrder.Intel)
        {
            for (var i = 0; i < length; i++)
            {
                var position = startBit + i;
                value |= (ulong)ReadBit(data, position) << i;
            }
        }
        else
        {
            var position = startBit;
            for (var i = 0; i < length; i++)
            {
                value = (value << 1) | ReadBit(data, position);
                position = NextMotorolaPosition(position);
            }
        }

        return value;
    }

    public static void Insert(Span<byte> data, int startBit, int length, ByteOrder byteOrder, ulong value)
    {
        Validate(length);
        if (byteOrder == ByteOrder.Intel)
        {
            for (var i = 0; i < length; i++)
            {
                WriteBit(data, startBit + i, (value >> i) & 1);
            }
        }
        else
        {
            var position = startBit;
            for (var i = length - 1; i >= 0; i--)
            {
                WriteBit(data, position, (value >> i) & 1);
                position = NextMotorolaPosition(position);
            }
        }
    }

    /// <summary>Returns every bit position occupied by a signal (used to validate the database for overlaps).</summary>
    public static IEnumerable<int> OccupiedBits(int startBit, int length, ByteOrder byteOrder)
    {
        var position = startBit;
        for (var i = 0; i < length; i++)
        {
            yield return byteOrder == ByteOrder.Intel ? startBit + i : position;
            position = byteOrder == ByteOrder.Intel ? position : NextMotorolaPosition(position);
        }
    }

    /// <summary>Sign-extends a raw value of <paramref name="length"/> bits (two's complement).</summary>
    public static long ToSigned(ulong raw, int length)
    {
        if (length == 64)
        {
            return unchecked((long)raw);
        }

        var signBit = 1UL << (length - 1);
        return (raw & signBit) != 0 ? unchecked((long)(raw | ~((1UL << length) - 1))) : (long)raw;
    }

    /// <summary>Converts a signed value to its <paramref name="length"/>-bit two's complement representation.</summary>
    public static ulong FromSigned(long value, int length) =>
        length == 64 ? unchecked((ulong)value) : unchecked((ulong)value) & ((1UL << length) - 1);

    public static long MaxRaw(int length, bool isSigned) => isSigned ? (1L << (length - 1)) - 1 : (long)((1UL << length) - 1);

    public static long MinRaw(int length, bool isSigned) => isSigned ? -(1L << (length - 1)) : 0;

    // DBC "sawtooth" order: inside a byte go from bit 7 down to bit 0, then continue at bit 7 of the next byte.
    private static int NextMotorolaPosition(int position) => position % 8 == 0 ? position + 15 : position - 1;

    private static ulong ReadBit(ReadOnlySpan<byte> data, int position)
    {
        var index = position / 8;
        if (index < 0 || index >= data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Bit {position} lies outside the {data.Length}-byte payload.");
        }

        return (ulong)((data[index] >> (position % 8)) & 1);
    }

    private static void WriteBit(Span<byte> data, int position, ulong bit)
    {
        var index = position / 8;
        if (index < 0 || index >= data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(position), $"Bit {position} lies outside the {data.Length}-byte payload.");
        }

        var mask = (byte)(1 << (position % 8));
        data[index] = bit != 0 ? (byte)(data[index] | mask) : (byte)(data[index] & ~mask);
    }

    private static void Validate(int length)
    {
        if (length is < 1 or > 64)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "Signals must be 1..64 bits long.");
        }
    }
}
