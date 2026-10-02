using AutoSphere.Contracts.Messages;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Application.Telemetry;

/// <summary>Latest live state of a vehicle as held in the cache.</summary>
public sealed record VehicleLiveState(
    string VehicleId,
    long Sequence,
    DateTimeOffset GatewayTimestamp,
    DateTimeOffset ReceivedAt,
    IReadOnlyList<SignalValueDto> Signals,
    GatewayStatisticsDto? Statistics);

/// <summary>Strongly typed view of the most important vehicle signals (with units in the property names).</summary>
public sealed record VehicleSnapshotDto(
    double? SpeedKmh,
    double? MotorRpm,
    double? MotorTorqueNm,
    double? MotorTemperatureC,
    double? BatteryStateOfChargePercent,
    double? BatteryVoltageV,
    double? BatteryCurrentA,
    double? BatteryTemperatureC,
    double? RangeKm,
    double? OdometerKm,
    double? AcceleratorPedalPercent,
    bool? BrakePressed,
    GearPosition? Gear,
    IgnitionStatus? Ignition,
    ChargingStatus? Charging,
    bool? AnyDoorOpen)
{
    public static VehicleSnapshotDto From(IReadOnlyList<SignalValueDto> signals)
    {
        var byPath = signals.ToDictionary(s => s.Path, s => s.Value, StringComparer.Ordinal);
        double? Get(string path) => byPath.TryGetValue(path, out var value) ? value : null;
        TEnum? Enum<TEnum>(string path)
            where TEnum : struct, System.Enum =>
            Get(path) is { } raw && System.Enum.IsDefined(typeof(TEnum), (int)raw) ? (TEnum)(object)(int)raw : null;

        var doors = new[] { VssPaths.DoorFrontLeftOpen, VssPaths.DoorFrontRightOpen, VssPaths.DoorRearLeftOpen, VssPaths.DoorRearRightOpen, VssPaths.TrunkOpen }
            .Select(Get).Where(v => v.HasValue).ToList();

        return new VehicleSnapshotDto(
            Get(VssPaths.VehicleSpeed),
            Get(VssPaths.MotorSpeed),
            Get(VssPaths.MotorTorque),
            Get(VssPaths.MotorTemperature),
            Get(VssPaths.BatteryStateOfCharge),
            Get(VssPaths.BatteryVoltage),
            Get(VssPaths.BatteryCurrent),
            Get(VssPaths.BatteryTemperature),
            Get(VssPaths.VehicleRange),
            Get(VssPaths.Odometer),
            Get(VssPaths.AcceleratorPedalPosition),
            Get(VssPaths.BrakePedalPressed) is { } brake ? brake > 0.5 : null,
            Enum<GearPosition>(VssPaths.GearPosition),
            Enum<IgnitionStatus>(VssPaths.IgnitionStatus),
            Enum<ChargingStatus>(VssPaths.ChargingStatus),
            doors.Count == 0 ? null : doors.Any(d => d > 0.5));
    }
}

/// <summary>Latency breakdown attached to every live update (milliseconds).</summary>
/// <param name="CanToGatewayPublishMs">Age of the newest CAN-sourced value when the gateway published the message.</param>
/// <param name="GatewayToBackendMs">Gateway publish → backend receive (MQTT transport, assumes synchronized clocks).</param>
/// <param name="CanToBackendMs">Newest CAN frame → backend receive.</param>
public sealed record LatencyDto(double CanToGatewayPublishMs, double GatewayToBackendMs, double CanToBackendMs);

/// <summary>Live update pushed to dashboards via SignalR and returned by the latest-telemetry endpoint.</summary>
public sealed record VehicleTelemetryDto(
    string VehicleId,
    long Sequence,
    DateTimeOffset GatewayTimestamp,
    DateTimeOffset BackendReceivedAt,
    VehicleSnapshotDto Snapshot,
    IReadOnlyList<SignalValueDto> Signals,
    GatewayStatisticsDto? Statistics,
    LatencyDto Latency)
{
    public static VehicleTelemetryDto From(VehicleLiveState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var newest = state.Signals.Count == 0 ? state.GatewayTimestamp : state.Signals.Max(s => s.Timestamp);
        return new VehicleTelemetryDto(state.VehicleId, state.Sequence, state.GatewayTimestamp, state.ReceivedAt,
            VehicleSnapshotDto.From(state.Signals), state.Signals, state.Statistics,
            new LatencyDto(
                Math.Round((state.GatewayTimestamp - newest).TotalMilliseconds, 2),
                Math.Round((state.ReceivedAt - state.GatewayTimestamp).TotalMilliseconds, 2),
                Math.Round((state.ReceivedAt - newest).TotalMilliseconds, 2)));
    }
}

public sealed record TelemetryPointDto(DateTimeOffset Timestamp, double Value);

public sealed record TelemetrySeriesDto(string SignalPath, IReadOnlyList<TelemetryPointDto> Points);
