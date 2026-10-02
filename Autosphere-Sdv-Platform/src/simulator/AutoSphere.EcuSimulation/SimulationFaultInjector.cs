using System.Collections.Concurrent;
using AutoSphere.Contracts.Messages;
using AutoSphere.EcuSimulation.Ecus;
using AutoSphere.SharedKernel.Simulation;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.Extensions.Logging;

namespace AutoSphere.EcuSimulation;

/// <summary>Outcome of a fault-injection request.</summary>
public sealed record FaultInjectionResult(bool Accepted, string Message);

/// <summary>
/// Applies test-harness faults to the simulated ECUs and plant model. Gateway-level faults
/// (<see cref="FaultType.GatewayDisconnect"/>, <see cref="FaultType.MqttDisconnect"/>) and the backend-level
/// <see cref="FaultType.CorruptedOtaPackage"/> are not handled here.
/// </summary>
public sealed class SimulationFaultInjector : IDisposable
{
    private readonly VehicleSimulation _simulation;
    private readonly ILogger<SimulationFaultInjector> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<(FaultType, string?), CancellationTokenSource> _autoClear = new();

    public SimulationFaultInjector(VehicleSimulation simulation, ILogger<SimulationFaultInjector> logger, TimeProvider timeProvider)
    {
        _simulation = simulation;
        _logger = logger;
        _timeProvider = timeProvider;
    }

    public static bool Handles(FaultType fault) => fault is
        FaultType.EcuCrash or FaultType.BatteryOverheat or FaultType.MotorOverheat or FaultType.InvalidSensorValue
        or FaultType.CanMessageLoss or FaultType.CanMessageDelay;

    public FaultInjectionResult Apply(FaultInjectionCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!_simulation.Options.FaultInjectionEnabled)
        {
            return new FaultInjectionResult(false, "Fault injection is disabled for this simulation.");
        }

        if (!Handles(command.Fault))
        {
            return new FaultInjectionResult(false, $"{command.Fault} is not handled by the ECU simulator.");
        }

        var inject = command.Action == FaultAction.Inject;
        var result = command.Fault switch
        {
            FaultType.BatteryOverheat => SetPlant(() => _simulation.Plant.BatteryCoolingFailed = inject,
                inject ? "Battery cooling failure injected: pack temperature will rise." : "Battery cooling restored."),
            FaultType.MotorOverheat => SetPlant(() => _simulation.Plant.MotorCoolingFailed = inject,
                inject ? "Motor cooling failure injected: winding temperature will rise." : "Motor cooling restored."),
            _ => ApplyToEcu(command, inject),
        };

        _logger.LogWarning("Fault injection {Action} {Fault} on {Target}: {Result} (correlation {CorrelationId})",
            command.Action, command.Fault, command.TargetEcuId ?? "vehicle", result.Message, command.CorrelationId);

        if (result.Accepted && inject && command.DurationSeconds is > 0)
        {
            ScheduleAutoClear(command);
        }

        return result;
    }

    public void Dispose()
    {
        foreach (var source in _autoClear.Values)
        {
            source.Cancel();
            source.Dispose();
        }

        _autoClear.Clear();
    }

    private static FaultInjectionResult SetPlant(Action apply, string message)
    {
        apply();
        return new FaultInjectionResult(true, message);
    }

    private FaultInjectionResult ApplyToEcu(FaultInjectionCommand command, bool inject)
    {
        var ecu = command.TargetEcuId is null
            ? DefaultTarget(command.Fault)
            : _simulation.FindEcu(command.TargetEcuId);
        if (ecu is null)
        {
            return new FaultInjectionResult(false, $"ECU '{command.TargetEcuId}' is not part of the simulation.");
        }

        switch (command.Fault)
        {
            case FaultType.EcuCrash when inject:
                ecu.Faults.Crashed = true;
                return new FaultInjectionResult(true, $"{ecu.EcuId} crashed: no CAN traffic, no diagnostic responses.");
            case FaultType.EcuCrash:
                _ = ecu.RecoverAsync();
                return new FaultInjectionResult(true, $"{ecu.EcuId} recovering (reboot).");
            case FaultType.InvalidSensorValue when ecu.Type == EcuType.BodyControlModule:
                return new FaultInjectionResult(false, $"{ecu.EcuId} has no analog sensor whose value could be corrupted.");
            case FaultType.InvalidSensorValue:
                ecu.Faults.InvalidSensorValue = inject;
                return new FaultInjectionResult(true, inject ? $"{ecu.EcuId} now reports an implausible sensor value." : $"{ecu.EcuId} sensor value restored.");
            case FaultType.CanMessageLoss:
                ecu.Faults.MessageLoss = inject;
                return new FaultInjectionResult(true, inject ? $"{ecu.EcuId} stopped transmitting cyclic messages." : $"{ecu.EcuId} transmits again.");
            case FaultType.CanMessageDelay:
                ecu.Faults.MessageDelayMs = inject ? Math.Clamp(command.DelayMilliseconds ?? 800, 1, 10_000) : 0;
                return new FaultInjectionResult(true, inject ? $"{ecu.EcuId} delays its messages by {ecu.Faults.MessageDelayMs} ms." : $"{ecu.EcuId} message timing restored.");
            default:
                return new FaultInjectionResult(false, $"{command.Fault} cannot be applied to an ECU.");
        }
    }

    private EcuSimulator? DefaultTarget(FaultType fault) => fault switch
    {
        FaultType.InvalidSensorValue => _simulation.FindEcu(EcuType.BatteryManagementSystem),
        _ => _simulation.FindEcu(EcuType.MotorControlUnit),
    };

    private void ScheduleAutoClear(FaultInjectionCommand command)
    {
        var key = (command.Fault, command.TargetEcuId);
        var source = new CancellationTokenSource();
        if (_autoClear.TryRemove(key, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }

        _autoClear[key] = source;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(command.DurationSeconds!.Value), _timeProvider, source.Token);
                Apply(command with { Action = FaultAction.Clear, DurationSeconds = null });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}
