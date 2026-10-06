using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Diagnostics.Addressing;

/// <summary>Physical request/response CAN identifiers of an ECU's diagnostic channel.</summary>
public readonly record struct DiagnosticAddress(uint RequestId, uint ResponseId);

/// <summary>
/// 11-bit diagnostic addressing following the widespread OBD convention
/// (request <c>0x7E0 + n</c>, response <c>0x7E8 + n</c>).
/// </summary>
public static class DiagnosticAddresses
{
    public const uint FunctionalRequestId = 0x7DF;

    public static DiagnosticAddress For(EcuType ecuType) => ecuType switch
    {
        EcuType.VehicleControlUnit => new(0x7E0, 0x7E8),
        EcuType.MotorControlUnit => new(0x7E1, 0x7E9),
        EcuType.BatteryManagementSystem => new(0x7E2, 0x7EA),
        EcuType.BodyControlModule => new(0x7E3, 0x7EB),
        _ => throw new ArgumentOutOfRangeException(nameof(ecuType), ecuType, "ECU type has no CAN diagnostic address."),
    };

    public static bool HasCanAddress(EcuType ecuType) => ecuType != EcuType.CentralGateway;
}
