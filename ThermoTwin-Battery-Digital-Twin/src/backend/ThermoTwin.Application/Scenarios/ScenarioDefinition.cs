using ThermoTwin.Domain.Enums;
using ThermoTwin.Numerics.Inverse;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Application.Scenarios;

/// <summary>
/// Complete, serialisable description of a digital-twin scenario: geometry, physics, sensing,
/// numerics, estimation, control and playback. Defaults reproduce the built-in demo.
/// </summary>
public sealed record ScenarioDefinition
{
    public string Key { get; init; } = "custom";

    public string Name { get; init; } = "Custom scenario";

    public string Description { get; init; } = string.Empty;

    public GeometrySettings Geometry { get; init; } = new();

    public MaterialSettings Material { get; init; } = new();

    public EnvironmentSettings Environment { get; init; } = new();

    public CoolingSettings Cooling { get; init; } = new();

    public HeatSourceSettings HeatSource { get; init; } = new();

    public SensorSettings Sensors { get; init; } = new();

    public SolverSettings Solver { get; init; } = new();

    public EstimatorSettings Estimator { get; init; } = new();

    public ControlSettings Control { get; init; } = new();

    public PlaybackSettings Playback { get; init; } = new();
}

/// <param name="LengthX">Cell length [m].</param>
/// <param name="LengthY">Cell width [m].</param>
/// <param name="Nx">Model grid cells along x.</param>
/// <param name="Ny">Model grid cells along y.</param>
/// <param name="PlantRefinement">Refinement of the synthetic plant grid relative to the model grid (avoids the inverse crime).</param>
public sealed record GeometrySettings(
    double LengthX = 0.20,
    double LengthY = 0.10,
    int Nx = 40,
    int Ny = 20,
    int PlantRefinement = 2);

public sealed record MaterialSettings(
    double Density = 2500,
    double SpecificHeat = 1000,
    double Conductivity = 20,
    double Thickness = 0.010);

public sealed record EnvironmentSettings(
    double AmbientTemperature = 25,
    double InitialTemperature = 25,
    BoundaryKind EdgeCondition = BoundaryKind.Robin,
    double EdgeHeatTransferCoefficient = 10);

/// <param name="BaselineLevel">Cooling level applied in Fixed/Advisory mode and in the counterfactual plant.</param>
public sealed record CoolingSettings(
    double CoolantTemperature = 22,
    double MinHeatTransferCoefficient = 5,
    double MaxHeatTransferCoefficient = 120,
    double RatedPowerWatts = 150,
    double BaselineLevel = 0.15,
    CoolingMode Mode = CoolingMode.Autonomous);

public sealed record HotspotSettings(double X, double Y, double PeakPower, double Radius);

public enum LoadProfileKind
{
    FastChargeCcCv,
    Constant,
}

public sealed record HeatSourceSettings
{
    /// <summary>Bulk I²R heating at full load [W/m³].</summary>
    public double UniformJouleHeating { get; init; } = 55_000;

    /// <summary>Hidden localised heat sources (unknown to the twin).</summary>
    public IReadOnlyList<HotspotSettings> Hotspots { get; init; } = [new(0.138, 0.064, 450_000, 0.012)];

    public LoadProfileKind LoadProfile { get; init; } = LoadProfileKind.FastChargeCcCv;

    /// <summary>End of the constant-current phase [s].</summary>
    public double ConstantCurrentEnd { get; init; } = 1200;

    /// <summary>Heat factor reached at the end of the CV taper.</summary>
    public double TaperFactor { get; init; } = 0.25;
}

public sealed record SensorSettings(int Count = 12, double NoiseStd = 0.1, int Seed = 42);

public sealed record SolverSettings(TimeScheme Scheme = TimeScheme.CrankNicolson, double TimeStep = 5, double Duration = 1800);

/// <param name="EstimationInterval">Simulated seconds between inverse solves.</param>
public sealed record EstimatorSettings(
    int BasisNodesX = 13,
    int BasisNodesY = 7,
    RegularizationKind Regularization = RegularizationKind.Gradient,
    LambdaSelection LambdaSelection = LambdaSelection.Gcv,
    double FixedLambda = 1e-4,
    double EstimationInterval = 30);

/// <param name="ControlMargin">Back-off below T_safe used by the optimiser to absorb model mismatch [K].</param>
/// <param name="ForecastLead">Lead time of the tracked "prediction vs actual" series [s].</param>
public sealed record ControlSettings(
    double SafeTemperature = 45,
    double CriticalTemperature = 55,
    double ControlMargin = 1.0,
    int HorizonSegments = 6,
    double SegmentDuration = 100,
    double PredictionTimeStep = 10,
    double ControlInterval = 60,
    double ForecastLead = 300);

/// <param name="StepsPerFrame">Solver steps advanced per streamed frame.</param>
/// <param name="FrameIntervalMs">Wall-clock delay between frames (playback speed).</param>
public sealed record PlaybackSettings(int StepsPerFrame = 2, int FrameIntervalMs = 100);
