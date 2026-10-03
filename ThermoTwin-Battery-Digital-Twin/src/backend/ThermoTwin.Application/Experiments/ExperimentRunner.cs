using System.Diagnostics;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Enums;
using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Inverse;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Prediction;
using ThermoTwin.Numerics.Simulation;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Application.Experiments;

/// <summary>
/// Reproducible numerical experiments. Every number reported by the dashboard and the README is
/// produced here from actual solver runs of the configured scenario (default: the built-in demo).
/// </summary>
public sealed class ExperimentRunner
{
    private readonly ScenarioDefinition _scenario;
    private readonly ScenarioFactory _factory;

    public ExperimentRunner(ScenarioDefinition? scenario = null)
    {
        _scenario = scenario ?? ScenarioCatalog.RapidChargeHiddenHotspot;
        _factory = new ScenarioFactory(_scenario);
    }

    // =====================================================================================
    // 1. Numerical convergence, stability, conservation and performance
    // =====================================================================================

    public ConvergenceExperimentResult RunConvergence()
    {
        (int, int)[] grids = [(20, 10), (40, 20), (80, 40), (160, 80)];
        var spatial = new List<ConvergenceRow>();
        foreach (var scheme in new[] { TimeScheme.ExplicitEuler, TimeScheme.ImplicitEuler, TimeScheme.CrankNicolson })
        {
            spatial.AddRange(ConvergenceStudy.SpatialStudy(scheme, grids));
        }

        var temporal = new List<ConvergenceRow>();
        temporal.AddRange(ConvergenceStudy.TemporalStudy(TimeScheme.ExplicitEuler, [0.64, 0.32, 0.16, 0.08, 0.04]));
        temporal.AddRange(ConvergenceStudy.TemporalStudy(TimeScheme.ImplicitEuler, [20, 10, 5, 2.5, 1.25]));
        temporal.AddRange(ConvergenceStudy.TemporalStudy(TimeScheme.CrankNicolson, [20, 10, 5, 2.5, 1.25]));

        var probes = ConvergenceStudy.ExplicitStabilityProbe([0.5, 0.9, 0.99, 1.01, 1.1, 1.5]);

        var model = _factory.ThermalModel();
        var reports = new List<StabilityReport>
        {
            StabilityAnalyzer.Analyze(new HeatEquationSolver(model, TimeScheme.ExplicitEuler, _scenario.Solver.TimeStep), 1),
            StabilityAnalyzer.Analyze(new HeatEquationSolver(model, TimeScheme.ExplicitEuler, 0.5), 1),
            StabilityAnalyzer.Analyze(new HeatEquationSolver(model, TimeScheme.ImplicitEuler, _scenario.Solver.TimeStep), 1),
            StabilityAnalyzer.Analyze(new HeatEquationSolver(model, TimeScheme.CrankNicolson, _scenario.Solver.TimeStep), 1),
        };

        var energy = new[] { TimeScheme.ExplicitEuler, TimeScheme.ImplicitEuler, TimeScheme.CrankNicolson }
            .Select(s =>
            {
                var r = ConvergenceStudy.EnergyConservation(s);
                return new SchemeEnergyBalance(s, r.InjectedEnergy, r.StoredEnergyChange, r.RelativeImbalance);
            })
            .ToArray();

        return new ConvergenceExperimentResult(spatial, temporal, probes, reports, energy, MeasurePerformance());
    }

    private List<PerformanceRow> MeasurePerformance()
    {
        var rows = new List<PerformanceRow>();
        var model = _factory.ThermalModel();
        foreach (var (nx, ny) in new[] { (40, 20), (80, 40), (160, 80) })
        {
            var grid = new Grid2D(nx, ny, model.Grid.LengthX, model.Grid.LengthY);
            var solver = new HeatEquationSolver(model.WithGrid(grid), TimeScheme.CrankNicolson, _scenario.Solver.TimeStep);
            var u = grid.CreateField(25);
            var next = new double[u.Length];
            var q = grid.CreateField(5e4);
            solver.Step(u, next, q, q, 0.2); // warm-up incl. factorisation
            const int steps = 200;
            var sw = Stopwatch.StartNew();
            for (var s = 0; s < steps; s++)
            {
                solver.Step(u, next, q, q, 0.2);
                (u, next) = (next, u);
            }

            sw.Stop();
            rows.Add(new PerformanceRow($"Crank–Nicolson step {nx}×{ny}", nx, ny, "CrankNicolson", steps, sw.Elapsed.TotalMilliseconds,
                sw.Elapsed.TotalMilliseconds / steps));
        }

        // End-to-end: full 30-minute plant simulation (fine grid) + online inverse estimator.
        var plant = _factory.Plant();
        var estimator = _factory.Estimator();
        var total = (int)Math.Round(_scenario.Solver.Duration / _scenario.Solver.TimeStep);
        var plantMs = 0.0;
        var estimatorMs = 0.0;
        for (var n = 0; n < total; n++)
        {
            var sw = Stopwatch.StartNew();
            var y = plant.Step(_scenario.Cooling.BaselineLevel);
            plantMs += sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            estimator.Assimilate(_scenario.Cooling.BaselineLevel, y);
            estimatorMs += sw.Elapsed.TotalMilliseconds;
        }

        var g = plant.FineGrid;
        rows.Add(new PerformanceRow($"Plant simulation {_scenario.Solver.Duration:0} s ({g.Nx}×{g.Ny})", g.Nx, g.Ny, "CrankNicolson", total, plantMs, plantMs / total));
        rows.Add(new PerformanceRow($"Inverse assimilation, {estimator.Unknowns} basis responses", _scenario.Geometry.Nx, _scenario.Geometry.Ny,
            "CrankNicolson", total, estimatorMs, estimatorMs / total));

        var gcvWatch = Stopwatch.StartNew();
        estimator.SolveAuto(LambdaSelection.Gcv, _scenario.Estimator.Regularization, _scenario.Sensors.NoiseStd, _scenario.Estimator.FixedLambda);
        rows.Add(new PerformanceRow("Tikhonov solve incl. GCV λ search", estimator.Unknowns, 1, "Cholesky", 1, gcvWatch.Elapsed.TotalMilliseconds,
            gcvWatch.Elapsed.TotalMilliseconds));
        return rows;
    }

    // =====================================================================================
    // 2. Inverse problem: L-curve, GCV, discrepancy principle, regulariser comparison
    // =====================================================================================

    public RegularizationExperimentResult RunRegularization()
    {
        var plant = _factory.Plant();
        var estimator = _factory.Estimator();
        var grid = _factory.ModelGrid();
        var trueSource = plant.SourceOnModelGrid();
        var total = (int)Math.Round(_scenario.Solver.Duration / _scenario.Solver.TimeStep);
        var every = (int)Math.Round(60 / _scenario.Solver.TimeStep);
        var timeline = new List<ReconstructionTimelinePoint>();

        for (var n = 1; n <= total; n++)
        {
            var y = plant.Step(_scenario.Cooling.BaselineLevel);
            estimator.Assimilate(_scenario.Cooling.BaselineLevel, y);
            if (n % every == 0)
            {
                var truth = plant.TemperatureOnModelGrid();
                var lambda = estimator.GcvLambda(_scenario.Estimator.Regularization);
                var s = estimator.Solve(lambda, _scenario.Estimator.Regularization);
                var m = Metrics(grid, s, truth, trueSource);
                timeline.Add(new ReconstructionTimelinePoint(plant.Time, lambda, m.TemperatureRmse, m.SourceRelativeError, m.HotspotErrorMm,
                    Vector.Max(truth), Vector.Max(s.TemperatureField)));
            }
        }

        var finalTruth = plant.TemperatureOnModelGrid();
        var studies = new List<RegularizationStudy>();
        foreach (var kind in new[] { RegularizationKind.Identity, RegularizationKind.Gradient, RegularizationKind.Laplacian })
        {
            var lcurve = estimator.ComputeLCurve(kind, 1e-8, 1e1, 37);
            var curve = lcurve.Points.Select(p =>
            {
                var s = estimator.Solve(p.RelativeLambda, kind);
                var m = Metrics(grid, s, finalTruth, trueSource);
                return new LCurveSample(p.RelativeLambda, p.ResidualNorm, p.SolutionSeminorm, p.Curvature, p.Gcv,
                    m.SourceRelativeError, m.TemperatureRmse, m.HotspotErrorMm);
            }).ToArray();

            var oracle = curve.MinBy(c => c.SourceRelativeError)!.RelativeLambda;
            var choices = new List<LambdaChoice>();
            foreach (var (method, lambda) in new[]
                     {
                         ("L-curve corner", lcurve.Corner.RelativeLambda),
                         ("GCV", estimator.GcvLambda(kind)),
                         ("Discrepancy principle", estimator.DiscrepancyLambda(kind, _scenario.Sensors.NoiseStd)),
                         ("Oracle (min. source error)", oracle),
                     })
            {
                var s = estimator.Solve(lambda, kind);
                choices.Add(new LambdaChoice(method, lambda, s.ResidualNorm, s.SolutionSeminorm, Metrics(grid, s, finalTruth, trueSource)));
            }

            studies.Add(new RegularizationStudy(kind.ToString(), curve, choices));
        }

        var kindUsed = _scenario.Estimator.Regularization;
        var gcv = estimator.GcvLambda(kindUsed);
        var fields = new Dictionary<string, FieldDto>
        {
            ["trueSource"] = FieldDto.Create("trueSource", "True heat source", "kW/m³", grid, trueSource, 1e-3),
            ["underRegularized"] = FieldDto.Create("underRegularized", $"Under-regularised (λ = {gcv * 1e-3:0.0e+0})", "kW/m³", grid,
                estimator.Solve(gcv * 1e-3, kindUsed).SourceField, 1e-3),
            ["gcvSource"] = FieldDto.Create("gcvSource", $"GCV choice (λ = {gcv:0.0e+0})", "kW/m³", grid,
                estimator.Solve(gcv, kindUsed).SourceField, 1e-3),
            ["overRegularized"] = FieldDto.Create("overRegularized", $"Over-regularised (λ = {gcv * 1e3:0.0e+0})", "kW/m³", grid,
                estimator.Solve(gcv * 1e3, kindUsed).SourceField, 1e-3),
            ["trueTemperature"] = FieldDto.Create("trueTemperature", "True temperature (t = end)", "°C", grid, finalTruth),
            ["estimatedTemperature"] = FieldDto.Create("estimatedTemperature", "Estimated temperature (GCV)", "°C", grid,
                estimator.Solve(gcv, kindUsed).TemperatureField),
        };

        var sensors = _factory.Sensors().Select(s => new SensorReadingDto(s.Id, s.X, s.Y, 0, 0)).ToArray();
        var hotspots = _scenario.HeatSource.Hotspots.Select(h => new HotspotDto(h.X, h.Y, h.PeakPower / 1000, null, null)).ToArray();
        return new RegularizationExperimentResult(
            estimator.MeasurementCount,
            estimator.Unknowns,
            _scenario.Sensors.Count,
            _scenario.Sensors.NoiseStd,
            Math.Sqrt(estimator.MeasurementCount) * _scenario.Sensors.NoiseStd,
            studies,
            timeline,
            fields,
            sensors,
            hotspots);
    }

    // =====================================================================================
    // 3/4. Sensitivity to sensor density and measurement noise
    // =====================================================================================

    public SensitivityExperimentResult RunSensorDensity(IReadOnlyList<int>? counts = null)
    {
        counts ??= [4, 6, 8, 12, 16, 24, 32];
        var rows = counts.AsParallel().AsOrdered()
            .Select(c => RunReconstruction(sensorCount: c, noiseStd: null, parameter: c))
            .ToArray();
        return new SensitivityExperimentResult("Sensor count", "sensors", rows);
    }

    public SensitivityExperimentResult RunNoiseRobustness(IReadOnlyList<double>? levels = null)
    {
        levels ??= [0.0, 0.05, 0.1, 0.2, 0.5, 1.0];
        var rows = levels.AsParallel().AsOrdered()
            .Select(sigma => RunReconstruction(sensorCount: null, noiseStd: sigma, parameter: sigma))
            .ToArray();
        return new SensitivityExperimentResult("Noise standard deviation σ", "K", rows);
    }

    private SensitivityRow RunReconstruction(int? sensorCount, double? noiseStd, double parameter)
    {
        var plant = _factory.Plant(noiseStd, sensorCount);
        var estimator = _factory.Estimator(sensorCount);
        var total = (int)Math.Round(_scenario.Solver.Duration / _scenario.Solver.TimeStep);
        for (var n = 0; n < total; n++)
        {
            estimator.Assimilate(_scenario.Cooling.BaselineLevel, plant.Step(_scenario.Cooling.BaselineLevel));
        }

        var lambda = estimator.GcvLambda(_scenario.Estimator.Regularization);
        var s = estimator.Solve(lambda, _scenario.Estimator.Regularization);
        return new SensitivityRow(parameter, lambda, Metrics(_factory.ModelGrid(), s, plant.TemperatureOnModelGrid(), plant.SourceOnModelGrid()));
    }

    // =====================================================================================
    // 5. Forecast accuracy: prediction vs actual
    // =====================================================================================

    public ForecastExperimentResult RunForecastAccuracy(IReadOnlyList<double>? issueTimes = null, double horizon = 600)
    {
        issueTimes ??= [300, 600, 900, 1200];
        var plant = _factory.Plant();
        var estimator = _factory.Estimator();
        var predictor = _factory.Predictor();
        var dt = _scenario.Solver.TimeStep;
        var total = (int)Math.Round(_scenario.Solver.Duration / dt);
        var baseline = _scenario.Cooling.BaselineLevel;
        var actual = new SortedDictionary<double, double> { [0] = Vector.Max(plant.TemperatureOnModelGrid()) };
        var pending = new List<(double IssuedAt, ThermalForecast Forecast)>();

        for (var n = 1; n <= total; n++)
        {
            estimator.Assimilate(baseline, plant.Step(baseline));
            actual[Math.Round(plant.Time, 6)] = Vector.Max(plant.TemperatureOnModelGrid());
            if (issueTimes.Any(t => Math.Abs(t - plant.Time) < 1e-6))
            {
                var s = estimator.SolveAuto(_scenario.Estimator.LambdaSelection, _scenario.Estimator.Regularization, _scenario.Sensors.NoiseStd,
                    _scenario.Estimator.FixedLambda);
                var steps = (int)Math.Round(horizon / _scenario.Control.SegmentDuration);
                pending.Add((plant.Time, predictor.Predict(s.TemperatureField, s.SourceField, plant.Time,
                    CoolingPlan.Constant(baseline, steps, _scenario.Control.SegmentDuration))));
            }
        }

        var traces = pending.Select(p =>
        {
            var times = p.Forecast.Times.Where(t => actual.ContainsKey(Math.Round(t, 6))).ToArray();
            var predicted = times.Select(t => Math.Round(p.Forecast.MaxTemperature[Array.IndexOf(p.Forecast.Times, t)], 4)).ToArray();
            var observed = times.Select(t => Math.Round(actual[Math.Round(t, 6)], 4)).ToArray();
            var mae = predicted.Zip(observed, (a, b) => Math.Abs(a - b)).Average();
            return new ForecastTrace(p.IssuedAt, horizon, times, predicted, observed, mae,
                predicted[^1] - observed[^1], predicted.Max(), observed.Max());
        }).ToArray();

        var downsampled = actual.Where((_, i) => i % 2 == 0).ToArray();
        return new ForecastExperimentResult(traces, downsampled.Select(a => a.Key).ToArray(),
            downsampled.Select(a => Math.Round(a.Value, 4)).ToArray());
    }

    // =====================================================================================
    // 6. Cooling strategies: none / constant / optimised open-loop / closed-loop MPC
    // =====================================================================================

    public CoolingComparisonResult RunCoolingComparison()
    {
        const double segmentDuration = 100;
        var duration = _scenario.Solver.Duration;
        var segments = (int)Math.Round(duration / segmentDuration);
        var safe = _scenario.Control.SafeTemperature;
        var options = _factory.OptimizationOptions(segments, segmentDuration) with { MaxIterationsPerStage = 40 };
        var cooling = _factory.Cooling();
        var referenceEnergy = cooling.RatedPower * duration;

        // Identify the hidden source from a previous charge cycle run with baseline cooling.
        var learningPlant = _factory.Plant(seedOffset: 500);
        var estimator = _factory.Estimator();
        var total = (int)Math.Round(duration / _scenario.Solver.TimeStep);
        for (var n = 0; n < total; n++)
        {
            estimator.Assimilate(_scenario.Cooling.BaselineLevel, learningPlant.Step(_scenario.Cooling.BaselineLevel));
        }

        var identified = estimator.SolveAuto(_scenario.Estimator.LambdaSelection, _scenario.Estimator.Regularization,
            _scenario.Sensors.NoiseStd, _scenario.Estimator.FixedLambda);
        var initial = _factory.ModelGrid().CreateField(_scenario.Environment.InitialTemperature);
        var optimizer = new CoolingOptimizer(_factory.Predictor());

        var sw = Stopwatch.StartNew();
        var optimized = optimizer.Optimize(initial, identified.SourceField, 0, options, Enumerable.Repeat(0.3, segments).ToArray());
        var optimizationMs = sw.Elapsed.TotalMilliseconds;
        var minimumConstant = optimizer.MinimumFeasibleConstantLevel(initial, identified.SourceField, 0, options);

        var strategies = new List<StrategyOutcome>
        {
            Evaluate("none", "No cooling", "Cold plate pump off (u = 0).", _ => 0),
            Evaluate("baseline", $"Constant baseline ({_scenario.Cooling.BaselineLevel:P0})",
                "Fixed low-flow cooling — the vehicle's default setting.", _ => _scenario.Cooling.BaselineLevel),
            Evaluate("full", "Constant full cooling", "Pump at 100 % for the whole charge (conservative rule).", _ => 1),
            Evaluate("min-constant", $"Constant minimal-feasible ({minimumConstant:P1})",
                "Smallest constant level that satisfies T_max ≤ T_safe − margin on the twin (bisection).", _ => minimumConstant),
            Evaluate("optimized", "Optimised schedule (open loop)",
                $"{segments}×{segmentDuration:0} s piecewise-constant plan from the penalty/projected-gradient optimiser.",
                t => optimized.Plan.LevelAt(t)),
            EvaluateMpc(),
        };

        return new CoolingComparisonResult(
            safe,
            options.SafeTemperature,
            referenceEnergy,
            minimumConstant,
            optimized.Plan.Levels.Select(v => Math.Round(v, 4)).ToArray(),
            segmentDuration,
            strategies,
            optimized.History.Select(h => new OptimizationTracePoint(h.Iteration, h.Penalty, h.Objective, h.EnergyTerm, h.Violation, h.PeakTemperature)).ToArray(),
            optimizationMs,
            optimized.Evaluations);

        StrategyOutcome Evaluate(string key, string name, string description, Func<double, double> policy)
        {
            var plant = _factory.Plant();
            var dt = _scenario.Solver.TimeStep;
            var times = new List<double> { 0 };
            var maxT = new List<double> { Vector.Max(plant.TemperatureOnModelGrid()) };
            var levels = new List<double> { policy(0) };
            var energy = 0.0;
            var previous = policy(0);
            var smooth = 0.0;
            for (var n = 0; n < total; n++)
            {
                var u = policy(plant.Time);
                smooth += (u - previous) * (u - previous);
                previous = u;
                plant.Step(u);
                energy += cooling.Power(u) * dt;
                times.Add(plant.Time);
                maxT.Add(Vector.Max(plant.TemperatureOnModelGrid()));
                levels.Add(u);
            }

            return Outcome(key, name, description, times, maxT, levels, energy, smooth);
        }

        StrategyOutcome EvaluateMpc()
        {
            var engine = new DigitalTwinEngine(_scenario with { Cooling = _scenario.Cooling with { Mode = CoolingMode.Autonomous } }, Guid.Empty);
            while (!engine.IsComplete)
            {
                engine.Advance();
            }

            var history = engine.History;
            var times = new List<double> { 0 };
            var maxT = new List<double> { _scenario.Environment.InitialTemperature };
            var levels = new List<double> { _scenario.Cooling.BaselineLevel };
            var smooth = 0.0;
            var previous = _scenario.Cooling.BaselineLevel;
            foreach (var h in history)
            {
                times.Add(h.Time);
                maxT.Add(h.TrueMax);
                levels.Add(h.CoolingLevel);
                smooth += (h.CoolingLevel - previous) * (h.CoolingLevel - previous);
                previous = h.CoolingLevel;
            }

            return Outcome("mpc", "Closed-loop MPC (live twin)",
                "Receding-horizon control from the live digital twin: no prior knowledge of the defect, re-estimated every 30 s, re-optimised every 60 s.",
                times, maxT, levels, engine.CoolingEnergy, smooth);
        }

        StrategyOutcome Outcome(string key, string name, string description, List<double> times, List<double> maxT, List<double> levels,
            double energy, double smooth)
        {
            var dt = _scenario.Solver.TimeStep;
            var excess = maxT.Skip(1).Select(t => Math.Max(0, t - safe)).ToArray();
            var meanSquare = excess.Select(e => e * e).Average();
            var penalty = options.Penalties[^1];
            var objective = energy / referenceEnergy + options.SmoothnessWeight * smooth + penalty * meanSquare;
            var keep = Enumerable.Range(0, times.Count).Where(i => i % 2 == 0).ToArray();
            return new StrategyOutcome(
                key,
                name,
                description,
                Math.Round(maxT.Max(), 3),
                Math.Round(energy, 1),
                Math.Round(excess.Max(), 3),
                excess.Count(e => e > 0) * dt,
                Math.Round(excess.Sum() * dt, 2),
                Math.Round(objective, 5),
                excess.Max() <= 1e-9,
                keep.Select(i => times[i]).ToArray(),
                keep.Select(i => Math.Round(maxT[i], 3)).ToArray(),
                keep.Select(i => Math.Round(levels[i], 4)).ToArray());
        }
    }

    // =====================================================================================

    private ReconstructionMetrics Metrics(Grid2D grid, InverseSolution s, double[] trueTemperature, double[] trueSource)
    {
        var hotspot = HotspotDetector.Detect(grid, s.SourceField);
        double? error = hotspot is null || _scenario.HeatSource.Hotspots.Count == 0
            ? null
            : _scenario.HeatSource.Hotspots.Min(h => Math.Sqrt(Math.Pow(h.X - hotspot.X, 2) + Math.Pow(h.Y - hotspot.Y, 2))) * 1000;
        return new ReconstructionMetrics(
            Math.Round(ErrorMetrics.Rmse(s.TemperatureField, trueTemperature), 5),
            Math.Round(ErrorMetrics.MaxError(s.TemperatureField, trueTemperature), 5),
            Math.Round(ErrorMetrics.RelativeL2(s.SourceField, trueSource), 5),
            error is null ? null : Math.Round(error.Value, 3),
            hotspot?.X,
            hotspot?.Y,
            Math.Round(Vector.Max(s.SourceField) / 1000, 2));
    }

    public static string Describe(ExperimentKind kind) => kind switch
    {
        ExperimentKind.NumericalConvergence => "Spatial/temporal convergence, explicit stability limit, energy conservation and solver performance",
        ExperimentKind.Regularization => "L-curve, GCV, discrepancy principle and regulariser comparison for the inverse heat-source problem",
        ExperimentKind.SensorDensity => "Reconstruction accuracy versus number of thermistors",
        ExperimentKind.NoiseRobustness => "Reconstruction accuracy versus sensor noise level",
        ExperimentKind.ForecastAccuracy => "Predicted versus actual peak temperature at several issue times",
        ExperimentKind.CoolingComparison => "No / constant / optimised / MPC cooling evaluated on the ground-truth plant",
        _ => kind.ToString(),
    };
}
