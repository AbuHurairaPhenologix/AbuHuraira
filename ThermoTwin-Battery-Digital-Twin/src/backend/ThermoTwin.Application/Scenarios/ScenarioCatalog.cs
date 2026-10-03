using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Application.Scenarios;

/// <summary>Built-in scenarios.</summary>
public static class ScenarioCatalog
{
    public const string DemoKey = "rapid-charge-hidden-hotspot";

    /// <summary>
    /// Flagship demo: a large-format pouch cell at 25 °C starts a fast CC–CV charge. A localised
    /// high-resistance defect produces a hidden hotspot between the twelve thermistors. The twin
    /// reconstructs the source, forecasts the peak and drives the cold plate with MPC.
    /// </summary>
    public static ScenarioDefinition RapidChargeHiddenHotspot { get; } = new()
    {
        Key = DemoKey,
        Name = "Rapid Charging with Hidden Battery Hotspot",
        Description =
            "A 200 × 100 mm Li-ion pouch cell starts a 30-minute fast charge at 25 °C ambient. A high-resistance " +
            "defect creates a hidden internal heat source between the 12 thermistors. ThermoTwin reconstructs the " +
            "source from noisy sparse measurements, forecasts the peak temperature and optimises cold-plate cooling " +
            "to keep the cell below 45 °C.",
    };

    public static ScenarioDefinition HealthyCell { get; } = RapidChargeHiddenHotspot with
    {
        Key = "healthy-cell-nominal-charge",
        Name = "Healthy Cell — Nominal Charge",
        Description = "Same fast charge without a defect: uniform Joule heating only. Useful as a reference for false-alarm behaviour.",
        HeatSource = RapidChargeHiddenHotspot.HeatSource with { Hotspots = [] },
    };

    public static ScenarioDefinition DualHotspotStress { get; } = RapidChargeHiddenHotspot with
    {
        Key = "dual-hotspot-stress",
        Name = "Dual Hotspot Stress Test",
        Description = "Two defects of different strength near opposite tabs, with noisier sensors — a harder inverse problem.",
        HeatSource = RapidChargeHiddenHotspot.HeatSource with
        {
            Hotspots = [new(0.050, 0.035, 300_000, 0.011), new(0.150, 0.068, 380_000, 0.012)],
        },
        Sensors = new SensorSettings(Count: 12, NoiseStd: 0.2, Seed: 7),
    };

    public static ScenarioDefinition AdvisoryOnly { get; } = RapidChargeHiddenHotspot with
    {
        Key = "rapid-charge-advisory",
        Name = "Rapid Charging — Advisory Mode",
        Description = "The optimiser recommends cooling but the baseline 15 % level is kept, showing the unmitigated thermal excursion.",
        Cooling = RapidChargeHiddenHotspot.Cooling with { Mode = CoolingMode.Advisory },
    };

    public static IReadOnlyList<ScenarioDefinition> All { get; } =
        [RapidChargeHiddenHotspot, AdvisoryOnly, HealthyCell, DualHotspotStress];

    public static ScenarioDefinition? Find(string key) =>
        All.FirstOrDefault(s => string.Equals(s.Key, key, StringComparison.OrdinalIgnoreCase));
}
