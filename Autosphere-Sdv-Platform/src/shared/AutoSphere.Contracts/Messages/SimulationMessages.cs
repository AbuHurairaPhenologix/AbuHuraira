using AutoSphere.SharedKernel.Simulation;

namespace AutoSphere.Contracts.Messages;

public enum FaultAction
{
    Inject = 0,
    Clear = 1,
}

/// <summary>
/// Test-harness command published to <c>autosphere/simulation/{vehicleId}/faults</c>.
/// Never used for production vehicle control.
/// </summary>
public sealed record FaultInjectionCommand : CorrelatedVehicleMessage
{
    public required FaultType Fault { get; init; }

    public required FaultAction Action { get; init; }

    /// <summary>Target ECU for ECU-specific faults (crash, message loss/delay, invalid value).</summary>
    public string? TargetEcuId { get; init; }

    /// <summary>Optional automatic clear after this many seconds.</summary>
    public int? DurationSeconds { get; init; }

    /// <summary>Additional delay for <see cref="FaultType.CanMessageDelay"/>.</summary>
    public int? DelayMilliseconds { get; init; }
}

/// <summary>Acknowledgement published to <c>autosphere/simulation/{vehicleId}/faults/ack</c>.</summary>
public sealed record FaultInjectionAck : CorrelatedVehicleMessage
{
    public required FaultType Fault { get; init; }

    public required FaultAction Action { get; init; }

    public required bool Accepted { get; init; }

    public required string Message { get; init; }

    /// <summary>Component that executed the fault: <c>simulator</c> or <c>gateway</c>.</summary>
    public required string HandledBy { get; init; }
}
