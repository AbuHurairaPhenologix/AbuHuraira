using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Contracts.Messages;

/// <summary>A value captured together with a DTC (UDS "snapshot" / OBD "freeze frame").</summary>
public sealed record DtcSnapshotValueDto(string Name, double Value, string Unit);

/// <summary>A DTC as stored in an ECU's fault memory.</summary>
/// <param name="Code">Five-character DTC, e.g. <c>P0A7E</c>.</param>
/// <param name="EcuId">The ECU in whose memory the DTC is stored.</param>
/// <param name="StatusMask">The raw UDS DTC status byte.</param>
/// <param name="TestFailed">Bit 0: the fault condition is present right now.</param>
/// <param name="Confirmed">Bit 3: the fault was confirmed (debounced) at least once.</param>
public sealed record DtcDto(
    string Code,
    string EcuId,
    byte StatusMask,
    bool TestFailed,
    bool Confirmed,
    string Description,
    DtcSeverity Severity,
    string FaultCategory,
    IReadOnlyList<DtcSnapshotValueDto> Snapshot);

/// <summary>
/// Published to <c>autosphere/vehicles/{vehicleId}/dtcs</c> whenever the fault memory of any ECU changes.
/// The message is a complete image of the fault memory of every ECU listed in <see cref="ReportedEcuIds"/>;
/// ECUs that could not be read (e.g. offline) are not listed and their previous state must be kept.
/// </summary>
public sealed record DtcReportMessage : VehicleMessage
{
    public required IReadOnlyList<string> ReportedEcuIds { get; init; }

    public required IReadOnlyList<DtcDto> Dtcs { get; init; }
}

public enum AlertState
{
    Raised = 0,
    Cleared = 1,
}

public enum AlertCategory
{
    Thermal = 0,
    Communication = 1,
    SignalPlausibility = 2,
    Connectivity = 3,
    Ota = 4,
    Diagnostics = 5,
}

/// <summary>Edge alert published on a state transition to <c>autosphere/vehicles/{vehicleId}/alerts</c>.</summary>
public sealed record AlertMessage : VehicleMessage
{
    /// <summary>Stable key identifying the alert condition, e.g. <c>battery-temperature-critical</c>.</summary>
    public required string AlertKey { get; init; }

    public required AlertState State { get; init; }

    public required DtcSeverity Severity { get; init; }

    public required AlertCategory Category { get; init; }

    public required string Message { get; init; }

    public string? EcuId { get; init; }

    public string? SignalPath { get; init; }

    public double? Value { get; init; }

    public double? Threshold { get; init; }
}

public enum DiagnosticOperation
{
    /// <summary>Reads identification, software version and fault memory of every ECU.</summary>
    FullScan = 0,
    ReadDtcs = 1,
    ClearDtcs = 2,
    EcuReset = 3,
    ReadDataByIdentifier = 4,
    ReadSoftwareVersion = 5,
    SessionControl = 6,
    TesterPresent = 7,
}

public enum DiagnosticSessionKind
{
    Default = 1,
    Programming = 2,
    Extended = 3,
}

public enum EcuResetKind
{
    HardReset = 1,
    KeyOffOnReset = 2,
    SoftReset = 3,
}

/// <summary>Published by the backend to <c>autosphere/vehicles/{vehicleId}/diagnostics/request</c>.</summary>
public sealed record DiagnosticRequestMessage : CorrelatedVehicleMessage
{
    public required DiagnosticOperation Operation { get; init; }

    /// <summary>Target ECU; <c>null</c> addresses every ECU on the vehicle.</summary>
    public string? EcuId { get; init; }

    /// <summary>Optional single DTC for <see cref="DiagnosticOperation.ClearDtcs"/>; null clears all clearable DTCs.</summary>
    public string? DtcCode { get; init; }

    public IReadOnlyList<ushort>? DataIdentifiers { get; init; }

    public DiagnosticSessionKind? Session { get; init; }

    public EcuResetKind? ResetKind { get; init; }

    public string? RequestedBy { get; init; }
}

/// <summary>A decoded data identifier (UDS DID) value.</summary>
public sealed record DataIdentifierValueDto(string Identifier, string Name, string Value, string? Unit);

/// <summary>One UDS request/response exchange, kept raw for traceability and teaching.</summary>
public sealed record UdsExchangeDto(string Service, string RequestHex, string? ResponseHex, double DurationMs, string? NegativeResponse);

/// <summary>Result of a diagnostic operation for one ECU.</summary>
public sealed record EcuDiagnosticResultDto(
    string EcuId,
    bool Success,
    string? Error,
    double DurationMs,
    IReadOnlyList<UdsExchangeDto> Exchanges,
    IReadOnlyList<DtcDto>? Dtcs = null,
    IReadOnlyList<DataIdentifierValueDto>? Data = null);

/// <summary>Published by the gateway to <c>autosphere/vehicles/{vehicleId}/diagnostics/response</c>.</summary>
public sealed record DiagnosticResponseMessage : CorrelatedVehicleMessage
{
    public required DiagnosticOperation Operation { get; init; }

    public required bool Success { get; init; }

    public string? Error { get; init; }

    public required IReadOnlyList<EcuDiagnosticResultDto> Results { get; init; }

    public double TotalDurationMs { get; init; }
}
