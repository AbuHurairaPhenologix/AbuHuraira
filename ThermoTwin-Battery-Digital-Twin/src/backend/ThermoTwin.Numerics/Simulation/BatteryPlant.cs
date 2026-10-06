using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Sensors;

namespace ThermoTwin.Numerics.Simulation;

/// <summary>
/// The synthetic "physical" battery: the ground-truth process the digital twin observes.
/// <para>
/// To avoid the <i>inverse crime</i> (generating data with the very model used for inversion),
/// the plant runs on a grid refined by <see cref="RefinementFactor"/> relative to the twin's model
/// grid, uses the exact Gaussian hotspot rather than the coarse source basis, and its sensors add
/// Gaussian noise. Temperature fields are restricted to the model grid only for comparison.
/// </para>
/// </summary>
public sealed class BatteryPlant
{
    private readonly HeatEquationSolver _solver;
    private readonly HeatSourceModel _source;
    private readonly double[] _spatialSource;
    private readonly SensorNetwork _sensors;
    private readonly GaussianNoise _noise;
    private double[] _state;
    private double[] _next;
    private readonly double[] _qNow;
    private readonly double[] _qNext;

    public BatteryPlant(
        ThermalModel model,
        int refinementFactor,
        TimeScheme scheme,
        double timeStep,
        HeatSourceModel source,
        IReadOnlyList<Sensor> sensors,
        double noiseStd,
        int seed,
        double initialTemperature)
    {
        RefinementFactor = refinementFactor;
        ModelGrid = model.Grid;
        FineGrid = new Grid2D(model.Grid.Nx * refinementFactor, model.Grid.Ny * refinementFactor,
            model.Grid.LengthX, model.Grid.LengthY);
        _solver = new HeatEquationSolver(model.WithGrid(FineGrid), scheme, timeStep);
        _source = source;
        _spatialSource = source.SpatialField(FineGrid);
        _sensors = new SensorNetwork(FineGrid, sensors);
        _noise = new GaussianNoise(noiseStd, seed);
        _state = FineGrid.CreateField(initialTemperature);
        _next = new double[FineGrid.CellCount];
        _qNow = new double[FineGrid.CellCount];
        _qNext = new double[FineGrid.CellCount];
    }

    public int RefinementFactor { get; }

    public Grid2D FineGrid { get; }

    public Grid2D ModelGrid { get; }

    public double Time { get; private set; }

    public double TimeStep => _solver.TimeStep;

    public HeatSourceModel Source => _source;

    public ReadOnlySpan<double> FineState => _state;

    /// <summary>Advances the plant by one step under cooling level u and returns noisy sensor readings.</summary>
    public double[] Step(double coolingLevel)
    {
        _source.Evaluate(FineGrid, Time, _qNow, _spatialSource);
        _source.Evaluate(FineGrid, Time + TimeStep, _qNext, _spatialSource);
        _solver.Step(_state, _next, _qNow, _qNext, coolingLevel);
        (_state, _next) = (_next, _state);
        Time += TimeStep;
        return _sensors.Measure(_state, _noise);
    }

    /// <summary>Noise-free sensor values (for diagnostics only).</summary>
    public double[] TrueSensorValues() => _sensors.Observe(_state);

    /// <summary>True temperature restricted (cell-averaged) onto the model grid.</summary>
    public double[] TemperatureOnModelGrid() => ModelGrid.RestrictFrom(FineGrid, _state);

    /// <summary>True spatial source shape q(x, y) at full load restricted onto the model grid [W/m³].</summary>
    public double[] SourceOnModelGrid() => ModelGrid.RestrictFrom(FineGrid, _spatialSource);
}
