using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Telemetry;

/// <summary>
/// Hot path for live telemetry (singleton, no database access per message):
/// update the live-state cache, push to dashboards, and hand sampled values to the batch writer.
/// </summary>
public sealed class TelemetryIngestionService(
    VehicleRegistry registry,
    ILiveVehicleStateCache cache,
    IRealtimeNotifier notifier,
    ITelemetrySampleSink sink,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryIngestionService> logger)
{
    /// <summary>Meter for the thesis latency measurements (observe with dotnet-counters).</summary>
    public const string MeterName = "AutoSphere.Backend";

    private static readonly Meter Meter = new(MeterName, "1.0");
    private static readonly Histogram<double> GatewayToBackend = Meter.CreateHistogram<double>("autosphere.backend.gateway_to_backend_latency", "ms",
        "Gateway publish → backend receive (MQTT transport; assumes synchronized clocks)");
    private static readonly Histogram<double> CanToBackend = Meter.CreateHistogram<double>("autosphere.backend.can_to_backend_latency", "ms",
        "Newest CAN frame at the gateway → backend receive");

    private readonly ConcurrentDictionary<(Guid Vehicle, string Path), DateTimeOffset> _lastSampled = new();
    private readonly ConcurrentDictionary<string, long> _lastSequence = new(StringComparer.Ordinal);

    public async Task HandleAsync(TelemetryMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var vehicleKey = await registry.FindAsync(message.VehicleId, cancellationToken);
        if (vehicleKey is null)
        {
            logger.LogDebug("Telemetry from unregistered vehicle {VehicleId} ignored", message.VehicleId);
            return;
        }

        var receivedAt = timeProvider.GetUtcNow();
        Sample(vehicleKey.Value, message.Signals);

        // Messages flushed from the gateway's offline buffer are older than what dashboards already show:
        // persist their samples, but do not move the live view backwards.
        var previous = _lastSequence.GetValueOrDefault(message.VehicleId);
        var isNewest = message.Sequence > previous || message.Sequence < previous - 1000; // gateway restarted
        if (!isNewest)
        {
            return;
        }

        _lastSequence[message.VehicleId] = message.Sequence;
        var state = new VehicleLiveState(message.VehicleId, message.Sequence, message.Timestamp, receivedAt, message.Signals, message.Statistics);
        var dto = VehicleTelemetryDto.From(state);
        GatewayToBackend.Record(dto.Latency.GatewayToBackendMs);
        CanToBackend.Record(dto.Latency.CanToBackendMs);
        await cache.SetAsync(state, cancellationToken);
        await notifier.TelemetryAsync(dto, cancellationToken);
    }

    private void Sample(Guid vehicleKey, IReadOnlyList<SignalValueDto> signals)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.SamplingIntervalSeconds));
        foreach (var signal in signals.Where(s => s.Quality != SignalQuality.Stale))
        {
            var key = (vehicleKey, signal.Path);
            if (_lastSampled.TryGetValue(key, out var last) && signal.Timestamp - last < interval)
            {
                continue;
            }

            _lastSampled[key] = signal.Timestamp;
            sink.Enqueue(TelemetryRecord.Create(vehicleKey, signal.Path, signal.Value, signal.Timestamp));
        }
    }
}
