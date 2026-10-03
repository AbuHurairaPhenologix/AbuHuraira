using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Domain.Services;

/// <summary>
/// Classifies thermal risk from the current estimate and the forecast:
/// <list type="bullet">
/// <item>Critical — current estimate ≥ T_safe, or forecast ≥ T_critical;</item>
/// <item>Warning — forecast ≥ T_safe;</item>
/// <item>Elevated — estimate or forecast within the warning band below T_safe;</item>
/// <item>Normal — otherwise.</item>
/// </list>
/// </summary>
public static class ThermalRiskPolicy
{
    public static ThermalRisk Classify(double currentMax, double? forecastPeak, double safeLimit, double criticalLimit, double warningBand = 5.0)
    {
        var forecast = forecastPeak ?? currentMax;
        if (currentMax >= safeLimit || forecast >= criticalLimit)
        {
            return ThermalRisk.Critical;
        }

        if (forecast >= safeLimit)
        {
            return ThermalRisk.Warning;
        }

        return Math.Max(currentMax, forecast) >= safeLimit - warningBand ? ThermalRisk.Elevated : ThermalRisk.Normal;
    }
}
