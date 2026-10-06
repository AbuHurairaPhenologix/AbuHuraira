namespace AutoSphere.Domain.Diagnostics;

public enum DiagnosticSessionStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2,
    TimedOut = 3,
}

/// <summary>
/// One remote diagnostic request/response exchange. The id is the correlation id carried through
/// HTTP, MQTT, the gateway and back.
/// </summary>
public sealed class DiagnosticSession
{
    private DiagnosticSession()
    {
    }

    public Guid Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    /// <summary>Requested operation (name of <c>DiagnosticOperation</c>).</summary>
    public string Operation { get; private set; } = string.Empty;

    public string? EcuId { get; private set; }

    public string RequestedBy { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DiagnosticSessionStatus Status { get; private set; }

    public string? Error { get; private set; }

    /// <summary>Total duration measured by the gateway.</summary>
    public double? VehicleDurationMs { get; private set; }

    /// <summary>Round trip measured by the backend (HTTP request → MQTT → vehicle → MQTT → backend).</summary>
    public double? RoundTripMs { get; private set; }

    /// <summary>Serialized per-ECU results as received from the vehicle (opaque to the domain).</summary>
    public string? ResultJson { get; private set; }

    /// <summary>Human-readable diagnosis, e.g. "Battery Thermal Fault (P0A7E on BMS-001)".</summary>
    public string? Diagnosis { get; private set; }

    public static DiagnosticSession Start(Guid correlationId, Guid vehicleKey, string operation, string? ecuId, string requestedBy, DateTimeOffset at) => new()
    {
        Id = correlationId,
        VehicleKey = vehicleKey,
        Operation = operation,
        EcuId = ecuId,
        RequestedBy = requestedBy,
        RequestedAt = at,
        Status = DiagnosticSessionStatus.Pending,
    };

    public void Complete(bool success, string? error, double vehicleDurationMs, string resultJson, string? diagnosis, DateTimeOffset at)
    {
        Status = success ? DiagnosticSessionStatus.Completed : DiagnosticSessionStatus.Failed;
        Error = error;
        VehicleDurationMs = vehicleDurationMs;
        RoundTripMs = (at - RequestedAt).TotalMilliseconds;
        ResultJson = resultJson;
        Diagnosis = diagnosis;
        CompletedAt = at;
    }

    public void TimeOut(DateTimeOffset at)
    {
        Status = DiagnosticSessionStatus.TimedOut;
        Error = "The vehicle did not answer in time.";
        CompletedAt = at;
        RoundTripMs = (at - RequestedAt).TotalMilliseconds;
    }
}
