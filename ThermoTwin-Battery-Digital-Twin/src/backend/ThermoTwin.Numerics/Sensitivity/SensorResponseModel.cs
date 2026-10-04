using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Sensors;

namespace ThermoTwin.Numerics.Sensitivity;

/// <summary>Physical parameters whose influence on the sensor data is analysed.</summary>
public enum ThermalParameter
{
    /// <summary>k — effective in-plane conductivity [W/(m·K)].</summary>
    Conductivity,

    /// <summary>h_e — edge (Robin) heat-transfer coefficient [W/(m²·K)].</summary>
    EdgeHeatTransfer,

    /// <summary>Q — peak volumetric power of the defect [W/m³].</summary>
    DefectPower,

    /// <summary>x₀ — defect centre [m].</summary>
    DefectX,

    /// <summary>y₀ — defect centre [m].</summary>
    DefectY,
}

/// <summary>A parameter with its nominal value, a plausible a-priori uncertainty (used for scaling), bounds and FD step.</summary>
public sealed record ParameterSpec(
    ThermalParameter Parameter,
    string Symbol,
    string Unit,
    double Nominal,
    double Uncertainty,
    double Lower,
    double Upper,
    double FiniteDifferenceStep);

/// <summary>
/// Parameter-to-observation map θ ↦ y(θ) ∈ ℝ^m: the battery heat equation (uniform Joule heating + one Gaussian
/// defect, Robin edges, constant cold-plate cooling, known load s(t)) is solved with the finite-volume
/// Crank–Nicolson scheme and the thermistors are read every <c>sampleInterval</c> seconds.
/// The map is linear in Q but nonlinear in k, h_e, x₀ and y₀.
/// </summary>
public sealed class SensorResponseModel
{
    private readonly ThermalModel _baseModel;
    private readonly IReadOnlyList<Sensor> _sensors;
    private readonly LoadProfile _load;
    private readonly double _joule;
    private readonly double _radius;
    private readonly double _coolingLevel;
    private readonly double _initialTemperature;
    private readonly double _ambient;
    private readonly double _duration;
    private readonly double _timeStep;
    private readonly int _sampleEvery;

    public SensorResponseModel(
        ThermalModel baseModel,
        IReadOnlyList<Sensor> sensors,
        LoadProfile load,
        double jouleHeating,
        double defectRadius,
        double coolingLevel,
        double initialTemperature,
        double ambientTemperature,
        double duration,
        double timeStep,
        double sampleInterval)
    {
        _baseModel = baseModel;
        _sensors = sensors;
        _load = load;
        _joule = jouleHeating;
        _radius = defectRadius;
        _coolingLevel = coolingLevel;
        _initialTemperature = initialTemperature;
        _ambient = ambientTemperature;
        _duration = duration;
        _timeStep = timeStep;
        _sampleEvery = Math.Max(1, (int)Math.Round(sampleInterval / timeStep));
        SampleTimes = Enumerable.Range(1, (int)Math.Round(duration / timeStep))
            .Where(n => n % _sampleEvery == 0)
            .Select(n => n * timeStep)
            .ToArray();
    }

    public IReadOnlyList<Sensor> Sensors => _sensors;

    public double[] SampleTimes { get; }

    /// <summary>m = (#sample times) × (#sensors); ordering time-major: y[t·S + s].</summary>
    public int ObservationCount => SampleTimes.Length * _sensors.Count;

    /// <summary>Evaluates y(θ) for parameter values in <see cref="ThermalParameter"/> order, on the given grid.</summary>
    public double[] Simulate(IReadOnlyDictionary<ThermalParameter, double> theta, Grid2D? grid = null, double noiseStd = 0, int seed = 0)
    {
        grid ??= _baseModel.Grid;
        var k = theta[ThermalParameter.Conductivity];
        var h = theta[ThermalParameter.EdgeHeatTransfer];
        var model = _baseModel with
        {
            Grid = grid,
            Material = _baseModel.Material with { Conductivity = k },
            Boundaries = BoundaryConditions.All(BoundaryCondition.Convective(_ambient, h)),
        };

        var solver = new HeatEquationSolver(model, TimeScheme.CrankNicolson, _timeStep);
        var spot = new GaussianHotspot(theta[ThermalParameter.DefectX], theta[ThermalParameter.DefectY], theta[ThermalParameter.DefectPower], _radius);
        var shape = grid.CreateField((x, y) => _joule + spot.Evaluate(x, y));
        var network = new SensorNetwork(grid, _sensors);
        var noise = new GaussianNoise(noiseStd, seed);

        var t = grid.CreateField(_initialTemperature);
        var next = new double[t.Length];
        var qNow = new double[t.Length];
        var qNext = new double[t.Length];
        var steps = (int)Math.Round(_duration / _timeStep);
        var y = new double[ObservationCount];
        var row = 0;
        for (var n = 0; n < steps; n++)
        {
            var sNow = _load.At(n * _timeStep);
            var sNext = _load.At((n + 1) * _timeStep);
            for (var i = 0; i < t.Length; i++)
            {
                qNow[i] = sNow * shape[i];
                qNext[i] = sNext * shape[i];
            }

            solver.Step(t, next, qNow, qNext, _coolingLevel);
            (t, next) = (next, t);
            if ((n + 1) % _sampleEvery == 0)
            {
                var readings = noiseStd > 0 ? network.Measure(t, noise) : network.Observe(t);
                readings.CopyTo(y, row);
                row += readings.Length;
            }
        }

        return y;
    }
}
