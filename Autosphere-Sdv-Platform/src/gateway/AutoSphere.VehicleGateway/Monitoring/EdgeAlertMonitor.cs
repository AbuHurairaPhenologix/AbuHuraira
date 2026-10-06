using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleGateway.Signals;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Monitoring;

/// <summary>A condition evaluated by the edge monitor.</summary>
public sealed record AlertCondition(string Key, DtcSeverity Severity, AlertCategory Category, string Message, string? EcuId = null,
    string? SignalPath = null, double? Value = null, double? Threshold = null);

/// <summary>
/// Pure evaluation of edge alert rules. Separated from the hosted service so the rules are unit-testable.
/// </summary>
public static class EdgeAlertRules
{
    public static IEnumerable<AlertCondition> Evaluate(
        IReadOnlyList<SignalValueDto> signals,
        IReadOnlyList<EcuNode> ecus,
        EdgeThresholdOptions thresholds,
        IReadOnlyDictionary<string, DtcSeverity> activeAlerts)
    {
        var byPath = signals.ToDictionary(s => s.Path, StringComparer.Ordinal);

        foreach (var condition in Temperature(byPath, VssPaths.BatteryTemperature, "battery-temperature", "Battery temperature",
                     thresholds.BatteryTemperatureWarning, thresholds.BatteryTemperatureCritical, thresholds.Hysteresis, activeAlerts))
        {
            yield return condition;
        }

        foreach (var condition in Temperature(byPath, VssPaths.MotorTemperature, "motor-temperature", "Motor temperature",
                     thresholds.MotorTemperatureWarning, thresholds.MotorTemperatureCritical, thresholds.Hysteresis, activeAlerts))
        {
            yield return condition;
        }

        foreach (var signal in signals.Where(s => s.Quality == SignalQuality.OutOfRange))
        {
            yield return new AlertCondition($"implausible-{signal.Name}", DtcSeverity.Warning, AlertCategory.SignalPlausibility,
                $"{signal.Name} value {signal.Value} {signal.Unit} is outside its physical range", signal.SourceEcuId, signal.Path, signal.Value);
        }

        foreach (var ecu in ecus.Where(e => e.Status == EcuStatus.Offline))
        {
            var severity = ecu.Type is EcuType.BodyControlModule ? DtcSeverity.Warning : DtcSeverity.Critical;
            yield return new AlertCondition($"communication-lost-{ecu.EcuId}", severity, AlertCategory.Communication,
                $"Lost communication with {ecu.Name} ({ecu.EcuId})", ecu.EcuId);
        }
    }

    private static IEnumerable<AlertCondition> Temperature(
        Dictionary<string, SignalValueDto> byPath, string path, string key, string label,
        double warning, double critical, double hysteresis, IReadOnlyDictionary<string, DtcSeverity> active)
    {
        if (!byPath.TryGetValue(path, out var signal) || signal.Quality == SignalQuality.OutOfRange)
        {
            yield break;
        }

        var current = active.TryGetValue(key, out var severity) ? severity : (DtcSeverity?)null;
        var value = signal.Value;
        DtcSeverity? level = value >= critical || (current == DtcSeverity.Critical && value > critical - hysteresis) ? DtcSeverity.Critical
            : value >= warning || (current is not null && value > warning - hysteresis) ? DtcSeverity.Warning
            : null;
        if (level is { } l)
        {
            var threshold = l == DtcSeverity.Critical ? critical : warning;
            yield return new AlertCondition(key, l, AlertCategory.Thermal,
                $"{label} {value:0.0} {signal.Unit} exceeds the {l.ToString().ToLowerInvariant()} threshold of {threshold} {signal.Unit}",
                signal.SourceEcuId, path, value, threshold);
        }
    }
}

/// <summary>
/// Evaluates edge alert rules and publishes <see cref="AlertMessage"/>s on rising and falling edges only,
/// so the cloud is not flooded with repeated alerts.
/// </summary>
public sealed class EdgeAlertMonitor(
    VehicleSignalStore store,
    EcuNetworkMonitor network,
    GatewayMqttClient mqtt,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<EdgeAlertMonitor> logger) : BackgroundService
{
    private readonly Dictionary<string, AlertCondition> _active = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = timeProvider.GetUtcNow();
            var conditions = EdgeAlertRules.Evaluate(store.Snapshot(now), network.Nodes, options.Value.Thresholds,
                    _active.ToDictionary(a => a.Key, a => a.Value.Severity, StringComparer.Ordinal))
                .GroupBy(c => c.Key).Select(g => g.First()).ToDictionary(c => c.Key, StringComparer.Ordinal);

            foreach (var condition in conditions.Values)
            {
                if (!_active.TryGetValue(condition.Key, out var previous) || previous.Severity != condition.Severity)
                {
                    _active[condition.Key] = condition;
                    Publish(condition, AlertState.Raised, now);
                }
            }

            foreach (var key in _active.Keys.Where(k => !conditions.ContainsKey(k)).ToList())
            {
                Publish(_active[key], AlertState.Cleared, now);
                _active.Remove(key);
            }
        }
    }

    private void Publish(AlertCondition condition, AlertState state, DateTimeOffset now)
    {
        logger.Log(state == AlertState.Raised && condition.Severity == DtcSeverity.Critical ? LogLevel.Warning : LogLevel.Information,
            "Edge alert {AlertKey} {State} ({Severity}): {Message}", condition.Key, state, condition.Severity, condition.Message);
        mqtt.Publish(MqttTopics.Alerts(options.Value.VehicleId), new AlertMessage
        {
            VehicleId = options.Value.VehicleId,
            Timestamp = now,
            AlertKey = condition.Key,
            State = state,
            Severity = condition.Severity,
            Category = condition.Category,
            Message = condition.Message,
            EcuId = condition.EcuId,
            SignalPath = condition.SignalPath,
            Value = condition.Value,
            Threshold = condition.Threshold,
        });
    }
}
