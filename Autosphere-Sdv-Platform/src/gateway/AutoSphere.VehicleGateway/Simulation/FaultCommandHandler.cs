using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.EcuSimulation;
using AutoSphere.SharedKernel.Simulation;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Simulation;

/// <summary>
/// Handles test-harness fault injection. Gateway-level faults are executed here; ECU/plant faults are
/// forwarded to the in-process simulation when the gateway hosts it (otherwise the standalone simulator
/// subscribes to the same topic and handles them).
/// </summary>
public sealed class FaultCommandHandler(
    GatewayMqttClient mqtt,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<FaultCommandHandler> logger,
    SimulationFaultInjector? simulation = null)
{
    private const int DefaultOutageSeconds = 15;

    public async Task HandleAsync(FaultInjectionCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        FaultInjectionResult? result = null;
        var handledBy = "gateway";

        switch (command.Fault)
        {
            case FaultType.GatewayDisconnect or FaultType.MqttDisconnect when !options.Value.FaultInjectionEnabled:
                Acknowledge(command, false, "Gateway fault injection is disabled (Gateway:FaultInjectionEnabled).", handledBy);
                return;

            case FaultType.GatewayDisconnect or FaultType.MqttDisconnect when command.Action == FaultAction.Inject:
                var seconds = Math.Clamp(command.DurationSeconds ?? DefaultOutageSeconds, 1, 600);
                var withWill = command.Fault == FaultType.GatewayDisconnect;

                // Acknowledge first: once disconnected, the acknowledgement could only be sent after the outage.
                Acknowledge(command, true, withWill
                    ? $"Gateway drops its cloud connection abruptly for {seconds} s (last will → offline)."
                    : $"MQTT session interrupted for {seconds} s; data is buffered and flushed on reconnect.", handledBy);
                await Task.Delay(TimeSpan.FromMilliseconds(300), timeProvider, cancellationToken);
                await mqtt.SuspendAsync(TimeSpan.FromSeconds(seconds), withWill);
                return;

            case FaultType.GatewayDisconnect or FaultType.MqttDisconnect:
                mqtt.Resume();
                result = new FaultInjectionResult(true, "Cloud connection restored.");
                break;

            case FaultType.CorruptedOtaPackage:
                return; // executed by the backend when it sends the package

            default:
                if (simulation is null)
                {
                    return; // standalone simulator process handles ECU faults
                }

                handledBy = "simulator";
                result = simulation.Apply(command);
                break;
        }

        Acknowledge(command, result.Accepted, result.Message, handledBy);
    }

    private void Acknowledge(FaultInjectionCommand command, bool accepted, string message, string handledBy)
    {
        logger.LogWarning("Fault {Fault} {Action}: {Message} (correlation {CorrelationId})", command.Fault, command.Action, message, command.CorrelationId);
        mqtt.Publish(MqttTopics.FaultInjectionAck(options.Value.VehicleId), new FaultInjectionAck
        {
            VehicleId = options.Value.VehicleId,
            Timestamp = timeProvider.GetUtcNow(),
            CorrelationId = command.CorrelationId,
            Fault = command.Fault,
            Action = command.Action,
            Accepted = accepted,
            Message = message,
            HandledBy = handledBy,
        });
    }
}
