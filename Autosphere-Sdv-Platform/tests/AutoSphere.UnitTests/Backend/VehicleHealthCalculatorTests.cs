using AutoSphere.Domain.Vehicles;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.UnitTests.Backend;

public sealed class VehicleHealthCalculatorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly HealthThresholds Thresholds = new();

    private static readonly EcuHealthInput[] HealthyEcus =
    [
        new("VCU-001", EcuType.VehicleControlUnit, EcuStatus.Online, 0),
        new("MCU-001", EcuType.MotorControlUnit, EcuStatus.Online, 0),
        new("BMS-001", EcuType.BatteryManagementSystem, EcuStatus.Online, 0),
        new("BCM-001", EcuType.BodyControlModule, EcuStatus.Online, 0),
    ];

    [Fact]
    public void Nominal_vehicle_is_healthy_with_full_score()
    {
        var result = Evaluate();

        Assert.Equal(HealthStatus.Healthy, result.Status);
        Assert.Equal(100, result.Score);
        Assert.Empty(result.Reasons);
    }

    [Fact]
    public void Gateway_offline_means_vehicle_offline()
    {
        Assert.Equal(HealthStatus.Offline, Evaluate(connectivity: ConnectivityStatus.Offline).Status);
    }

    [Fact]
    public void Missing_or_old_telemetry_means_offline()
    {
        Assert.Equal(HealthStatus.Offline, Evaluate(telemetryAge: null).Status);
        Assert.Equal(HealthStatus.Offline, Evaluate(telemetryAge: TimeSpan.FromSeconds(31)).Status);
    }

    [Fact]
    public void Stale_telemetry_is_a_warning()
    {
        var result = Evaluate(telemetryAge: TimeSpan.FromSeconds(15));

        Assert.Equal(HealthStatus.Warning, result.Status);
        Assert.Equal(80, result.Score);
    }

    [Theory]
    [InlineData(45.0, HealthStatus.Healthy)]
    [InlineData(52.0, HealthStatus.Warning)]
    [InlineData(65.0, HealthStatus.Critical)]
    public void Battery_temperature_thresholds(double temperature, HealthStatus expected)
    {
        Assert.Equal(expected, Evaluate(batteryTemperature: temperature).Status);
    }

    [Fact]
    public void Active_critical_dtc_makes_vehicle_critical()
    {
        var result = Evaluate(dtcs: [("P0A7E", DtcSeverity.Critical)]);

        Assert.Equal(HealthStatus.Critical, result.Status);
        Assert.Equal(60, result.Score);
        Assert.Contains(result.Reasons, r => r.Contains("Battery Thermal Fault", StringComparison.Ordinal));
    }

    [Fact]
    public void Warning_dtc_degrades_to_warning()
    {
        Assert.Equal(HealthStatus.Warning, Evaluate(dtcs: [("U0140", DtcSeverity.Warning)]).Status);
    }

    [Fact]
    public void Offline_powertrain_ecu_is_critical_but_offline_body_ecu_only_a_warning()
    {
        var bmsOffline = HealthyEcus.Select(e => e.EcuId == "BMS-001" ? e with { Status = EcuStatus.Offline } : e).ToArray();
        var bcmOffline = HealthyEcus.Select(e => e.EcuId == "BCM-001" ? e with { Status = EcuStatus.Offline } : e).ToArray();

        Assert.Equal(HealthStatus.Critical, Evaluate(ecus: bmsOffline).Status);
        Assert.Equal(HealthStatus.Warning, Evaluate(ecus: bcmOffline).Status);
    }

    [Fact]
    public void Penalties_accumulate_and_score_is_clamped()
    {
        var result = Evaluate(dtcs: [("P0A7E", DtcSeverity.Critical), ("P0A2F", DtcSeverity.Critical), ("P0606", DtcSeverity.Critical)], batteryTemperature: 80);

        Assert.Equal(HealthStatus.Critical, result.Status);
        Assert.Equal(0, result.Score);
    }

    [Fact]
    public void Recent_communication_errors_reduce_score()
    {
        var result = Evaluate(communicationErrors: 3);

        Assert.Equal(HealthStatus.Warning, result.Status);
        Assert.Equal(97, result.Score);
    }

    private static readonly TimeSpan FreshTelemetry = TimeSpan.FromSeconds(1);

    /// <param name="telemetryAge">Age of the last telemetry; <c>null</c> means none was ever received.</param>
    private static HealthAssessment Evaluate(
        ConnectivityStatus connectivity = ConnectivityStatus.Online,
        EcuHealthInput[]? ecus = null,
        (string, DtcSeverity)[]? dtcs = null,
        double? batteryTemperature = 38,
        double? motorTemperature = 67,
        long communicationErrors = 0) =>
        Evaluate(FreshTelemetry, connectivity, ecus, dtcs, batteryTemperature, motorTemperature, communicationErrors);

    private static HealthAssessment Evaluate(
        TimeSpan? telemetryAge,
        ConnectivityStatus connectivity = ConnectivityStatus.Online,
        EcuHealthInput[]? ecus = null,
        (string, DtcSeverity)[]? dtcs = null,
        double? batteryTemperature = 38,
        double? motorTemperature = 67,
        long communicationErrors = 0) =>
        VehicleHealthCalculator.Evaluate(new HealthInput(
            connectivity,
            telemetryAge is { } age ? Now - age : null,
            Now,
            ecus ?? HealthyEcus,
            dtcs ?? [],
            batteryTemperature,
            motorTemperature,
            communicationErrors), Thresholds);
}
