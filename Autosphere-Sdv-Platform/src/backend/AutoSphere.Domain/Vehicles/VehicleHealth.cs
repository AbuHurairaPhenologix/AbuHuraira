using System.Globalization;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Domain.Vehicles;

/// <summary>Result of the vehicle health calculation.</summary>
public sealed record HealthAssessment(HealthStatus Status, int Score, IReadOnlyList<string> Reasons)
{
    public string Summary => Reasons.Count == 0 ? "All monitored conditions nominal." : string.Join(" ", Reasons);
}

/// <summary>Thresholds used by the health calculation.</summary>
public sealed class HealthThresholds
{
    public double BatteryTemperatureWarningC { get; set; } = 50;

    public double BatteryTemperatureCriticalC { get; set; } = 60;

    public double MotorTemperatureWarningC { get; set; } = 110;

    public double MotorTemperatureCriticalC { get; set; } = 130;

    /// <summary>Telemetry older than this is considered stale (warning).</summary>
    public int StaleTelemetrySeconds { get; set; } = 10;

    /// <summary>Telemetry older than this means the vehicle is offline.</summary>
    public int OfflineAfterSeconds { get; set; } = 30;
}

/// <summary>Per-ECU input for the health calculation.</summary>
public sealed record EcuHealthInput(string EcuId, EcuType Type, EcuStatus Status, long CommunicationErrors);

/// <summary>Everything the health calculation looks at.</summary>
public sealed record HealthInput(
    ConnectivityStatus Connectivity,
    DateTimeOffset? LastTelemetryAt,
    DateTimeOffset Now,
    IReadOnlyList<EcuHealthInput> Ecus,
    IReadOnlyList<(string Code, DtcSeverity Severity)> ActiveDtcs,
    double? BatteryTemperatureC,
    double? MotorTemperatureC,
    long RecentCommunicationErrors);

/// <summary>
/// Rule-based vehicle health calculation.
/// </summary>
/// <remarks>
/// <para>Score: start at 100 and subtract penalties; the score is informative, the status is decided by rules:</para>
/// <list type="table">
/// <item><term>Offline</term><description>gateway offline, or no telemetry for <c>OfflineAfterSeconds</c>.</description></item>
/// <item><term>Critical</term><description>any active critical DTC, a temperature above its critical threshold,
/// or an unreachable powertrain ECU (VCU, MCU, BMS).</description></item>
/// <item><term>Warning</term><description>any active warning DTC, a temperature above its warning threshold,
/// an unreachable or degraded non-critical ECU, stale telemetry or recent communication errors.</description></item>
/// <item><term>Healthy</term><description>none of the above.</description></item>
/// </list>
/// <para>Penalties: critical DTC −40, warning DTC −10, critical temperature −40, warning temperature −15,
/// offline powertrain ECU −35, offline other ECU −15, degraded ECU −10, stale telemetry −20,
/// communication errors −1 each (max −10). The score is clamped to [0, 100].</para>
/// </remarks>
public static class VehicleHealthCalculator
{
    public static HealthAssessment Evaluate(HealthInput input, HealthThresholds thresholds)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(thresholds);

        var telemetryAge = input.LastTelemetryAt is { } last ? input.Now - last : (TimeSpan?)null;
        if (input.Connectivity != ConnectivityStatus.Online || telemetryAge is null || telemetryAge > TimeSpan.FromSeconds(thresholds.OfflineAfterSeconds))
        {
            var reason = input.Connectivity == ConnectivityStatus.Offline
                ? "Vehicle gateway is offline."
                : telemetryAge is null ? "No telemetry received yet." : $"No telemetry for {telemetryAge.Value.TotalSeconds:0} s.";
            return new HealthAssessment(HealthStatus.Offline, 0, [reason]);
        }

        var score = 100;
        var critical = false;
        var warning = false;
        var reasons = new List<string>();

        foreach (var (code, severity) in input.ActiveDtcs)
        {
            var definition = KnownDtcs.Describe(SharedKernel.Diagnostics.DtcCode.Parse(code));
            if (severity == DtcSeverity.Critical)
            {
                critical = true;
                score -= 40;
                reasons.Add($"Critical DTC {code}: {definition.FaultCategory}.");
            }
            else if (severity == DtcSeverity.Warning)
            {
                warning = true;
                score -= 10;
                reasons.Add($"Warning DTC {code}: {definition.FaultCategory}.");
            }
        }

        Temperature("Battery", input.BatteryTemperatureC, thresholds.BatteryTemperatureWarningC, thresholds.BatteryTemperatureCriticalC);
        Temperature("Motor", input.MotorTemperatureC, thresholds.MotorTemperatureWarningC, thresholds.MotorTemperatureCriticalC);

        foreach (var ecu in input.Ecus)
        {
            var powertrain = ecu.Type is EcuType.VehicleControlUnit or EcuType.MotorControlUnit or EcuType.BatteryManagementSystem;
            switch (ecu.Status)
            {
                case EcuStatus.Offline when powertrain:
                    critical = true;
                    score -= 35;
                    reasons.Add($"{ecu.EcuId} is not communicating.");
                    break;
                case EcuStatus.Offline:
                    warning = true;
                    score -= 15;
                    reasons.Add($"{ecu.EcuId} is not communicating.");
                    break;
                case EcuStatus.Warning:
                    warning = true;
                    score -= 10;
                    reasons.Add($"{ecu.EcuId} reports degraded communication.");
                    break;
            }
        }

        if (telemetryAge > TimeSpan.FromSeconds(thresholds.StaleTelemetrySeconds))
        {
            warning = true;
            score -= 20;
            reasons.Add($"Telemetry is stale ({telemetryAge.Value.TotalSeconds:0} s old).");
        }

        if (input.RecentCommunicationErrors > 0)
        {
            score -= (int)Math.Min(10, input.RecentCommunicationErrors);
            warning = true;
            reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{input.RecentCommunicationErrors} recent CAN communication errors."));
        }

        var status = critical || score < 40 ? HealthStatus.Critical
            : warning || score < 80 ? HealthStatus.Warning
            : HealthStatus.Healthy;
        return new HealthAssessment(status, Math.Clamp(score, 0, 100), reasons);

        void Temperature(string label, double? value, double warningThreshold, double criticalThreshold)
        {
            if (value is not { } t)
            {
                return;
            }

            if (t >= criticalThreshold)
            {
                critical = true;
                score -= 40;
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{label} temperature {t:0.0} °C ≥ critical {criticalThreshold} °C."));
            }
            else if (t >= warningThreshold)
            {
                warning = true;
                score -= 15;
                reasons.Add(string.Create(CultureInfo.InvariantCulture, $"{label} temperature {t:0.0} °C ≥ warning {warningThreshold} °C."));
            }
        }
    }
}

/// <summary>Persisted history of health changes.</summary>
public sealed class VehicleHealthSnapshot
{
    private VehicleHealthSnapshot()
    {
    }

    public long Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    public DateTimeOffset Timestamp { get; private set; }

    public HealthStatus Status { get; private set; }

    public int Score { get; private set; }

    public string Summary { get; private set; } = string.Empty;

    public static VehicleHealthSnapshot Create(Guid vehicleKey, HealthAssessment assessment, DateTimeOffset at) => new()
    {
        VehicleKey = vehicleKey,
        Timestamp = at,
        Status = assessment.Status,
        Score = assessment.Score,
        Summary = assessment.Summary,
    };
}
