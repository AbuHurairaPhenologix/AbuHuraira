using AutoSphere.Domain.Vehicles;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Application.Vehicles;

public sealed record EcuDto(
    string EcuId,
    EcuType Type,
    string Name,
    EcuStatus Status,
    string SoftwareVersion,
    string? HardwareVersion,
    DateTimeOffset? LastHeartbeatAt,
    int ActiveDtcCount,
    long TimeoutCount,
    long E2EErrorCount);

public sealed record HealthDto(HealthStatus Status, int Score, string? Summary, DateTimeOffset? EvaluatedAt);

public sealed record VehicleSummaryDto(
    string VehicleId,
    string Vin,
    string Model,
    int ModelYear,
    ConnectivityStatus Connectivity,
    DateTimeOffset? LastSeenAt,
    HealthDto Health,
    int EcuCount);

public sealed record VehicleDetailsDto(
    string VehicleId,
    string Vin,
    string Model,
    int ModelYear,
    DateTimeOffset RegisteredAt,
    ConnectivityStatus Connectivity,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset? ConnectivityChangedAt,
    string? GatewayId,
    string? GatewaySoftwareVersion,
    bool SimulationMode,
    HealthDto Health,
    IReadOnlyList<EcuDto> Ecus);

public sealed record RegisterEcuRequest(string EcuId, EcuType Type, string Name, string? SoftwareVersion);

public sealed record RegisterVehicleRequest(string VehicleId, string Vin, string Model, int ModelYear, IReadOnlyList<RegisterEcuRequest>? Ecus);

public sealed record HealthSnapshotDto(DateTimeOffset Timestamp, HealthStatus Status, int Score, string Summary);

public static class VehicleMappings
{
    public static EcuDto ToDto(this Ecu ecu) => new(ecu.EcuId, ecu.Type, ecu.Name, ecu.Status, ecu.SoftwareVersion, ecu.HardwareVersion,
        ecu.LastHeartbeatAt, ecu.ActiveDtcCount, ecu.TimeoutCount, ecu.E2EErrorCount);

    public static HealthDto ToHealthDto(this Vehicle vehicle) => new(vehicle.Health, vehicle.HealthScore, vehicle.HealthSummary, vehicle.HealthEvaluatedAt);

    public static VehicleSummaryDto ToSummary(this Vehicle vehicle) => new(vehicle.VehicleId, vehicle.Vin, vehicle.Model, vehicle.ModelYear,
        vehicle.Connectivity, vehicle.LastSeenAt, vehicle.ToHealthDto(), vehicle.Ecus.Count);

    public static VehicleDetailsDto ToDetails(this Vehicle vehicle) => new(vehicle.VehicleId, vehicle.Vin, vehicle.Model, vehicle.ModelYear,
        vehicle.RegisteredAt, vehicle.Connectivity, vehicle.LastSeenAt, vehicle.ConnectivityChangedAt, vehicle.GatewayId,
        vehicle.GatewaySoftwareVersion, vehicle.SimulationMode, vehicle.ToHealthDto(),
        vehicle.Ecus.OrderBy(e => e.Type).ThenBy(e => e.EcuId, StringComparer.Ordinal).Select(e => e.ToDto()).ToList());
}
