using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Domain.Entities;

/// <summary>Persisted, down-sampled time-series point of a simulation run.</summary>
public sealed class SimulationSnapshot
{
    public long Id { get; set; }

    public Guid RunId { get; set; }

    public double Time { get; set; }

    public double MaxTemperature { get; set; }

    public double MeanTemperature { get; set; }

    public double MinTemperature { get; set; }

    public double? EstimatedMaxTemperature { get; set; }

    public double CounterfactualMaxTemperature { get; set; }

    public double? PredictedPeakTemperature { get; set; }

    public double CoolingLevel { get; set; }

    public double CoolingPowerWatts { get; set; }

    public double CumulativeEnergyJoules { get; set; }

    public double? TemperatureRmse { get; set; }

    public double? SourceRelativeError { get; set; }

    public double? HotspotErrorMm { get; set; }

    public ThermalRisk Risk { get; set; }
}
