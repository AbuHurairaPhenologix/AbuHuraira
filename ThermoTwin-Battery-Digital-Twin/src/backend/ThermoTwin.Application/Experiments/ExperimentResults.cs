using ThermoTwin.Application.Twin;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Application.Experiments;

// ---------- Numerical convergence ----------

public sealed record PerformanceRow(string Label, int Nx, int Ny, string Scheme, int Steps, double TotalMs, double MsPerStep);

public sealed record SchemeEnergyBalance(TimeScheme Scheme, double InjectedEnergy, double StoredEnergyChange, double RelativeImbalance);

public sealed record ConvergenceExperimentResult(
    IReadOnlyList<ConvergenceRow> Spatial,
    IReadOnlyList<ConvergenceRow> Temporal,
    IReadOnlyList<StabilityProbe> StabilityProbes,
    IReadOnlyList<StabilityReport> StabilityReports,
    IReadOnlyList<SchemeEnergyBalance> EnergyConservation,
    IReadOnlyList<PerformanceRow> Performance);

// ---------- Inverse problem / regularisation ----------

public sealed record ReconstructionMetrics(double TemperatureRmse, double TemperatureMaxError, double SourceRelativeError, double? HotspotErrorMm, double? EstimatedHotspotX, double? EstimatedHotspotY, double PeakSourceKw);

public sealed record LCurveSample(double RelativeLambda, double ResidualNorm, double SolutionSeminorm, double Curvature, double Gcv, double SourceRelativeError, double TemperatureRmse, double? HotspotErrorMm);

public sealed record LambdaChoice(string Method, double RelativeLambda, double ResidualNorm, double SolutionSeminorm, ReconstructionMetrics Metrics);

public sealed record RegularizationStudy(string Regularization, IReadOnlyList<LCurveSample> Curve, IReadOnlyList<LambdaChoice> Choices);

public sealed record ReconstructionTimelinePoint(double Time, double RelativeLambda, double TemperatureRmse, double SourceRelativeError, double? HotspotErrorMm, double TrueMax, double EstimatedMax);

public sealed record RegularizationExperimentResult(
    int MeasurementCount,
    int Unknowns,
    int SensorCount,
    double NoiseStd,
    double DiscrepancyTarget,
    IReadOnlyList<RegularizationStudy> Studies,
    IReadOnlyList<ReconstructionTimelinePoint> Timeline,
    IReadOnlyDictionary<string, FieldDto> Fields,
    IReadOnlyList<SensorReadingDto> Sensors,
    IReadOnlyList<HotspotDto> TrueHotspots);

// ---------- Sensitivity studies ----------

public sealed record SensitivityRow(double Parameter, double RelativeLambda, ReconstructionMetrics Metrics);

public sealed record SensitivityExperimentResult(string Parameter, string Unit, IReadOnlyList<SensitivityRow> Rows);

// ---------- Forecast accuracy ----------

public sealed record ForecastTrace(double IssuedAt, double Horizon, double[] Times, double[] Predicted, double[] Actual, double MeanAbsoluteError, double ErrorAtHorizon, double PredictedPeak, double ActualPeak);

public sealed record ForecastExperimentResult(IReadOnlyList<ForecastTrace> Forecasts, double[] ActualTimes, double[] ActualMax);

// ---------- Cooling optimisation ----------

public sealed record StrategyOutcome(
    string Key,
    string Name,
    string Description,
    double PeakTemperature,
    double CoolingEnergyJoules,
    double MaxViolation,
    double TimeAboveLimit,
    double ViolationIntegral,
    double Objective,
    bool Feasible,
    double[] Times,
    double[] MaxTemperature,
    double[] CoolingLevel);

public sealed record OptimizationTracePoint(int Iteration, double Penalty, double Objective, double EnergyTerm, double Violation, double PeakTemperature);

public sealed record CoolingComparisonResult(
    double SafeTemperature,
    double ControlTemperature,
    double ReferenceEnergyJoules,
    double MinimumFeasibleConstantLevel,
    double[] OptimizedPlan,
    double SegmentDuration,
    IReadOnlyList<StrategyOutcome> Strategies,
    IReadOnlyList<OptimizationTracePoint> OptimizationHistory,
    double OptimizationMs,
    int OptimizationEvaluations);
