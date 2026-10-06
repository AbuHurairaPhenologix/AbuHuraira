using System.Diagnostics.CodeAnalysis;

namespace AutoSphere.SharedKernel.Diagnostics;

/// <summary>
/// A five-character diagnostic trouble code such as <c>P0A7E</c> or <c>U0100</c>.
/// </summary>
/// <remarks>
/// The two-byte encoding follows the SAE J2012 / ISO 15031-6 convention used by OBD and UDS:
/// bits 15..14 select the system (P, C, B, U), bits 13..12 the first digit (0-3) and the remaining
/// three nibbles the hexadecimal characters. In UDS a third byte (failure type byte, FTB) is appended;
/// AutoSphere always uses FTB 0x00 for simplicity.
/// </remarks>
public readonly record struct DtcCode
{
    private const string Systems = "PCBU";

    private DtcCode(string value) => Value = value;

    public string Value { get; }

    /// <summary>The DTC system letter: P = powertrain, C = chassis, B = body, U = network.</summary>
    public char System => Value[0];

    public static DtcCode Parse(string value) =>
        TryParse(value, out var code) ? code : throw new FormatException($"'{value}' is not a valid DTC code.");

    public static bool TryParse([NotNullWhen(true)] string? value, out DtcCode code)
    {
        code = default;
        if (value is null || value.Length != 5)
        {
            return false;
        }

        var normalized = value.ToUpperInvariant();
        if (!Systems.Contains(normalized[0], StringComparison.Ordinal) || normalized[1] is < '0' or > '3')
        {
            return false;
        }

        for (var i = 2; i < 5; i++)
        {
            if (!char.IsAsciiHexDigit(normalized[i]))
            {
                return false;
            }
        }

        code = new DtcCode(normalized);
        return true;
    }

    /// <summary>Encodes the code into its two-byte representation (high byte first).</summary>
    public ushort ToUInt16()
    {
        var system = Systems.IndexOf(Value[0], StringComparison.Ordinal);
        var firstDigit = Value[1] - '0';
        var rest = Convert.ToUInt16(Value[2..], 16);
        return (ushort)((system << 14) | (firstDigit << 12) | rest);
    }

    /// <summary>Encodes the code as the 3-byte UDS DTC (2 bytes code + failure type byte 0x00).</summary>
    public uint ToUdsDtc() => (uint)ToUInt16() << 8;

    public static DtcCode FromUInt16(ushort raw)
    {
        var system = Systems[raw >> 14];
        var firstDigit = (char)('0' + ((raw >> 12) & 0x3));
        var rest = (raw & 0x0FFF).ToString("X3", global::System.Globalization.CultureInfo.InvariantCulture);
        return new DtcCode($"{system}{firstDigit}{rest}");
    }

    /// <summary>Decodes a 3-byte UDS DTC value; the failure type byte is ignored.</summary>
    public static DtcCode FromUdsDtc(uint udsDtc) => FromUInt16((ushort)((udsDtc >> 8) & 0xFFFF));

    public override string ToString() => Value ?? string.Empty;
}
