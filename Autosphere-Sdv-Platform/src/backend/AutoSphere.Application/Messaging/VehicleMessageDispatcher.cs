using System.Text.Json;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AutoSphere.Application.Messaging;

/// <summary>
/// Entry point for every message received from vehicles. Validates the sender against the registry,
/// deserializes the typed DTO and invokes the matching use case in its own DI scope.
/// </summary>
public sealed class VehicleMessageDispatcher(
    IServiceScopeFactory scopeFactory,
    VehicleRegistry registry,
    TelemetryIngestionService telemetry,
    ILogger<VehicleMessageDispatcher> logger)
{
    public async Task DispatchAsync(VehicleTopicKind kind, string vehicleId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        try
        {
            if (kind == VehicleTopicKind.Telemetry)
            {
                // Hot path: singleton service, no scope and no database access.
                var message = AutoSphereJson.Deserialize<TelemetryMessage>(payload.Span);
                if (EnsureConsistent(message, vehicleId))
                {
                    await telemetry.HandleAsync(message, cancellationToken);
                }

                return;
            }

            var vehicleKey = await registry.FindAsync(vehicleId, cancellationToken);
            if (vehicleKey is null)
            {
                logger.LogDebug("{Kind} message from unregistered vehicle {VehicleId} ignored", kind, vehicleId);
                return;
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var services = scope.ServiceProvider;
            switch (kind)
            {
                case VehicleTopicKind.Status:
                    await Handle<VehicleStatusMessage>(payload, vehicleId, m => services.GetRequiredService<VehicleStatusIngestionService>().HandleAsync(m, vehicleKey.Value, cancellationToken));
                    break;
                case VehicleTopicKind.Dtcs:
                    await Handle<DtcReportMessage>(payload, vehicleId, m => services.GetRequiredService<DtcIngestionService>().HandleAsync(m, vehicleKey.Value, cancellationToken));
                    break;
                case VehicleTopicKind.Alerts:
                    await Handle<AlertMessage>(payload, vehicleId, async m =>
                    {
                        await services.GetRequiredService<AlertService>().HandleAsync(m, vehicleKey.Value, cancellationToken);
                        await services.GetRequiredService<Health.VehicleHealthService>().EvaluateAsync(vehicleKey.Value, cancellationToken);
                    });
                    break;
                case VehicleTopicKind.DiagnosticResponse:
                    await Handle<DiagnosticResponseMessage>(payload, vehicleId, m => services.GetRequiredService<DiagnosticService>().HandleResponseAsync(m, vehicleKey.Value, cancellationToken));
                    break;
                case VehicleTopicKind.OtaStatus:
                    await Handle<OtaStatusMessage>(payload, vehicleId, m => services.GetRequiredService<OtaStatusIngestionService>().HandleAsync(m, vehicleKey.Value, cancellationToken));
                    break;
                case VehicleTopicKind.FaultInjectionAck:
                    await Handle<FaultInjectionAck>(payload, vehicleId, m => services.GetRequiredService<FaultInjectionService>().HandleAckAsync(m, vehicleKey.Value, cancellationToken));
                    break;
                default:
                    logger.LogDebug("No handler for {Kind}", kind);
                    break;
            }
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Rejected malformed {Kind} message from {VehicleId}", kind, vehicleId);
        }
    }

    private async Task Handle<T>(ReadOnlyMemory<byte> payload, string topicVehicleId, Func<T, Task> handler)
        where T : VehicleMessage
    {
        var message = AutoSphereJson.Deserialize<T>(payload.Span);
        if (EnsureConsistent(message, topicVehicleId))
        {
            await handler(message);
        }
    }

    /// <summary>The vehicle id in the payload must match the topic (prevents cross-vehicle spoofing via payload).</summary>
    private bool EnsureConsistent(VehicleMessage message, string topicVehicleId)
    {
        if (string.Equals(message.VehicleId, topicVehicleId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        logger.LogWarning("Message on topic of {TopicVehicleId} claims vehicle {PayloadVehicleId}; rejected", topicVehicleId, message.VehicleId);
        return false;
    }
}
