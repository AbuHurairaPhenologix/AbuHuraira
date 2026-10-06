using AutoSphere.SharedKernel.Simulation;

namespace AutoSphere.Domain.Simulation;

/// <summary>Audit record of a fault injected through the test harness.</summary>
public sealed class FaultInjectionRecord
{
    private FaultInjectionRecord()
    {
    }

    /// <summary>The correlation id of the injection command.</summary>
    public Guid Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    public FaultType Fault { get; private set; }

    /// <summary><c>Inject</c> or <c>Clear</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string? TargetEcuId { get; private set; }

    public int? DurationSeconds { get; private set; }

    public string RequestedBy { get; private set; } = string.Empty;

    public DateTimeOffset RequestedAt { get; private set; }

    public bool? Accepted { get; private set; }

    public string? Result { get; private set; }

    public string? HandledBy { get; private set; }

    public DateTimeOffset? AcknowledgedAt { get; private set; }

    public static FaultInjectionRecord Create(Guid correlationId, Guid vehicleKey, FaultType fault, string action, string? targetEcuId,
        int? durationSeconds, string requestedBy, DateTimeOffset at) => new()
    {
        Id = correlationId,
        VehicleKey = vehicleKey,
        Fault = fault,
        Action = action,
        TargetEcuId = targetEcuId,
        DurationSeconds = durationSeconds,
        RequestedBy = requestedBy,
        RequestedAt = at,
    };

    public void Acknowledge(bool accepted, string result, string handledBy, DateTimeOffset at)
    {
        Accepted = accepted;
        Result = result;
        HandledBy = handledBy;
        AcknowledgedAt = at;
    }
}
