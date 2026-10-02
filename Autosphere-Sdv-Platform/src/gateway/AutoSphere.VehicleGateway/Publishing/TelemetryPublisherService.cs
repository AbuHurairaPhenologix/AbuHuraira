using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Instrumentation;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Signals;
using Microsoft.Extensions.Options;
using MQTTnet.Protocol;

namespace AutoSphere.VehicleGateway.Publishing;

/// <summary>
/// Edge down-sampling: the CAN bus carries ~150 frames/s, the cloud receives one aggregated snapshot of
/// all signals every <see cref="GatewayOptions.TelemetryPublishIntervalMs"/> (QoS 0: live telemetry is
/// superseded by the next sample, so a lost message is acceptable).
/// </summary>
public sealed class TelemetryPublisherService(
    VehicleSignalStore store,
    GatewayMqttClient mqtt,
    GatewayMetrics metrics,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider) : BackgroundService
{
    private long _sequence;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var gateway = options.Value;
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(gateway.TelemetryPublishIntervalMs), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            var signals = store.Snapshot(now);
            if (signals.Count == 0)
            {
                continue;
            }

            var window = metrics.TakeWindow();
            mqtt.Publish(MqttTopics.Telemetry(gateway.VehicleId), new TelemetryMessage
            {
                VehicleId = gateway.VehicleId,
                Timestamp = now,
                Sequence = Interlocked.Increment(ref _sequence),
                Signals = signals,
                Statistics = new GatewayStatisticsDto(window.Received, window.Decoded, window.Rejected,
                    Math.Round(window.FramesPerSecond, 1), Math.Round(window.AverageDecodeUs, 2), Math.Round(window.MaxDecodeUs, 2)),
            }, MqttQualityOfServiceLevel.AtMostOnce);

            var newest = signals.Max(s => s.Timestamp);
            metrics.CanToMqttLatency((now - newest).TotalMilliseconds);
        }
    }
}
