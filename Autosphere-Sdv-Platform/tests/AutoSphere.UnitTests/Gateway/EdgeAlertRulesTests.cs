using AutoSphere.Contracts.Messages;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Monitoring;
using AutoSphere.VehicleGateway.Network;

namespace AutoSphere.UnitTests.Gateway;

public sealed class EdgeAlertRulesTests
{
    private static readonly EdgeThresholdOptions Thresholds = new();

    [Theory]
    [InlineData(38.0, null)]
    [InlineData(52.0, DtcSeverity.Warning)]
    [InlineData(61.0, DtcSeverity.Critical)]
    public void Battery_temperature_thresholds(double temperature, DtcSeverity? expected)
    {
        var alerts = Evaluate([Battery(temperature)], new Dictionary<string, DtcSeverity>());

        Assert.Equal(expected, alerts.SingleOrDefault(a => a.Key == "battery-temperature")?.Severity);
    }

    [Fact]
    public void Hysteresis_keeps_alert_until_value_falls_clearly_below_threshold()
    {
        var active = new Dictionary<string, DtcSeverity> { ["battery-temperature"] = DtcSeverity.Critical };

        Assert.Equal(DtcSeverity.Critical, Evaluate([Battery(59.0)], active).Single().Severity);
        Assert.Equal(DtcSeverity.Warning, Evaluate([Battery(57.0)], active).Single().Severity);

        active["battery-temperature"] = DtcSeverity.Warning;
        Assert.Single(Evaluate([Battery(49.0)], active));
        Assert.Empty(Evaluate([Battery(47.5)], active));
    }

    [Fact]
    public void Out_of_range_values_raise_plausibility_alert_instead_of_thermal_alert()
    {
        var alerts = Evaluate([Battery(3000.0) with { Quality = SignalQuality.OutOfRange }], new Dictionary<string, DtcSeverity>());

        var alert = Assert.Single(alerts);
        Assert.Equal(AlertCategory.SignalPlausibility, alert.Category);
    }

    private static List<AlertCondition> Evaluate(IReadOnlyList<SignalValueDto> signals, IReadOnlyDictionary<string, DtcSeverity> active) =>
        EdgeAlertRules.Evaluate(signals, Array.Empty<EcuNode>(), Thresholds, active).ToList();

    private static SignalValueDto Battery(double value) =>
        new(VssPaths.BatteryTemperature, "BatteryTemperature", value, "°C", "BMS-001", DateTimeOffset.UtcNow, SignalQuality.Valid);
}
