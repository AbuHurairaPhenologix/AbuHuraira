using System.Diagnostics;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Domain.Enums;
using ThermoTwin.Domain.Services;
using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Inverse;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;
using ThermoTwin.Numerics.Sensors;
using ThermoTwin.Numerics.Simulation;

namespace ThermoTwin.Application.Twin;

/// <summary>
/// The digital-twin loop. Each solver step:
/// <list type="number">
/// <item><b>Plant</b> — the synthetic battery advances under the applied cooling and its thermistors report noisy readings.</item>
/// <item><b>Assimilation</b> — the inverse estimator advances its forward responses and appends the readings to its normal equations.</item>
/// <item><b>Estimation</b> (every <c>EstimationInterval</c>) — Tikhonov inversion with automatic λ gives q̂ and the full field T̂.</item>
/// <item><b>Prediction &amp; control</b> (every <c>ControlInterval</c>) — forecast the peak, classify risk and, unless in Fixed mode,
///   solve the constrained cooling problem; in Autonomous mode the first segment is applied (receding-horizon MPC).</item>
/// </list>
/// A counterfactual plant runs in parallel with the baseline cooling so the benefit of control is measured, not assumed.
/// </summary>
public sealed class DigitalTwinEngine
{
    private readonly ScenarioDefinition _scenario;
    private readonly ScenarioFactory _factory;
    private readonly BatteryPlant _plant;
    private readonly BatteryPlant _counterfactual;
    private readonly InverseHeatSourceEstimator _estimator;
    private readonly ThermalPredictor _predictor;
    private readonly CoolingOptimizer _optimizer;
    private readonly CoolingModel _cooling;
    private readonly LoadProfile _load;
    private readonly Grid2D _grid;
    private readonly IReadOnlyList<Sensor> _sensors;
    private readonly double[] _trueSource;
    private readonly int _estimationEvery;
    private readonly int _controlEvery;
    private readonly List<TwinHistoryPoint> _history = [];
    private readonly SortedDictionary<double, double> _leadPredictions = [];

    private double _level;
    private double _energy;
    private double[] _lastReadings;
    private InverseSolution? _solution;
    private double[]? _estimatedTemperature;
    private Hotspot? _estimatedHotspot;
    private double? _hotspotErrorMm;
    private double? _temperatureRmse;
    private double? _sourceError;
    private double _lastEstimationTime;
    private double _lastSolveMs;
    private CoolingOptimizationResult? _optimization;
    private double _optimizationMs;
    private ThermalForecast? _plannedForecast;
    private ThermalForecast? _unmitigatedForecast;
    private double[]? _warmStart;
    private ThermalRisk _risk = ThermalRisk.Normal;
    private int _historyCursor;

    public DigitalTwinEngine(ScenarioDefinition scenario, Guid runId)
    {
        _scenario = scenario;
        RunId = runId;
        _factory = new ScenarioFactory(scenario);
        _plant = _factory.Plant();
        _counterfactual = _factory.Plant(seedOffset: 1000);
        _estimator = _factory.Estimator();
        _predictor = _factory.Predictor();
        _optimizer = new CoolingOptimizer(_predictor);
        _cooling = _factory.Cooling();
        _load = _factory.LoadProfile();
        _grid = _factory.ModelGrid();
        _sensors = _factory.Sensors();
        _trueSource = _plant.SourceOnModelGrid();
        _level = scenario.Cooling.BaselineLevel;
        Mode = scenario.Cooling.Mode;
        TotalSteps = (int)Math.Round(scenario.Solver.Duration / scenario.Solver.TimeStep);
        _estimationEvery = Math.Max(1, (int)Math.Round(scenario.Estimator.EstimationInterval / scenario.Solver.TimeStep));
        _controlEvery = Math.Max(1, (int)Math.Round(scenario.Control.ControlInterval / scenario.Solver.TimeStep));
        _lastReadings = _plant.TrueSensorValues();
    }

    public Guid RunId { get; }

    public ScenarioDefinition Scenario => _scenario;

    public CoolingMode Mode { get; set; }

    public int Step { get; private set; }

    public int TotalSteps { get; }

    public double Time => _plant.Time;

    public bool IsComplete => Step >= TotalSteps;

    public IReadOnlyList<TwinHistoryPoint> History => _history;

    public double PeakTemperature { get; private set; }

    public double PeakCounterfactualTemperature { get; private set; }

    public double CoolingEnergy => _energy;

    public double? TemperatureRmse => _temperatureRmse;

    public double? SourceRelativeError => _sourceError;

    public double? HotspotErrorMm => _hotspotErrorMm;

    public ThermalRisk Risk => _risk;

    /// <summary>Advances the twin by one solver step.</summary>
    public void Advance()
    {
        if (IsComplete)
        {
            return;
        }

        var dt = _scenario.Solver.TimeStep;
        var applied = _level;
        _lastReadings = _plant.Step(applied);
        _counterfactual.Step(_scenario.Cooling.BaselineLevel);
        _estimator.Assimilate(applied, _lastReadings);
        _energy += _cooling.Power(applied) * dt;
        Step++;

        if (Step % _estimationEvery == 0 || Step == TotalSteps)
        {
            Estimate();
        }
        else if (_solution is not null)
        {
            _estimatedTemperature = _estimator.EstimateTemperature(_solution.Coefficients);
        }

        if (Step % _controlEvery == 0 && _solution is not null && !IsComplete)
        {
            PredictAndControl();
        }

        RecordHistory();
    }

    private void Estimate()
    {
        var sw = Stopwatch.StartNew();
        _solution = _estimator.SolveAuto(_scenario.Estimator.LambdaSelection, _scenario.Estimator.Regularization,
            _scenario.Sensors.NoiseStd, _scenario.Estimator.FixedLambda);
        _lastSolveMs = sw.Elapsed.TotalMilliseconds;
        _lastEstimationTime = Time;
        _estimatedTemperature = _solution.TemperatureField;

        var truth = _plant.TemperatureOnModelGrid();
        _temperatureRmse = ErrorMetrics.Rmse(_estimatedTemperature, truth);
        _sourceError = ErrorMetrics.RelativeL2(_solution.SourceField, _trueSource);
        _estimatedHotspot = HotspotDetector.Detect(_grid, _solution.SourceField);
        _hotspotErrorMm = _estimatedHotspot is null || _scenario.HeatSource.Hotspots.Count == 0
            ? null
            : _scenario.HeatSource.Hotspots.Min(h => Math.Sqrt(Math.Pow(h.X - _estimatedHotspot.X, 2) + Math.Pow(h.Y - _estimatedHotspot.Y, 2))) * 1000;
    }

    private void PredictAndControl()
    {
        var solution = _solution!;
        var estimate = _estimatedTemperature!;
        var options = _factory.OptimizationOptions();
        var baselinePlan = CoolingPlan.Constant(_scenario.Cooling.BaselineLevel, options.Segments, options.SegmentDuration);
        _unmitigatedForecast = _predictor.Predict(estimate, solution.SourceField, Time, baselinePlan);

        if (Mode == CoolingMode.Fixed)
        {
            _optimization = null;
            _plannedForecast = _unmitigatedForecast;
        }
        else
        {
            var sw = Stopwatch.StartNew();
            _optimization = _optimizer.Optimize(estimate, solution.SourceField, Time, options, _warmStart);
            _optimizationMs = sw.Elapsed.TotalMilliseconds;
            var levels = _optimization.Plan.Levels;
            _warmStart = [.. levels.Skip(1), levels[^1]];

            if (Mode == CoolingMode.Autonomous)
            {
                _level = levels[0];
                _plannedForecast = _optimization.Forecast;
            }
            else
            {
                _plannedForecast = _unmitigatedForecast;
            }
        }

        // Track the lead-time prediction for the "prediction vs actual" comparison.
        var lead = _scenario.Control.ForecastLead;
        var forecast = _plannedForecast!;
        for (var n = 0; n < forecast.Times.Length; n++)
        {
            if (Math.Abs(forecast.Times[n] - (Time + lead)) < 1e-6)
            {
                _leadPredictions[Math.Round(forecast.Times[n], 6)] = forecast.MaxTemperature[n];
            }
        }

        _risk = ThermalRiskPolicy.Classify(Vector.Max(estimate), _unmitigatedForecast.PeakTemperature,
            _scenario.Control.SafeTemperature, _scenario.Control.CriticalTemperature);
    }

    private void RecordHistory()
    {
        var truth = _plant.TemperatureOnModelGrid();
        var counterfactualMax = Vector.Max(_counterfactual.TemperatureOnModelGrid());
        var trueMax = Vector.Max(truth);
        PeakTemperature = Math.Max(PeakTemperature, trueMax);
        PeakCounterfactualTemperature = Math.Max(PeakCounterfactualTemperature, counterfactualMax);

        double? leadPrediction = _leadPredictions.TryGetValue(Math.Round(Time, 6), out var p) ? p : null;
        _history.Add(new TwinHistoryPoint(
            Time,
            Math.Round(trueMax, 4),
            Math.Round(Vector.Mean(truth), 4),
            Math.Round(Vector.Min(truth), 4),
            _estimatedTemperature is null ? null : Math.Round(Vector.Max(_estimatedTemperature), 4),
            _estimatedTemperature is null ? null : Math.Round(Vector.Mean(_estimatedTemperature), 4),
            Math.Round(counterfactualMax, 4),
            Math.Round(_level, 4),
            Math.Round(_cooling.Power(_level), 4),
            Math.Round(_energy, 2),
            leadPrediction is null ? null : Math.Round(leadPrediction.Value, 4),
            _temperatureRmse is null ? null : Math.Round(_temperatureRmse.Value, 5),
            _sourceError is null ? null : Math.Round(_sourceError.Value, 5),
            _hotspotErrorMm is null ? null : Math.Round(_hotspotErrorMm.Value, 3),
            _lastReadings.Select(v => Math.Round(v, 3)).ToArray(),
            _risk,
            Math.Round(_load.At(Time), 4)));
    }

    /// <summary>Builds the frame to stream; <paramref name="includeFields"/> controls whether heatmap data is attached.</summary>
    public TwinFrame BuildFrame(SimulationStatus status, bool includeFields = true)
    {
        var truth = _plant.TemperatureOnModelGrid();
        var truthStats = Stats(truth);
        var estimateStats = _estimatedTemperature is null ? null : Stats(_estimatedTemperature);
        var trueSensors = _plant.TrueSensorValues();
        var sensors = _sensors.Select((s, i) => new SensorReadingDto(s.Id, s.X, s.Y,
            Math.Round(_lastReadings[i], 3), Math.Round(trueSensors[i], 3))).ToArray();

        var options = _factory.OptimizationOptions();
        var cooling = new CoolingDto(
            _level,
            _scenario.Cooling.BaselineLevel,
            _optimization?.Plan.Levels[0],
            _optimization?.Plan.Levels.Select(v => Math.Round(v, 4)).ToArray(),
            options.SegmentDuration,
            _cooling.Power(_level),
            _energy,
            _optimization?.CoolingEnergy,
            _optimization?.Evaluations,
            _optimization is null ? null : Math.Round(_optimizationMs, 1));

        ForecastDto? forecast = null;
        if (_plannedForecast is not null && _unmitigatedForecast is not null)
        {
            forecast = new ForecastDto(
                _plannedForecast.StartTime,
                _plannedForecast.Times,
                _plannedForecast.MaxTemperature.Select(v => Math.Round(v, 3)).ToArray(),
                _unmitigatedForecast.MaxTemperature.Select(v => Math.Round(v, 3)).ToArray(),
                _plannedForecast.PeakTemperature,
                _unmitigatedForecast.PeakTemperature,
                _plannedForecast.PeakTime);
        }

        EstimationDto? estimation = null;
        if (_solution is not null)
        {
            estimation = new EstimationDto(
                _lastEstimationTime,
                _scenario.Estimator.Regularization.ToString(),
                _solution.Selection.ToString(),
                _solution.RelativeLambda,
                _solution.ResidualNorm,
                _solution.SolutionSeminorm,
                _solution.MeasurementCount,
                _estimator.Unknowns,
                _temperatureRmse ?? 0,
                _sourceError ?? 0,
                Math.Round(_lastSolveMs, 1));
        }

        Dictionary<string, FieldDto>? fields = null;
        if (includeFields)
        {
            fields = new Dictionary<string, FieldDto>
            {
                ["trueTemperature"] = FieldDto.Create("trueTemperature", "True temperature field", "°C", _grid, truth),
                ["trueSource"] = FieldDto.Create("trueSource", "True heat source (full load)", "kW/m³", _grid, _trueSource, 1e-3),
            };

            if (_estimatedTemperature is not null && _solution is not null)
            {
                fields["estimatedTemperature"] = FieldDto.Create("estimatedTemperature", "Estimated temperature field", "°C", _grid, _estimatedTemperature);
                fields["estimationError"] = FieldDto.Create("estimationError", "Estimation error T̂ − T", "K", _grid, ErrorMetrics.Difference(_estimatedTemperature, truth));
                fields["estimatedSource"] = FieldDto.Create("estimatedSource", "Reconstructed heat source", "kW/m³", _grid, _solution.SourceField, 1e-3);
            }

            if (_plannedForecast is not null)
            {
                fields["predictedTemperature"] = FieldDto.Create("predictedTemperature",
                    $"Predicted temperature at t = {_plannedForecast.Times[^1]:0} s", "°C", _grid, _plannedForecast.FinalField);
            }
        }

        var newHistory = _history.Skip(_historyCursor).ToArray();
        _historyCursor = _history.Count;

        return new TwinFrame(
            RunId,
            _scenario.Key,
            _scenario.Name,
            status,
            Mode,
            Step,
            TotalSteps,
            Time,
            _scenario.Solver.Duration,
            _load.At(Time),
            truthStats,
            estimateStats,
            _estimatedHotspot is null ? null : new HotspotDto(_estimatedHotspot.X, _estimatedHotspot.Y, _estimatedHotspot.PeakValue / 1000,
                Math.Round(_estimatedHotspot.Area * 1e6, 1), Math.Round(_estimatedHotspot.Prominence, 3)),
            _scenario.HeatSource.Hotspots.Select(h => new HotspotDto(h.X, h.Y, h.PeakPower / 1000, null, null)).ToArray(),
            _hotspotErrorMm,
            _risk,
            Mode == CoolingMode.Autonomous && _optimization is not null && _level > _scenario.Cooling.BaselineLevel + 1e-3,
            _scenario.Control.SafeTemperature,
            _scenario.Control.CriticalTemperature,
            Vector.Max(_counterfactual.TemperatureOnModelGrid()),
            cooling,
            forecast,
            estimation,
            sensors,
            fields,
            newHistory);
    }

    /// <summary>Resets the incremental history cursor so the next frame carries the full history.</summary>
    public void ResetHistoryCursor() => _historyCursor = 0;

    private TemperatureStats Stats(double[] field)
    {
        var (x, y, max) = HotspotDetector.Maximum(_grid, field);
        var min = Vector.Min(field);
        return new TemperatureStats(Math.Round(max, 3), Math.Round(min, 3), Math.Round(Vector.Mean(field), 3), Math.Round(max - min, 3), x, y);
    }
}
