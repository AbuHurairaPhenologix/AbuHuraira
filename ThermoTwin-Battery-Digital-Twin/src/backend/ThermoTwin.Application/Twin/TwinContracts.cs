using ThermoTwin.Domain.Enums;
using ThermoTwin.Numerics.Grid;

namespace ThermoTwin.Application.Twin;

/// <summary>A scalar field on the model grid, serialised as rows (y-index) of columns (x-index).</summary>
public sealed record FieldDto(
    string Key,
    string Label,
    string Unit,
    int Nx,
    int Ny,
    double LengthX,
    double LengthY,
    double Min,
    double Max,
    double[][] Values)
{
    public static FieldDto Create(string key, string label, string unit, Grid2D grid, ReadOnlySpan<double> field, double scale = 1.0, int decimals = 3)
    {
        var rows = grid.ToRows(field);
        var min = double.PositiveInfinity;
        var max = double.NegativeInfinity;
        foreach (var row in rows)
        {
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = Math.Round(row[i] * scale, decimals);
                min = Math.Min(min, row[i]);
                max = Math.Max(max, row[i]);
            }
        }

        return new FieldDto(key, label, unit, grid.Nx, grid.Ny, grid.LengthX, grid.LengthY, min, max, rows);
    }
}

public sealed record TemperatureStats(double Max, double Min, double Mean, double Spread, double MaxX, double MaxY);

public sealed record HotspotDto(double X, double Y, double PeakPower, double? AreaMm2, double? Prominence);

public sealed record SensorReadingDto(string Id, double X, double Y, double Value, double TrueValue);

public sealed record CoolingDto(
    double Level,
    double BaselineLevel,
    double? RecommendedLevel,
    double[]? RecommendedPlan,
    double SegmentDuration,
    double PowerWatts,
    double EnergyJoules,
    double? PlannedEnergyJoules,
    int? OptimizerEvaluations,
    double? OptimizerMs);

public sealed record ForecastDto(
    double IssuedAt,
    double[] Times,
    double[] PlannedMax,
    double[] UnmitigatedMax,
    double PlannedPeak,
    double UnmitigatedPeak,
    double PlannedPeakTime);

public sealed record EstimationDto(
    double LastUpdate,
    string Regularization,
    string LambdaSelection,
    double RelativeLambda,
    double ResidualNorm,
    double SolutionSeminorm,
    int MeasurementCount,
    int Unknowns,
    double TemperatureRmse,
    double SourceRelativeError,
    double SolveMs);

/// <summary>One time-series sample of the twin (one per solver step).</summary>
public sealed record TwinHistoryPoint(
    double Time,
    double TrueMax,
    double TrueMean,
    double TrueMin,
    double? EstimatedMax,
    double? EstimatedMean,
    double CounterfactualMax,
    double CoolingLevel,
    double CoolingPower,
    double CumulativeEnergy,
    double? LeadPrediction,
    double? TemperatureRmse,
    double? SourceRelativeError,
    double? HotspotErrorMm,
    double[] Sensors,
    ThermalRisk Risk,
    double LoadFactor);

/// <summary>Snapshot of the complete digital-twin state pushed to clients over SignalR.</summary>
public sealed record TwinFrame(
    Guid RunId,
    string ScenarioKey,
    string ScenarioName,
    SimulationStatus Status,
    CoolingMode Mode,
    int Step,
    int TotalSteps,
    double Time,
    double Duration,
    double LoadFactor,
    TemperatureStats Truth,
    TemperatureStats? Estimate,
    HotspotDto? EstimatedHotspot,
    IReadOnlyList<HotspotDto> TrueHotspots,
    double? HotspotErrorMm,
    ThermalRisk Risk,
    bool MitigationActive,
    double SafeTemperature,
    double CriticalTemperature,
    double CounterfactualMax,
    CoolingDto Cooling,
    ForecastDto? Forecast,
    EstimationDto? Estimation,
    IReadOnlyList<SensorReadingDto> Sensors,
    IReadOnlyDictionary<string, FieldDto>? Fields,
    IReadOnlyList<TwinHistoryPoint> NewHistory);
