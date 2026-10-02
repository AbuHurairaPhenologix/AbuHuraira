using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Contracts.Messages;

public enum SignalQuality
{
    /// <summary>Decoded from a frame that passed E2E checks and is within the physical range.</summary>
    Valid = 0,

    /// <summary>Decoded but outside the physical range defined in the CAN database.</summary>
    OutOfRange = 1,

    /// <summary>No fresh frame was received within the timeout; the value is the last known one.</summary>
    Stale = 2,
}

/// <summary>A normalized, VSS-addressed signal value.</summary>
/// <param name="Path">VSS-inspired path, e.g. <c>Vehicle.Speed</c>.</param>
/// <param name="Name">Short signal name from the CAN database, e.g. <c>VehicleSpeed</c>.</param>
/// <param name="Value">Physical value after factor/offset scaling.</param>
/// <param name="Unit">Unit of <paramref name="Value"/>.</param>
/// <param name="SourceEcuId">The ECU that transmitted the frame.</param>
/// <param name="Timestamp">Receive timestamp of the CAN frame at the gateway.</param>
/// <param name="Quality">Signal quality.</param>
/// <param name="Label">Text for enumerated signals (e.g. <c>Drive</c>).</param>
public sealed record SignalValueDto(
    string Path,
    string Name,
    double Value,
    string Unit,
    string SourceEcuId,
    DateTimeOffset Timestamp,
    SignalQuality Quality,
    string? Label = null);

/// <summary>Gateway-side processing statistics for the interval since the previous telemetry message.</summary>
public sealed record GatewayStatisticsDto(
    long FramesReceived,
    long FramesDecoded,
    long FramesRejected,
    double FramesPerSecond,
    double AverageDecodeMicroseconds,
    double MaxDecodeMicroseconds);

/// <summary>Published to <c>autosphere/vehicles/{vehicleId}/telemetry</c>.</summary>
public sealed record TelemetryMessage : VehicleMessage
{
    /// <summary>Monotonic sequence number, lets the backend detect gaps.</summary>
    public required long Sequence { get; init; }

    public required IReadOnlyList<SignalValueDto> Signals { get; init; }

    public GatewayStatisticsDto? Statistics { get; init; }
}

public enum GatewayCanState
{
    Disconnected = 0,
    Connected = 1,
}

/// <summary>Communication and software state of one ECU as observed by the gateway.</summary>
public sealed record EcuStatusDto(
    string EcuId,
    EcuType EcuType,
    string Name,
    EcuStatus Status,
    string? SoftwareVersion,
    string? HardwareVersion,
    DateTimeOffset? LastSeen,
    int ActiveDtcCount,
    long TimeoutCount,
    long E2EErrorCount);

/// <summary>
/// Published retained to <c>autosphere/vehicles/{vehicleId}/status</c>. The gateway also registers an
/// offline variant of this message as its MQTT last will.
/// </summary>
public sealed record VehicleStatusMessage : VehicleMessage
{
    public required ConnectivityStatus Connectivity { get; init; }

    public string? GatewayId { get; init; }

    public string? GatewaySoftwareVersion { get; init; }

    public GatewayCanState CanState { get; init; }

    public string? CanTransport { get; init; }

    public bool SimulationMode { get; init; }

    public double UptimeSeconds { get; init; }

    public IReadOnlyList<EcuStatusDto> Ecus { get; init; } = [];
}
