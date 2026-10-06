namespace AutoSphere.VehicleSignals.Codec;

/// <summary>
/// End-to-end protection inspired by AUTOSAR E2E Profile 1: CRC-8 (SAE J1850 polynomial 0x1D, start
/// value 0xFF, final XOR 0xFF) over the data id and payload, plus a 4-bit alive counter.
/// </summary>
/// <remarks>
/// Layout used by AutoSphere: byte 0 = CRC, low nibble of byte 1 = counter. This is a simplified,
/// educational scheme and not a certified implementation of the AUTOSAR E2E library.
/// </remarks>
public static class E2EProtection
{
    public const int CrcByte = 0;
    public const int CounterByte = 1;
    public const int CounterModulo = 16;

    private static readonly byte[] Table = BuildTable();

    public static byte ComputeCrc(ushort dataId, ReadOnlySpan<byte> payload)
    {
        byte crc = 0xFF;
        crc = Table[crc ^ (byte)(dataId & 0xFF)];
        crc = Table[crc ^ (byte)(dataId >> 8)];
        for (var i = CounterByte; i < payload.Length; i++)
        {
            crc = Table[crc ^ payload[i]];
        }

        return (byte)(crc ^ 0xFF);
    }

    /// <summary>Writes counter and CRC into an otherwise complete payload.</summary>
    public static void Protect(ushort dataId, Span<byte> payload, int counter)
    {
        payload[CounterByte] = (byte)((payload[CounterByte] & 0xF0) | (counter & 0x0F));
        payload[CrcByte] = ComputeCrc(dataId, payload);
    }

    public static bool CrcIsValid(ushort dataId, ReadOnlySpan<byte> payload) =>
        payload.Length > CounterByte && payload[CrcByte] == ComputeCrc(dataId, payload);

    public static int ReadCounter(ReadOnlySpan<byte> payload) => payload[CounterByte] & 0x0F;

    /// <summary>Number of counter steps between two consecutive receptions (1 = no loss).</summary>
    public static int CounterDelta(int previous, int current) => ((current - previous) + CounterModulo) % CounterModulo;

    private static byte[] BuildTable()
    {
        var table = new byte[256];
        for (var i = 0; i < 256; i++)
        {
            var crc = (byte)i;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 0x80) != 0 ? (byte)((crc << 1) ^ 0x1D) : (byte)(crc << 1);
            }

            table[i] = crc;
        }

        return table;
    }
}
