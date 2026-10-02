using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Application.Diagnostics;

public sealed record DtcRecordDto(
    Guid Id,
    string Code,
    string EcuId,
    string Description,
    DtcSeverity Severity,
    string FaultCategory,
    DtcRecordStatus Status,
    byte StatusMask,
    bool Confirmed,
    DateTimeOffset DetectedAt,
    DateTimeOffset LastReportedAt,
    DateTimeOffset? ResolvedAt,
    DateTimeOffset? ClearedAt,
    int OccurrenceCount,
    IReadOnlyList<DtcSnapshotValue> Snapshot,
    bool IsClearable);

public sealed record DiagnosticSessionDto(
    Guid CorrelationId,
    string VehicleId,
    string Operation,
    string? EcuId,
    DiagnosticSessionStatus Status,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt,
    double? VehicleDurationMs,
    double? RoundTripMs,
    string? Error,
    string? Diagnosis,
    IReadOnlyList<EcuDiagnosticResultDto> Results);

/// <summary>Generic diagnostic request accepted by the API.</summary>
public sealed record DiagnosticRequest(
    DiagnosticOperation Operation,
    string? EcuId = null,
    string? DtcCode = null,
    IReadOnlyList<ushort>? DataIdentifiers = null,
    DiagnosticSessionKind? Session = null,
    EcuResetKind? ResetKind = null);

public sealed record ClearDtcsRequest(string? EcuId, string? Code);

public static class DiagnosticMappings
{
    public static DtcRecordDto ToDto(this DiagnosticTroubleCode dtc) => new(dtc.Id, dtc.Code, dtc.EcuId, dtc.Description, dtc.Severity,
        dtc.FaultCategory, dtc.Status, dtc.StatusMask, dtc.Confirmed, dtc.DetectedAt, dtc.LastReportedAt, dtc.ResolvedAt, dtc.ClearedAt,
        dtc.OccurrenceCount, dtc.Snapshot, dtc.IsClearable);
}
