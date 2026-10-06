using ThermoTwin.Application.Twin;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Sensitivity;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Application.Experiments;

// ---------- FEM verification and FVM–FEM comparison ----------

/// <summary>A small triangulation for display: node coordinates [m] and counter-clockwise triangles.</summary>
public sealed record FemMeshDto(int Nx, int Ny, double LengthX, double LengthY, int Nodes, int Elements, int BoundarySegments, double[][] NodeXy, int[][] Triangles);

/// <summary>Size and sparsity of the two discretisations at the demo resolution.</summary>
public sealed record DiscretisationSystemDto(string Method, string Unknowns, int Dofs, int NonZeros, int HalfBandwidth, double NonZerosPerRow);

public sealed record FemVerificationResult(
    IReadOnlyList<MethodConvergenceRow> Eigenmode,
    IReadOnlyList<MethodConvergenceRow> Manufactured,
    IReadOnlyList<BatteryComparisonRow> Battery,
    double CoolingLevel,
    double SnapshotTime,
    IReadOnlyDictionary<string, FieldDto> Fields,
    FemMeshDto DisplayMesh,
    IReadOnlyList<DiscretisationSystemDto> Systems);

// ---------- Adjoint gradient validation ----------

public sealed record GradientCheckRow(string Model, int Vector, double Epsilon, double RelativeError, double MaxAbsoluteError, double AdjointNorm, double FiniteDifferenceNorm);

/// <summary>Taylor remainder test along a direction d: R₀ = |Φ(u+εd) − Φ(u)| = O(ε), R₁ = |Φ(u+εd) − Φ(u) − ε∇Φᵀd| = O(ε²).</summary>
public sealed record TaylorTestRow(double Epsilon, double ZeroOrderRemainder, double FirstOrderRemainder);

public sealed record GradientComponent(int Segment, double Control, double Adjoint, double FiniteDifference);

public sealed record GradientCostRow(
    int Controls,
    double SegmentDuration,
    double AdjointMs,
    double ForwardDifferenceMs,
    double CentralDifferenceMs,
    int AdjointSolves,
    int ForwardDifferenceSolves,
    int CentralDifferenceSolves,
    double RelativeDifference);

public sealed record AdjointCheckResult(
    string Model,
    int StateDimension,
    int TimeSteps,
    int Controls,
    double Mu,
    double SafeTemperature,
    IReadOnlyList<GradientCheckRow> Rows,
    IReadOnlyList<TaylorTestRow> Taylor,
    IReadOnlyList<GradientComponent> Components,
    IReadOnlyList<GradientCostRow> Cost,
    double BestRelativeError,
    double BestEpsilon,
    double RomBestRelativeError);

// ---------- PDE-constrained optimisation benchmark ----------

public sealed record OptimizationMethodOutcome(
    string Key,
    string Name,
    string Gradient,
    string Model,
    double Objective,
    double EnergyJoules,
    double ModelPeak,
    double ModelViolation,
    double PlantPeak,
    double PlantViolation,
    bool Feasible,
    int Iterations,
    int ForwardSolves,
    int AdjointSolves,
    int FullOrderSolveEquivalents,
    double RuntimeMs,
    double[] Plan,
    KktDiagnostics Kkt,
    IReadOnlyList<PdeOptimizationIteration> History,
    double[] Times,
    double[] ModelMaxTemperature);

public sealed record OptimizationBenchmarkResult(
    double SafeTemperature,
    double ControlTemperature,
    int Segments,
    double SegmentDuration,
    double PredictionTimeStep,
    int StateDimension,
    double[] PenaltySchedule,
    double SmoothnessWeight,
    double ReferenceEnergyJoules,
    IReadOnlyList<OptimizationMethodOutcome> Methods,
    double AdjointSpeedup,
    double SolveReduction);

// ---------- Reduced-order modelling ----------

public sealed record PodSpectrumPoint(string Family, int Index, double Eigenvalue, double CumulativeEnergy);

public sealed record RomComparisonRow(
    string Family,
    string Test,
    int Modes,
    double CapturedEnergy,
    double Rmse,
    double MaxError,
    double ProjectionRmse,
    double PeakError,
    double FullOrderMs,
    double ReducedOrderMs,
    double Speedup,
    int FullDimension);

public sealed record RomOptimizationRow(
    string Model,
    int Modes,
    double RuntimeMs,
    double Speedup,
    double Objective,
    double ObjectiveGap,
    double EnergyJoules,
    double FullOrderPeak,
    bool Feasible,
    int ScreenedCells,
    int CorrectionRounds,
    double ConstraintShift,
    bool Repaired,
    bool FellBack,
    string? FallbackReason,
    int RomSolves,
    int FullOrderSolves,
    double ValidationError);

public sealed record RomTrainingInfo(string Family, int TrainingRuns, int Snapshots, int Rank, double TrainingMs, double PodMs, int[] SampleSteps);

public sealed record RomTimeSeries(double[] Times, double[] FullOrderMax, double[] SelectedMax, double[] CoarseMax, int CoarseModes);

public sealed record ReducedOrderResult(
    int FullDimension,
    double TimeStep,
    IReadOnlyList<RomTrainingInfo> Training,
    IReadOnlyList<PodSpectrumPoint> Spectrum,
    IReadOnlyList<RomComparisonRow> Comparison,
    int SelectedModes,
    string SelectionRule,
    IReadOnlyList<FieldDto> Modes,
    IReadOnlyDictionary<string, FieldDto> Fields,
    RomTimeSeries TimeSeries,
    IReadOnlyList<RomOptimizationRow> Optimization);

// ---------- Closed-loop MPC: legacy vs full-order adjoint vs ROM adjoint ----------

public sealed record MpcRunOutcome(
    string Key,
    string Name,
    string Optimizer,
    double PlantPeak,
    double EnergyJoules,
    double TimeAboveLimit,
    double MaxViolation,
    bool Feasible,
    int Optimizations,
    double TotalOptimizerMs,
    double MeanOptimizerMs,
    long ForwardSolves,
    long AdjointSolves,
    long FullOrderSolves,
    int Fallbacks,
    int CorrectionRounds,
    int Repairs,
    double MeanValidationError,
    double MaxValidationError,
    double[] Times,
    double[] MaxTemperature,
    double[] CoolingLevel);

public sealed record ReducedOrderControlResult(double SafeTemperature, int RomModes, double RomValidationThreshold, IReadOnlyList<MpcRunOutcome> Runs);

// ---------- Parameter sensitivity and identifiability ----------

public sealed record SensitivityTrace(string Parameter, double[] Values);

public sealed record SensitivityReportDto(
    string[] Parameters,
    string[] Units,
    double[] Nominal,
    double[] Uncertainty,
    int ObservationCount,
    double NoiseStd,
    double[] ColumnNorms,
    double[] SignalToNoise,
    double[][] Correlation,
    double[] SingularValues,
    double ConditionNumber,
    double[] CramerRaoStd,
    double[] CramerRaoRelative,
    double[][] ParameterCorrelation,
    IReadOnlyList<CollinearityIndex> Collinearity,
    double FiniteDifferenceConsistency)
{
    public static SensitivityReportDto From(SensitivityReport r) => new(
        r.Parameters, r.Units, r.Nominal, r.Uncertainty, r.ObservationCount, r.NoiseStd, r.ColumnNorms, r.SignalToNoise, r.Correlation,
        r.SingularValues, r.ConditionNumber, r.CramerRaoStd, r.CramerRaoRelative, r.ParameterCorrelation, r.Collinearity, r.FiniteDifferenceConsistency);
}

public sealed record EstimationCase(string Key, string Name, string Description, EstimationResult Result);

public sealed record IdentifiabilityResult(
    SensitivityReportDto Report,
    string TraceSensor,
    double[] TraceTimes,
    IReadOnlyList<SensitivityTrace> Traces,
    IReadOnlyList<EstimationCase> Estimations,
    double DataNoiseStd,
    string DataSource);
