using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Diagnostics.DataIdentifiers;

public enum DidDataType
{
    Ascii,
    UInt8,
    UInt16,
    Int16,
    UInt32,
}

/// <summary>Definition of a data identifier (DID) used with ReadDataByIdentifier and in DTC snapshots.</summary>
public sealed record DataIdentifierDefinition(
    ushort Identifier,
    string Name,
    DidDataType DataType,
    int Length,
    double Factor = 1,
    string? Unit = null,
    EcuType? OnlyOn = null)
{
    public string Hex => Identifier.ToString("X4", CultureInfo.InvariantCulture);

    public bool IsNumeric => DataType != DidDataType.Ascii;
}

/// <summary>
/// Data identifiers known to AutoSphere. The 0xF1xx identifiers follow the identification DIDs defined
/// in ISO 14229-1 Annex C; the 0x01xx–0x04xx live-data DIDs are project-specific.
/// </summary>
public static class DataIdentifierCatalog
{
    public static readonly DataIdentifierDefinition ActiveDiagnosticSession = new(0xF186, "ActiveDiagnosticSession", DidDataType.UInt8, 1);
    public static readonly DataIdentifierDefinition SparePartNumber = new(0xF187, "SparePartNumber", DidDataType.Ascii, 12);
    public static readonly DataIdentifierDefinition SoftwareVersion = new(0xF189, "ApplicationSoftwareVersion", DidDataType.Ascii, 8);
    public static readonly DataIdentifierDefinition EcuSerialNumber = new(0xF18C, "EcuSerialNumber", DidDataType.Ascii, 12);
    public static readonly DataIdentifierDefinition Vin = new(0xF190, "VIN", DidDataType.Ascii, 17);
    public static readonly DataIdentifierDefinition HardwareVersion = new(0xF191, "HardwareVersion", DidDataType.Ascii, 8);

    public static readonly DataIdentifierDefinition VehicleSpeed = new(0x0301, "VehicleSpeed", DidDataType.UInt16, 2, 0.01, "km/h", EcuType.VehicleControlUnit);
    public static readonly DataIdentifierDefinition Odometer = new(0x0302, "Odometer", DidDataType.UInt32, 4, 0.1, "km", EcuType.VehicleControlUnit);
    public static readonly DataIdentifierDefinition MotorSpeed = new(0x0201, "MotorSpeed", DidDataType.Int16, 2, 1, "rpm", EcuType.MotorControlUnit);
    public static readonly DataIdentifierDefinition MotorTemperature = new(0x0202, "MotorTemperature", DidDataType.Int16, 2, 0.1, "°C", EcuType.MotorControlUnit);
    public static readonly DataIdentifierDefinition BatteryTemperature = new(0x0101, "BatteryTemperature", DidDataType.Int16, 2, 0.1, "°C", EcuType.BatteryManagementSystem);
    public static readonly DataIdentifierDefinition BatteryStateOfCharge = new(0x0102, "BatteryStateOfCharge", DidDataType.UInt16, 2, 0.1, "%", EcuType.BatteryManagementSystem);
    public static readonly DataIdentifierDefinition BatteryVoltage = new(0x0103, "BatteryVoltage", DidDataType.UInt16, 2, 0.1, "V", EcuType.BatteryManagementSystem);
    public static readonly DataIdentifierDefinition BatteryCurrent = new(0x0104, "BatteryCurrent", DidDataType.Int16, 2, 0.1, "A", EcuType.BatteryManagementSystem);
    public static readonly DataIdentifierDefinition DoorStatus = new(0x0401, "DoorStatusBitfield", DidDataType.UInt8, 1, 1, null, EcuType.BodyControlModule);

    private static readonly Dictionary<ushort, DataIdentifierDefinition> ById = new[]
    {
        ActiveDiagnosticSession, SparePartNumber, SoftwareVersion, EcuSerialNumber, Vin, HardwareVersion,
        VehicleSpeed, Odometer, MotorSpeed, MotorTemperature, BatteryTemperature, BatteryStateOfCharge,
        BatteryVoltage, BatteryCurrent, DoorStatus,
    }.ToDictionary(d => d.Identifier);

    public static IReadOnlyCollection<DataIdentifierDefinition> All => ById.Values;

    /// <summary>Identification DIDs every ECU supports.</summary>
    public static IReadOnlyList<DataIdentifierDefinition> Identification { get; } =
        [SoftwareVersion, HardwareVersion, EcuSerialNumber, SparePartNumber, Vin, ActiveDiagnosticSession];

    public static bool TryGet(ushort identifier, out DataIdentifierDefinition definition) => ById.TryGetValue(identifier, out definition!);

    public static IEnumerable<DataIdentifierDefinition> LiveDataFor(EcuType ecuType) => ById.Values.Where(d => d.OnlyOn == ecuType);

    public static bool IsSupportedBy(DataIdentifierDefinition definition, EcuType ecuType) =>
        definition.OnlyOn is null || definition.OnlyOn == ecuType;

    /// <summary>Encodes a value for transmission (server side).</summary>
    public static byte[] Encode(DataIdentifierDefinition definition, object value)
    {
        var data = new byte[definition.Length];
        switch (definition.DataType)
        {
            case DidDataType.Ascii:
                var text = Encoding.ASCII.GetBytes(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty);
                data.AsSpan().Fill((byte)' ');
                text.AsSpan(0, Math.Min(text.Length, data.Length)).CopyTo(data);
                break;
            case DidDataType.UInt8:
                data[0] = (byte)Math.Clamp(ToRaw(definition, value), byte.MinValue, byte.MaxValue);
                break;
            case DidDataType.UInt16:
                BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)Math.Clamp(ToRaw(definition, value), ushort.MinValue, ushort.MaxValue));
                break;
            case DidDataType.Int16:
                BinaryPrimitives.WriteInt16BigEndian(data, (short)Math.Clamp(ToRaw(definition, value), short.MinValue, short.MaxValue));
                break;
            case DidDataType.UInt32:
                BinaryPrimitives.WriteUInt32BigEndian(data, (uint)Math.Clamp(ToRaw(definition, value), uint.MinValue, uint.MaxValue));
                break;
        }

        return data;
    }

    /// <summary>Decodes a received value to a display string and, for numeric DIDs, a physical value.</summary>
    public static (string Text, double? Numeric) Decode(DataIdentifierDefinition definition, ReadOnlySpan<byte> data)
    {
        if (data.Length < definition.Length)
        {
            throw new ArgumentException($"DID {definition.Hex} requires {definition.Length} bytes, got {data.Length}.", nameof(data));
        }

        double raw;
        switch (definition.DataType)
        {
            case DidDataType.Ascii:
                return (Encoding.ASCII.GetString(data[..definition.Length]).TrimEnd(' ', '\0'), null);
            case DidDataType.UInt8:
                raw = data[0];
                break;
            case DidDataType.UInt16:
                raw = BinaryPrimitives.ReadUInt16BigEndian(data);
                break;
            case DidDataType.Int16:
                raw = BinaryPrimitives.ReadInt16BigEndian(data);
                break;
            default:
                raw = BinaryPrimitives.ReadUInt32BigEndian(data);
                break;
        }

        var physical = Math.Round(raw * definition.Factor, 3);
        return (physical.ToString(CultureInfo.InvariantCulture), physical);
    }

    private static long ToRaw(DataIdentifierDefinition definition, object value) =>
        (long)Math.Round(Convert.ToDouble(value, CultureInfo.InvariantCulture) / definition.Factor, MidpointRounding.AwayFromZero);
}
