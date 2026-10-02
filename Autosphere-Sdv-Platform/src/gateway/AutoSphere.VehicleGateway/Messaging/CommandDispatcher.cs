using System.Text.Json;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.VehicleGateway.Ota;
using AutoSphere.VehicleGateway.RemoteDiagnostics;
using AutoSphere.VehicleGateway.Simulation;

namespace AutoSphere.VehicleGateway.Messaging;

/// <summary>
/// Routes commands received from the cloud to their handlers. Each command runs on its own task so a
/// long OTA update does not block diagnostic requests.
/// </summary>
public sealed class CommandDispatcher(
    GatewayMqttClient mqtt,
    DiagnosticRequestHandler diagnostics,
    OtaUpdateAgent ota,
    FaultCommandHandler faults,
    ILogger<CommandDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in mqtt.Inbound.ReadAllAsync(stoppingToken))
        {
            _ = Task.Run(() => DispatchAsync(message, stoppingToken), stoppingToken);
        }
    }

    private async Task DispatchAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        try
        {
            switch (message.Kind)
            {
                case VehicleTopicKind.DiagnosticRequest:
                    await diagnostics.HandleAsync(AutoSphereJson.Deserialize<DiagnosticRequestMessage>(message.Payload), cancellationToken);
                    break;
                case VehicleTopicKind.OtaCommand:
                    await ota.HandleAsync(AutoSphereJson.Deserialize<OtaUpdateCommand>(message.Payload), cancellationToken);
                    break;
                case VehicleTopicKind.FaultInjection:
                    await faults.HandleAsync(AutoSphereJson.Deserialize<FaultInjectionCommand>(message.Payload), cancellationToken);
                    break;
                default:
                    logger.LogDebug("Ignoring message on {Topic}", message.Topic);
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected malformed command on {Topic}", message.Topic);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Command on {Topic} failed", message.Topic);
        }
    }
}
