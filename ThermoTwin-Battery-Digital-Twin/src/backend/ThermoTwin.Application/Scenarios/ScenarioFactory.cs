using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Inverse;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;
using ThermoTwin.Numerics.ReducedOrder;
using ThermoTwin.Numerics.Sensors;
using ThermoTwin.Numerics.Simulation;

namespace ThermoTwin.Application.Scenarios;

/// <summary>Translates a <see cref="ScenarioDefinition"/> into configured numerical components.</summary>
public sealed class ScenarioFactory
{
    public ScenarioFactory(ScenarioDefinition scenario) => Scenario = scenario;

    public ScenarioDefinition Scenario { get; }

    public Grid2D ModelGrid() =>
        new(Scenario.Geometry.Nx, Scenario.Geometry.Ny, Scenario.Geometry.LengthX, Scenario.Geometry.LengthY);

    public MaterialProperties Material() =>
        new(Scenario.Material.Density, Scenario.Material.SpecificHeat, Scenario.Material.Conductivity, Scenario.Material.Thickness);

    public BoundaryConditions Boundaries()
    {
        var env = Scenario.Environment;
        var edge = env.EdgeCondition switch
        {
            BoundaryKind.Dirichlet => BoundaryCondition.FixedTemperature(env.AmbientTemperature),
            BoundaryKind.Neumann => BoundaryCondition.Insulated,
            _ => BoundaryCondition.Convective(env.AmbientTemperature, env.EdgeHeatTransferCoefficient),
        };
        return BoundaryConditions.All(edge);
    }

    public CoolingModel Cooling() => new(
        Scenario.Cooling.CoolantTemperature,
        Scenario.Cooling.MinHeatTransferCoefficient,
        Scenario.Cooling.MaxHeatTransferCoefficient,
        Scenario.Cooling.RatedPowerWatts);

    public ThermalModel ThermalModel() => new(ModelGrid(), Material(), Boundaries(), Cooling());

    public LoadProfile LoadProfile() => Scenario.HeatSource.LoadProfile switch
    {
        LoadProfileKind.Constant => Numerics.Physics.LoadProfile.Constant(),
        _ => Numerics.Physics.LoadProfile.FastChargeCcCv(Scenario.HeatSource.ConstantCurrentEnd, Scenario.Solver.Duration, Scenario.HeatSource.TaperFactor),
    };

    public HeatSourceModel HeatSource() => new(
        Scenario.HeatSource.UniformJouleHeating,
        Scenario.HeatSource.Hotspots.Select(h => new GaussianHotspot(h.X, h.Y, h.PeakPower, h.Radius)).ToArray(),
        LoadProfile());

    public IReadOnlyList<Sensor> Sensors(int? count = null) =>
        SensorNetwork.Layout(Scenario.Geometry.LengthX, Scenario.Geometry.LengthY, count ?? Scenario.Sensors.Count);

    public BatteryPlant Plant(double? noiseStd = null, int? sensorCount = null, int seedOffset = 0) => new(
        ThermalModel(),
        Scenario.Geometry.PlantRefinement,
        Scenario.Solver.Scheme,
        Scenario.Solver.TimeStep,
        HeatSource(),
        Sensors(sensorCount),
        noiseStd ?? Scenario.Sensors.NoiseStd,
        Scenario.Sensors.Seed + seedOffset,
        Scenario.Environment.InitialTemperature);

    public HeatEquationSolver ModelSolver() =>
        new(ThermalModel(), Scenario.Solver.Scheme == TimeScheme.ExplicitEuler ? TimeScheme.CrankNicolson : Scenario.Solver.Scheme,
            Scenario.Solver.TimeStep);

    public SourceBasis SourceBasis(Grid2D grid) => new(grid, Scenario.Estimator.BasisNodesX, Scenario.Estimator.BasisNodesY);

    public InverseHeatSourceEstimator Estimator(int? sensorCount = null)
    {
        var solver = ModelSolver();
        var grid = solver.Model.Grid;
        return new InverseHeatSourceEstimator(
            solver,
            SourceBasis(grid),
            new SensorNetwork(grid, Sensors(sensorCount)),
            LoadProfile(),
            grid.CreateField(Scenario.Environment.InitialTemperature));
    }

    public ThermalPredictor Predictor() => new(PredictionSolver(), LoadProfile());

    /// <summary>Crank–Nicolson model used for forecasting and optimisation (Δt = prediction time step).</summary>
    public HeatEquationSolver PredictionSolver() =>
        new(ThermalModel(), TimeScheme.CrankNicolson, Scenario.Control.PredictionTimeStep);

    public CoolingOptimizationOptions OptimizationOptions(int? segments = null, double? segmentDuration = null) => new(
        SafeTemperature: Scenario.Control.SafeTemperature - Scenario.Control.ControlMargin,
        Segments: segments ?? Scenario.Control.HorizonSegments,
        SegmentDuration: segmentDuration ?? Scenario.Control.SegmentDuration);

    /// <summary>
    /// Offline POD training on the prediction model: Joule heating alone plus one localised source on every hat
    /// function of the inverse problem's source basis, under a ramped cooling schedule over the charge, with
    /// geometric snapshot times. No knowledge of the true defect is used.
    /// </summary>
    public (PodBasis Basis, SnapshotSet Snapshots, double TrainingMs, double PodMs) TrainPod(HeatEquationSolver solver, int maxModes = 80)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var grid = solver.Model.Grid;
        var duration = Scenario.Solver.Duration;
        var segment = Scenario.Control.SegmentDuration;
        var segments = Math.Max(1, (int)Math.Round(duration / segment));
        var ramp = new CoolingPlan(segment, Enumerable.Range(0, segments).Select(k => (double)k / Math.Max(1, segments - 1)).ToArray());
        var peak = Scenario.HeatSource.Hotspots.Count > 0 ? Scenario.HeatSource.Hotspots.Max(h => h.PeakPower) : 450_000;
        var cases = PodTrainer.SourceBasisCases(SourceBasis(grid), Scenario.HeatSource.UniformJouleHeating, peak, ramp);
        var steps = (int)Math.Round(ramp.Horizon / solver.TimeStep);
        var snapshots = PodTrainer.CollectSnapshots(new FullOrderThermalDynamics(solver), LoadProfile(),
            grid.CreateField(Scenario.Environment.InitialTemperature), cases, PodTrainer.GeometricSampleSteps(steps, 6));
        var trainingMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var basis = PodBasis.Compute(snapshots, maxModes);
        return (basis, snapshots, trainingMs, sw.Elapsed.TotalMilliseconds);
    }

    /// <summary>Builds the MPC optimiser selected by <see cref="ControlSettings.Optimizer"/>.</summary>
    public ICoolingOptimizer CoolingOptimizer(HeatEquationSolver predictionSolver, PodBasis? pod = null)
    {
        var load = LoadProfile();
        var cooling = Cooling();
        switch (Scenario.Control.Optimizer)
        {
            case CoolingOptimizerKind.PenaltyFiniteDifference:
                return new CoolingOptimizer(new ThermalPredictor(predictionSolver, load));
            case CoolingOptimizerKind.AdjointReducedOrder:
            {
                pod ??= TrainPod(predictionSolver).Basis;
                var rom = new ReducedThermalModel(pod, Math.Min(Scenario.Control.RomModes, pod.Rank), predictionSolver);
                return new ReducedOrderCoolingOptimizer(rom, predictionSolver, load, cooling, null, Scenario.Control.RomValidationThreshold);
            }

            default:
                return new AdjointCoolingOptimizer(new FullOrderThermalDynamics(predictionSolver), load, cooling);
        }
    }
}
