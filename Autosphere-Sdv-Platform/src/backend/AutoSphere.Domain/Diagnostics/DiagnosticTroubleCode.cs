using AutoSphere.SharedKernel.Diagnostics;

namespace AutoSphere.Domain.Diagnostics;

/// <summary>Lifecycle of a DTC record in the cloud.</summary>
public enum DtcRecordStatus
{
    /// <summary>The fault condition is present (UDS testFailed bit set).</summary>
    Active = 0,

    /// <summary>The condition healed; the DTC is still stored in the ECU's fault memory.</summary>
    Resolved = 1,

    /// <summary>The DTC was removed from the ECU's fault memory (ClearDiagnosticInformation).</summary>
    Cleared = 2,
}

/// <summary>A value captured with the DTC (freeze frame).</summary>
public sealed record DtcSnapshotValue(string Name, double Value, string Unit);

/// <summary>A diagnostic trouble code reported by a vehicle, with its full lifecycle history.</summary>
public sealed class DiagnosticTroubleCode
{
    private DiagnosticTroubleCode()
    {
    }

    public Guid Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    public string EcuId { get; private set; } = string.Empty;

    public string Code { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    public DtcSeverity Severity { get; private set; }

    public string FaultCategory { get; private set; } = string.Empty;

    public DtcRecordStatus Status { get; private set; }

    /// <summary>Last reported raw UDS status byte.</summary>
    public byte StatusMask { get; private set; }

    public bool Confirmed { get; private set; }

    public DateTimeOffset DetectedAt { get; private set; }

    public DateTimeOffset LastReportedAt { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset? ClearedAt { get; private set; }

    /// <summary>How often the condition became active while the record was stored.</summary>
    public int OccurrenceCount { get; private set; }

    public List<DtcSnapshotValue> Snapshot { get; private set; } = [];

    /// <summary>Only DTCs whose condition is no longer present may be cleared.</summary>
    public bool IsClearable => Status == DtcRecordStatus.Resolved;

    public static DiagnosticTroubleCode Detect(Guid vehicleKey, string ecuId, string code, byte statusMask, bool testFailed, bool confirmed,
        IEnumerable<DtcSnapshotValue> snapshot, DateTimeOffset at)
    {
        var definition = KnownDtcs.Describe(DtcCode.Parse(code));
        return new DiagnosticTroubleCode
        {
            Id = Guid.NewGuid(),
            VehicleKey = vehicleKey,
            EcuId = ecuId,
            Code = definition.Code.Value,
            Description = definition.Description,
            Severity = definition.Severity,
            FaultCategory = definition.FaultCategory,
            Status = testFailed ? DtcRecordStatus.Active : DtcRecordStatus.Resolved,
            StatusMask = statusMask,
            Confirmed = confirmed,
            DetectedAt = at,
            LastReportedAt = at,
            ResolvedAt = testFailed ? null : at,
            OccurrenceCount = 1,
            Snapshot = snapshot.ToList(),
        };
    }

    /// <summary>Applies a new observation from the vehicle. Returns true if the status changed.</summary>
    public bool Observe(byte statusMask, bool testFailed, bool confirmed, IReadOnlyCollection<DtcSnapshotValue> snapshot, DateTimeOffset at)
    {
        var previous = Status;
        StatusMask = statusMask;
        Confirmed = confirmed;
        LastReportedAt = at;
        if (snapshot.Count > 0 && Snapshot.Count == 0)
        {
            Snapshot = snapshot.ToList();
        }

        if (testFailed && Status != DtcRecordStatus.Active)
        {
            Status = DtcRecordStatus.Active;
            ResolvedAt = null;
            OccurrenceCount++;
        }
        else if (!testFailed && Status == DtcRecordStatus.Active)
        {
            Status = DtcRecordStatus.Resolved;
            ResolvedAt = at;
        }

        return previous != Status;
    }

    /// <summary>The DTC is no longer stored in the ECU.</summary>
    public void MarkCleared(DateTimeOffset at)
    {
        if (Status == DtcRecordStatus.Cleared)
        {
            return;
        }

        ResolvedAt ??= at;
        Status = DtcRecordStatus.Cleared;
        ClearedAt = at;
    }
}
