using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;

namespace AutoSphere.UnitTests.VehicleSignals;

public sealed class BitCodecTests
{
    [Fact]
    public void Intel_signal_spanning_two_bytes_is_little_endian()
    {
        byte[] data = [0x00, 0x00, 0x34, 0x12, 0, 0, 0, 0];

        var raw = BitCodec.Extract(data, startBit: 16, length: 16, ByteOrder.Intel);

        Assert.Equal(0x1234UL, raw);
    }

    [Fact]
    public void Motorola_signal_uses_dbc_msb_start_bit_and_sawtooth_order()
    {
        // DBC convention: a 16-bit big-endian signal starting at bit 23 occupies byte 2 (MSB) and byte 3 (LSB).
        byte[] data = [0x00, 0x00, 0x12, 0x34, 0, 0, 0, 0];

        var raw = BitCodec.Extract(data, startBit: 23, length: 16, ByteOrder.Motorola);

        Assert.Equal(0x1234UL, raw);
    }

    [Theory]
    [InlineData(12, 3, ByteOrder.Intel, 5UL)]
    [InlineData(16, 10, ByteOrder.Intel, 784UL)]
    [InlineData(23, 16, ByteOrder.Motorola, 4002UL)]
    [InlineData(39, 16, ByteOrder.Motorola, 0xFFFFUL)]
    [InlineData(5, 4, ByteOrder.Motorola, 0xAUL)]
    public void Insert_then_extract_round_trips(int startBit, int length, ByteOrder order, ulong value)
    {
        var data = new byte[8];

        BitCodec.Insert(data, startBit, length, order, value);

        Assert.Equal(value, BitCodec.Extract(data, startBit, length, order));
    }

    [Fact]
    public void Insert_does_not_touch_neighbouring_bits()
    {
        var data = Enumerable.Repeat((byte)0xFF, 8).ToArray();

        BitCodec.Insert(data, startBit: 12, length: 3, ByteOrder.Intel, 0);

        Assert.Equal(0b1000_1111, data[1]);
        Assert.All(data.Where((_, i) => i != 1), b => Assert.Equal(0xFF, b));
    }

    [Theory]
    [InlineData(0xFFFFUL, 16, -1L)]
    [InlineData(0x8000UL, 16, -32768L)]
    [InlineData(0x7FFFUL, 16, 32767L)]
    [InlineData(0b100UL, 3, -4L)]
    public void Two_complement_sign_extension(ulong raw, int length, long expected)
    {
        Assert.Equal(expected, BitCodec.ToSigned(raw, length));
        Assert.Equal(raw, BitCodec.FromSigned(expected, length));
    }

    [Fact]
    public void Reading_beyond_payload_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => BitCodec.Extract(new byte[2], 10, 8, ByteOrder.Intel));
    }
}
