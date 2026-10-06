using System.Diagnostics;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Enums;
using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Fem;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Prediction;
using ThermoTwin.Numerics.ReducedOrder;
using ThermoTwin.Numerics.Sensitivity;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Application.Experiments;

/// <summary>
/// Experiments 7–12: finite elements, discrete adjoint, PDE-constrained optimisation, POD reduced-order modelling,
/// reduced-order MPC and parameter identifiability. All use the configured scenario (default: the built-in demo)
/// and fixed seeds; only wall-clock timings vary between runs.
/// </summary>
public sealed partial class ExperimentRunner
{
    private double[]? _identifiedSource;

    // =====================================================================================
    // 7. Finite elements: convergence and FVM–FEM cross-validation
    // =====================================================================================

    public FemVerificationResult RunFemVerification()
    {
        (int, int)[] grids = [(10, 5), (20, 10), (40, 20), (80, 40), (160, 80)];
        var eigenmode = FemVerificationStudy.Run(FemVerificationStudy.DirichletEigenmode, grids);
        var manufactured = FemVerificationStudy.Run(FemVerificationStudy.ManufacturedCooling, grids);

        var model = _factory.ThermalModel();
        var display = (_scenario.Geometry.Nx, _scenario.Geometry.Ny);
        var level = _scenario.Cooling.BaselineLevel;
        var snapshot = Math.Min(1200, _scenario.Solver.Duration);
        var batteryGrids = new List<(int, int)> { (display.Nx / 2, display.Ny / 2), display, (display.Nx * 2, display.Ny * 2), (display.Nx * 4, display.Ny * 4) }
            .Where(g => g.Item1 >= 2 && g.Item2 >= 2)
            .ToArray();
        var (battery, fields) = FemVerificationStudy.CompareOnBatteryModel(model, _factory.HeatSource(), level,
            _scenario.Environment.InitialTemperature, _scenario.Solver.Duration, _scenario.Solver.TimeStep, batteryGrids, display, snapshot);

        var grid = fields.Grid;
        var dto = new Dictionary<string, FieldDto>
        {
            ["fvm"] = FieldDto.Create("fvm", $"Finite volumes · cell centres · t = {fields.Time:0} s", "°C", grid, fields.Fvm),
            ["fem"] = FieldDto.FromRows("fem", $"Finite elements · P1 nodes · t = {fields.Time:0} s", "°C", grid.LengthX, grid.LengthY,
                fields.Mesh.ToRows(fields.FemNodal)),
            ["femCells"] = FieldDto.Create("femCells", "Finite elements evaluated at the cell centres", "°C", grid, fields.FemAtCellCentres),
            ["difference"] = FieldDto.Create("difference", "T_FEM − T_FVM at the cell centres", "K", grid, fields.Difference, 1, 5),
        };

        var mesh = new FemMesh2D(10, 5, grid.LengthX, grid.LengthY);
        var meshDto = new FemMeshDto(mesh.Nx, mesh.Ny, mesh.LengthX, mesh.LengthY, mesh.NodeCount, mesh.ElementCount, mesh.BoundarySegments.Count,
            mesh.Nodes.Select(n => new[] { Math.Round(n.X, 6), Math.Round(n.Y, 6) }).ToArray(),
            mesh.Elements.Select(e => e.Nodes.ToArray()).ToArray());

        var row = battery.First(r => (r.Nx, r.Ny) == display);
        var systems = new[]
        {
            new DiscretisationSystemDto("FVM", "cell averages", row.FvmDofs, row.FvmNonZeros, display.Ny, Math.Round((double)row.FvmNonZeros / row.FvmDofs, 2)),
            new DiscretisationSystemDto("FEM", "nodal values (P1)", row.FemDofs, row.FemNonZeros, display.Ny + 2, Math.Round((double)row.FemNonZeros / row.FemDofs, 2)),
        };

        return new FemVerificationResult(eigenmode, manufactured, battery, level, fields.Time, dto, meshDto, systems);
    }

    // =====================================================================================
    // 8. Discrete adjoint: gradient check, Taylor test, cost versus number of controls
    // =====================================================================================

    public AdjointCheckResult RunAdjointGradientCheck()
    {
        const double mu = 1e4;
        const double segmentDuration = 100;
        var solver = _factory.PredictionSolver();
        var fullOrder = new FullOrderThermalDynamics(solver);
        var load = _factory.LoadProfile();
        var cooling = _factory.Cooling();
        var grid = solver.Model.Grid;
        var initial = grid.CreateField(_scenario.Environment.InitialTemperature);
        var shape = _factory.Plant().SourceOnModelGrid();
        var horizon = _scenario.Solver.Duration;
        var segments = Math.Max(1, (int)Math.Round(horizon / segmentDuration));
        var safe = _scenario.Control.SafeTemperature - _scenario.Control.ControlMargin;
        var settings = new ControlProblemSettings(safe, segments, segmentDuration, _factory.OptimizationOptions().SmoothnessWeight);
        var objective = new PdeConstrainedObjective(fullOrder, load, cooling, initial, shape, 0, settings);
        double[] epsilons = [1e-1, 1e-2, 1e-3, 1e-4, 1e-5, 1e-6, 1e-7, 1e-8];

        var rows = new List<GradientCheckRow>();
        double[]? componentsAdjoint = null, componentsFd = null, firstVector = null;
        for (var v = 0; v < 4; v++)
        {
            var rng = new Random(100 + v);
            var u = Enumerable.Range(0, segments).Select(_ => 0.05 + 0.5 * rng.NextDouble()).ToArray();
            var adjoint = objective.EvaluateWithGradient(u, mu).Gradient!;
            foreach (var eps in epsilons)
            {
                var fd = objective.FiniteDifferenceGradient(u, mu, eps, central: true);
                var diff = Vector.Subtract(adjoint, fd);
                rows.Add(new GradientCheckRow("Full-order FVM", v, eps, Vector.Norm2(diff) / Vector.Norm2(fd), Vector.NormInf(diff),
                    Vector.Norm2(adjoint), Vector.Norm2(fd)));
                if (v == 0 && Math.Abs(eps - 1e-6) < 1e-18)
                {
                    componentsAdjoint = adjoint;
                    componentsFd = fd;
                    firstVector = u;
                }
            }
        }

        // Taylor test along a fixed random direction.
        var dirRng = new Random(7);
        var direction = Enumerable.Range(0, segments).Select(_ => dirRng.NextDouble() - 0.5).ToArray();
        var dNorm = Vector.Norm2(direction);
        direction = direction.Select(x => x / dNorm).ToArray();
        var baseEval = objective.EvaluateWithGradient(firstVector!, mu);
        var slope = Vector.Dot(baseEval.Gradient!, direction);
        var taylor = new List<TaylorTestRow>();
        foreach (var eps in Vector.LogSpace(1e-1, 1e-5, 9))
        {
            var shifted = firstVector!.Select((x, k) => Math.Clamp(x + eps * direction[k], 0, 1)).ToArray();
            var value = objective.Evaluate(shifted, mu).Value;
            taylor.Add(new TaylorTestRow(eps, Math.Abs(value - baseEval.Value), Math.Abs(value - baseEval.Value - eps * slope)));
        }

        // The same check for the adjoint of the POD reduced model (gradient in reduced coordinates).
        var pod = _factory.TrainPod(solver).Basis;
        var rom = new ReducedThermalModel(pod, Math.Min(_scenario.Control.RomModes, pod.Rank), solver);
        var romObjective = new PdeConstrainedObjective(rom, load, cooling, initial, shape, 0, settings);
        var romAdjoint = romObjective.EvaluateWithGradient(firstVector!, mu).Gradient!;
        foreach (var eps in epsilons)
        {
            var fd = romObjective.FiniteDifferenceGradient(firstVector!, mu, eps, central: true);
            var diff = Vector.Subtract(romAdjoint, fd);
            rows.Add(new GradientCheckRow($"POD ROM (r = {rom.Modes})", 0, eps, Vector.Norm2(diff) / Vector.Norm2(fd), Vector.NormInf(diff),
                Vector.Norm2(romAdjoint), Vector.Norm2(fd)));
        }

        // Cost of one gradient versus the number of control segments K (fixed horizon).
        var cost = new List<GradientCostRow>();
        foreach (var k in new[] { 3, 6, 9, 18, 36, 60, 90 })
        {
            var duration = horizon / k;
            if (Math.Abs(duration / solver.TimeStep - Math.Round(duration / solver.TimeStep)) > 1e-9)
            {
                continue;
            }

            var obj = new PdeConstrainedObjective(fullOrder, load, cooling, initial, shape, 0, settings with { Segments = k, SegmentDuration = duration });
            var u = Enumerable.Range(0, k).Select(j => 0.25 + 0.15 * Math.Sin(0.7 * j)).ToArray();
            var adjointMs = Median(3, () => obj.EvaluateWithGradient(u, mu));
            var adjoint = obj.EvaluateWithGradient(u, mu).Gradient!;
            var sw = Stopwatch.StartNew();
            obj.FiniteDifferenceGradient(u, mu, 1e-4, central: false);
            var forwardMs = sw.Elapsed.TotalMilliseconds;
            sw.Restart();
            var central = obj.FiniteDifferenceGradient(u, mu, 1e-6, central: true);
            var centralMs = sw.Elapsed.TotalMilliseconds;
            cost.Add(new GradientCostRow(k, duration, Math.Round(adjointMs, 2), Math.Round(forwardMs, 1), Math.Round(centralMs, 1), 2, k + 1, 2 * k,
                Vector.Norm2(Vector.Subtract(adjoint, central)) / Vector.Norm2(central)));
        }

        var fom = rows.Where(r => r.Model == "Full-order FVM").ToArray();
        var best = fom.GroupBy(r => r.Epsilon).Select(g => (Eps: g.Key, Error: g.Max(r => r.RelativeError))).MinBy(x => x.Error);
        var components = Enumerable.Range(0, segments)
            .Select(j => new GradientComponent(j, Math.Round(firstVector![j], 6), componentsAdjoint![j], componentsFd![j]))
            .ToArray();
        return new AdjointCheckResult(fullOrder.Name, fullOrder.StateDimension, objective.Steps, segments, mu, safe, rows, taylor, components, cost,
            best.Error, best.Eps, rows.Where(r => r.Model != "Full-order FVM").Min(r => r.RelativeError));
    }

    // =====================================================================================
    // 9. PDE-constrained optimisation: adjoint vs finite differences vs ROM vs legacy
    // =====================================================================================

    public OptimizationBenchmarkResult RunOptimizationBenchmark()
    {
        const double segmentDuration = 100;
        var segments = Math.Max(1, (int)Math.Round(_scenario.Solver.Duration / segmentDuration));
        var options = _factory.OptimizationOptions(segments, segmentDuration) with { MaxIterationsPerStage = 40 };
        var source = IdentifiedSource();
        var solver = _factory.PredictionSolver();
        var fullOrder = new FullOrderThermalDynamics(solver);
        var load = _factory.LoadProfile();
        var cooling = _factory.Cooling();
        var initial = solver.Model.Grid.CreateField(_scenario.Environment.InitialTemperature);
        var guess = Enumerable.Repeat(0.3, segments).ToArray();
        var reference = new AdjointCoolingOptimizer(fullOrder, load, cooling);
        var diagnosticsObjective = reference.CreateObjective(initial, source, 0, options);
        var penalties = new PdeOptimizerSettings().Penalties;

        var outcomes = new List<OptimizationMethodOutcome>();

        OptimizationMethodOutcome Outcome(string key, string name, string gradient, CoolingOptimizationResult result, double ms,
            IReadOnlyList<PdeOptimizationIteration> history, int fullOrderEquivalents)
        {
            var plan = result.Plan.Levels;
            var evaluation = diagnosticsObjective.Evaluate(plan, 0);
            var kkt = AdjointCoolingOptimizer.Diagnose(diagnosticsObjective, plan, penalties[^1]);
            var plant = PlantRun(t => result.Plan.LevelAt(t));
            var keep = Enumerable.Range(0, evaluation.Forecast.Times.Length).Where(i => i % 3 == 0).ToArray();
            return new OptimizationMethodOutcome(key, name, gradient, result.Model, evaluation.Objective, Math.Round(evaluation.EnergyJoules, 2),
                Math.Round(evaluation.PeakTemperature, 4), Math.Round(evaluation.MaxViolation, 4), Math.Round(plant.MaxTemperature.Max(), 4),
                Math.Round(Math.Max(0, plant.MaxTemperature.Max() - _scenario.Control.SafeTemperature), 4), result.Feasible,
                history.Count, result.Evaluations, result.AdjointSolves, fullOrderEquivalents, Math.Round(ms, 1),
                plan.Select(v => Math.Round(v, 5)).ToArray(), kkt, history,
                keep.Select(i => evaluation.Forecast.Times[i]).ToArray(), keep.Select(i => Math.Round(evaluation.Forecast.MaxTemperature[i], 4)).ToArray());
        }

        // A. Discrete adjoint gradient on the full-order model.
        var sw = Stopwatch.StartNew();
        var adjoint = reference.Optimize(initial, source, 0, options, guess);
        var adjointMs = sw.Elapsed.TotalMilliseconds;
        outcomes.Add(Outcome("adjoint", "Adjoint gradient · full order", "Discrete adjoint", adjoint, adjointMs, reference.LastHistory,
            adjoint.Evaluations + adjoint.AdjointSolves));

        // B. Same algorithm, forward finite-difference gradient (K extra solves per gradient), sequential.
        var fdOptimizer = new AdjointCoolingOptimizer(fullOrder, load, cooling, new PdeOptimizerSettings(GradientMethod.FiniteDifference));
        sw.Restart();
        var fd = fdOptimizer.Optimize(initial, source, 0, options, guess);
        var fdMs = sw.Elapsed.TotalMilliseconds;
        outcomes.Add(Outcome("finite-difference", "Finite-difference gradient · full order", "Forward differences, ε = 10⁻⁴", fd, fdMs,
            fdOptimizer.LastHistory, fd.Evaluations));

        // C. Adjoint on the POD reduced model, certified on the full model.
        var pod = _factory.TrainPod(solver).Basis;
        var rom = new ReducedThermalModel(pod, Math.Min(_scenario.Control.RomModes, pod.Rank), solver);
        var romOptimizer = new ReducedOrderCoolingOptimizer(rom, solver, load, cooling, null, _scenario.Control.RomValidationThreshold);
        sw.Restart();
        var reduced = romOptimizer.Optimize(initial, source, 0, options, guess);
        var romMs = sw.Elapsed.TotalMilliseconds;
        var report = romOptimizer.LastReport!;
        outcomes.Add(Outcome("rom-adjoint", $"Adjoint gradient · POD ROM (r = {rom.Modes}), FOM-certified", "Discrete adjoint (reduced)", reduced, romMs,
            reduced.History.Select(h => new PdeOptimizationIteration(h.Iteration, h.Penalty, h.Objective, h.EnergyTerm, h.Violation * h.Violation,
                h.PeakTemperature, double.NaN, double.NaN, 0, 0, double.NaN)).ToArray(),
            report.FullOrderForwardSolves + report.FullOrderAdjointSolves));

        // D. The original optimiser: max-temperature penalty, parallel finite differences.
        var legacy = new CoolingOptimizer(new ThermalPredictor(solver, load));
        sw.Restart();
        var legacyResult = legacy.Optimize(initial, source, 0, options, guess);
        var legacyMs = sw.Elapsed.TotalMilliseconds;
        outcomes.Add(Outcome("legacy", $"Original optimiser (max-penalty, parallel FD on {Environment.ProcessorCount} threads)", "Forward differences, h = 10⁻³",
            legacyResult, legacyMs,
            legacyResult.History.Select(h => new PdeOptimizationIteration(h.Iteration, h.Penalty, h.Objective, h.EnergyTerm, h.Violation * h.Violation,
                h.PeakTemperature, double.NaN, double.NaN, 0, 0, double.NaN)).ToArray(),
            legacyResult.Evaluations));

        return new OptimizationBenchmarkResult(
            _scenario.Control.SafeTemperature,
            options.SafeTemperature,
            segments,
            segmentDuration,
            solver.TimeStep,
            fullOrder.StateDimension,
            penalties,
            options.SmoothnessWeight,
            cooling.RatedPower * segments * segmentDuration,
            outcomes,
            fdMs / adjointMs,
            (double)fd.Evaluations / (adjoint.Evaluations + adjoint.AdjointSolves));
    }

    // =====================================================================================
    // 10. Proper orthogonal decomposition: spectrum, FOM vs ROM, ROM-accelerated optimisation
    // =====================================================================================

    public ReducedOrderResult RunReducedOrderModel()
    {
        var solver = _factory.PredictionSolver();
        var fullOrder = new FullOrderThermalDynamics(solver);
        var load = _factory.LoadProfile();
        var grid = solver.Model.Grid;
        var initial = grid.CreateField(_scenario.Environment.InitialTemperature);
        const double segmentDuration = 100;
        var segments = Math.Max(1, (int)Math.Round(_scenario.Solver.Duration / segmentDuration));
        var steps = (int)Math.Round(segments * segmentDuration / solver.TimeStep);
        var sampleSteps = PodTrainer.GeometricSampleSteps(steps, 6);

        // Offline: two training families.
        var (hats, hatSnapshots, hatTrainMs, hatPodMs) = _factory.TrainPod(solver);
        var peakPower = _scenario.HeatSource.Hotspots.Count > 0 ? _scenario.HeatSource.Hotspots.Max(h => h.PeakPower) : 450_000;
        var radius = _scenario.HeatSource.Hotspots.Count > 0 ? _scenario.HeatSource.Hotspots[0].Radius : 0.012;
        var sw = Stopwatch.StartNew();
        var latticeCases = PodTrainer.DefectLatticeCases(grid, _scenario.HeatSource.UniformJouleHeating, peakPower, radius, segments * segmentDuration, segmentDuration);
        var latticeSnapshots = PodTrainer.CollectSnapshots(fullOrder, load, initial, latticeCases, sampleSteps);
        var latticeTrainMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var lattice = PodBasis.Compute(latticeSnapshots, 80);
        var latticePodMs = sw.Elapsed.TotalMilliseconds;

        var families = new (string Name, PodBasis Basis)[] { ("Source-basis family", hats), ("Defect-lattice family", lattice) };
        var training = new[]
        {
            new RomTrainingInfo("Source-basis family", _factory.SourceBasis(grid).Count + 1, hatSnapshots.Count, hats.Rank, Math.Round(hatTrainMs, 1), Math.Round(hatPodMs, 1), sampleSteps),
            new RomTrainingInfo("Defect-lattice family", latticeCases.Count, latticeSnapshots.Count, lattice.Rank, Math.Round(latticeTrainMs, 1), Math.Round(latticePodMs, 1), sampleSteps),
        };

        var spectrum = families
            .SelectMany(f => f.Basis.Eigenvalues.Take(100).Select((l, i) => new PodSpectrumPoint(f.Name, i + 1, l, f.Basis.CumulativeEnergy[i])))
            .ToArray();

        // Online: held-out test configurations (the true defect is not a training case).
        var trueSource = _factory.Plant().SourceOnModelGrid();
        var rng = new Random(2024);
        var tests = new (string Name, double[] Source, CoolingPlan Plan)[]
        {
            ("True defect · baseline cooling", trueSource, CoolingPlan.Constant(_scenario.Cooling.BaselineLevel, segments, segmentDuration)),
            ("True defect · random schedule", trueSource, new CoolingPlan(segmentDuration, Enumerable.Range(0, segments).Select(_ => rng.NextDouble()).ToArray())),
            ("Reconstructed source q̂ · ramp", IdentifiedSource(), new CoolingPlan(segmentDuration, Enumerable.Range(0, segments).Select(k => 0.6 * k / Math.Max(1, segments - 1)).ToArray())),
        };

        int[] modeCounts = [2, 5, 10, 15, 20, 30, 40, 60, 80];
        var comparison = new List<RomComparisonRow>();
        var fomTrajectories = new Dictionary<string, SimulatedTrajectory>();
        foreach (var test in tests)
        {
            var fomMs = Median(3, () => ThermalTrajectorySimulator.Simulate(fullOrder, load, initial, test.Source, 0, test.Plan));
            var fom = ThermalTrajectorySimulator.Simulate(fullOrder, load, initial, test.Source, 0, test.Plan);
            fomTrajectories[test.Name] = fom;
            foreach (var (family, basis) in families)
            {
                foreach (var r in modeCounts.Where(r => r <= basis.Rank))
                {
                    var model = new ReducedThermalModel(basis, r, solver);
                    var romMs = Median(3, () => ThermalTrajectorySimulator.Simulate(model, load, initial, test.Source, 0, test.Plan));
                    var red = ThermalTrajectorySimulator.Simulate(model, load, initial, test.Source, 0, test.Plan);
                    double se = 0, max = 0, projection = 0;
                    var count = 0;
                    for (var j = 0; j < fom.Fields.Length; j++)
                    {
                        for (var k = 0; k < fom.Fields[j].Length; k++)
                        {
                            var e = red.Fields[j][k] - fom.Fields[j][k];
                            se += e * e;
                            max = Math.Max(max, Math.Abs(e));
                            count++;
                        }

                        var p = basis.ProjectionError(fom.Fields[j], r);
                        projection += p * p;
                    }

                    comparison.Add(new RomComparisonRow(family, test.Name, r, basis.CumulativeEnergy[r - 1], Math.Sqrt(se / count), max,
                        Math.Sqrt(projection / count), Math.Abs(red.MaxTemperature.Max() - fom.MaxTemperature.Max()),
                        Math.Round(fomMs, 3), Math.Round(romMs, 3), fomMs / romMs, fullOrder.StateDimension));
                }
            }
        }

        const double peakTolerance = 0.1, rmseTolerance = 0.05;
        var hatRows = comparison.Where(c => c.Family == "Source-basis family").ToArray();
        var selected = modeCounts.Where(r => r <= hats.Rank)
            .FirstOrDefault(r => hatRows.Where(c => c.Modes == r).All(c => c.PeakError <= peakTolerance && c.Rmse <= rmseTolerance));
        if (selected == 0)
        {
            selected = Math.Min(modeCounts[^1], hats.Rank);
        }

        var rule = $"smallest r with peak-temperature error ≤ {peakTolerance} K and field RMSE ≤ {rmseTolerance} K on every held-out test";

        var modes = hats.Modes.Take(6).Select((m, i) => FieldDto.Create($"mode{i + 1}", $"POD mode φ{i + 1} (λ = {hats.Eigenvalues[i]:0.0e+0} K²)", "–", grid, m, 1, 5)).ToArray();

        var baselineTest = tests[0];
        var fomBaseline = fomTrajectories[baselineTest.Name];
        var selectedModel = new ReducedThermalModel(hats, selected, solver);
        var romBaseline = ThermalTrajectorySimulator.Simulate(selectedModel, load, initial, baselineTest.Source, 0, baselineTest.Plan);
        const int coarse = 10;
        var romCoarse = ThermalTrajectorySimulator.Simulate(new ReducedThermalModel(hats, Math.Min(coarse, hats.Rank), solver), load, initial, baselineTest.Source, 0, baselineTest.Plan);
        var at = Math.Min((int)Math.Round(Math.Min(1200, _scenario.Solver.Duration) / solver.TimeStep), fomBaseline.Fields.Length - 1);
        var fields = new Dictionary<string, FieldDto>
        {
            ["fom"] = FieldDto.Create("fom", $"Full-order model (n = {fullOrder.StateDimension}) · t = {fomBaseline.Times[at]:0} s", "°C", grid, fomBaseline.Fields[at]),
            ["rom"] = FieldDto.Create("rom", $"POD ROM (r = {selected}) · t = {fomBaseline.Times[at]:0} s", "°C", grid, romBaseline.Fields[at]),
            ["error"] = FieldDto.Create("error", "T_ROM − T_FOM", "K", grid, ErrorMetrics.Difference(romBaseline.Fields[at], fomBaseline.Fields[at]), 1, 5),
            ["mean"] = FieldDto.Create("mean", "Reference state T̄ (snapshot mean)", "°C", grid, hats.Mean),
        };
        var every = Enumerable.Range(0, fomBaseline.Times.Length).Where(i => i % 3 == 0).ToArray();
        var series = new RomTimeSeries(
            every.Select(i => fomBaseline.Times[i]).ToArray(),
            every.Select(i => Math.Round(fomBaseline.MaxTemperature[i], 4)).ToArray(),
            every.Select(i => Math.Round(romBaseline.MaxTemperature[i], 4)).ToArray(),
            every.Select(i => Math.Round(romCoarse.MaxTemperature[i], 4)).ToArray(),
            coarse);

        var optimization = RomOptimizationStudy(solver, hats, segments, segmentDuration, selected);
        return new ReducedOrderResult(fullOrder.StateDimension, solver.TimeStep, training, spectrum, comparison, selected, rule, modes, fields, series, optimization);
    }

    private List<RomOptimizationRow> RomOptimizationStudy(Numerics.Pde.HeatEquationSolver solver, PodBasis pod, int segments, double segmentDuration, int selected)
    {
        var options = _factory.OptimizationOptions(segments, segmentDuration) with { MaxIterationsPerStage = 40 };
        var load = _factory.LoadProfile();
        var cooling = _factory.Cooling();
        var source = IdentifiedSource();
        var initial = solver.Model.Grid.CreateField(_scenario.Environment.InitialTemperature);
        var guess = Enumerable.Repeat(0.3, segments).ToArray();

        var sw = Stopwatch.StartNew();
        var full = new AdjointCoolingOptimizer(new FullOrderThermalDynamics(solver), load, cooling).Optimize(initial, source, 0, options, guess);
        var fullMs = sw.Elapsed.TotalMilliseconds;
        var rows = new List<RomOptimizationRow>
        {
            new("Full-order FVM (adjoint)", solver.Size, Math.Round(fullMs, 1), 1, full.Objective, 0, Math.Round(full.CoolingEnergy, 2),
                Math.Round(full.PeakTemperature, 4), full.Feasible, solver.Size, 0, 0, false, false, null, 0, full.Evaluations + full.AdjointSolves, 0),
        };

        foreach (var r in new[] { 20, 30, selected, 60 }.Distinct().Where(r => r <= pod.Rank).Order())
        {
            var optimizer = new ReducedOrderCoolingOptimizer(new ReducedThermalModel(pod, r, solver), solver, load, cooling, null,
                _scenario.Control.RomValidationThreshold);
            sw.Restart();
            var result = optimizer.Optimize(initial, source, 0, options, guess);
            var ms = sw.Elapsed.TotalMilliseconds;
            var report = optimizer.LastReport!;
            rows.Add(new RomOptimizationRow($"POD ROM r = {r}", r, Math.Round(ms, 1), fullMs / ms, result.Objective, (result.Objective - full.Objective) / full.Objective,
                Math.Round(result.CoolingEnergy, 2), Math.Round(result.PeakTemperature, 4), result.Feasible, report.ScreenedCells, report.CorrectionRounds,
                Math.Round(report.ConstraintShift, 4), report.Repaired, report.FellBack, report.FallbackReason,
                report.RomForwardSolves + report.RomAdjointSolves, report.FullOrderForwardSolves + report.FullOrderAdjointSolves, Math.Round(report.ValidationError, 4)));
        }

        return rows;
    }

    // =====================================================================================
    // 11. Closed-loop MPC on the plant: legacy vs full-order adjoint vs ROM adjoint
    // =====================================================================================

    public ReducedOrderControlResult RunReducedOrderControl()
    {
        var safe = _scenario.Control.SafeTemperature;
        var dt = _scenario.Solver.TimeStep;
        var runs = new List<MpcRunOutcome>();
        foreach (var (key, name, kind) in new[]
                 {
                     ("legacy", "Original MPC (max-penalty, finite differences)", CoolingOptimizerKind.PenaltyFiniteDifference),
                     ("adjoint", "Adjoint MPC · full-order model", CoolingOptimizerKind.AdjointFullOrder),
                     ("rom", $"Adjoint MPC · POD ROM (r = {_scenario.Control.RomModes}), FOM-certified", CoolingOptimizerKind.AdjointReducedOrder),
                 })
        {
            var scenario = _scenario with
            {
                Cooling = _scenario.Cooling with { Mode = CoolingMode.Autonomous },
                Control = _scenario.Control with { Optimizer = kind },
            };
            var engine = new DigitalTwinEngine(scenario, Guid.Empty);
            while (!engine.IsComplete)
            {
                engine.Advance();
            }

            var history = engine.History;
            var excess = history.Select(h => Math.Max(0, h.TrueMax - safe)).ToArray();
            var keep = Enumerable.Range(0, history.Count).Where(i => i % 2 == 0).ToArray();
            var s = engine.Statistics;
            runs.Add(new MpcRunOutcome(key, name, engine.OptimizerName, Math.Round(engine.PeakTemperature, 4), Math.Round(engine.CoolingEnergy, 1),
                excess.Count(e => e > 0) * dt, Math.Round(excess.Max(), 4), excess.Max() <= 1e-9, s.Optimizations, Math.Round(s.TotalMs, 1),
                Math.Round(s.TotalMs / Math.Max(1, s.Optimizations), 1), s.ForwardSolves, s.AdjointSolves, s.FullOrderSolves, s.Fallbacks,
                s.CorrectionRounds, s.Repairs, Math.Round(s.MeanValidationError, 4), Math.Round(s.MaxValidationError, 4),
                keep.Select(i => history[i].Time).ToArray(), keep.Select(i => history[i].TrueMax).ToArray(),
                keep.Select(i => history[i].CoolingLevel).ToArray()));
        }

        return new ReducedOrderControlResult(safe, _scenario.Control.RomModes, _scenario.Control.RomValidationThreshold, runs);
    }

    // =====================================================================================
    // 12. Parameter sensitivity, identifiability and bounded estimation
    // =====================================================================================

    public IdentifiabilityResult RunParameterIdentifiability()
    {
        var g = _scenario.Geometry;
        var spot = _scenario.HeatSource.Hotspots.Count > 0
            ? _scenario.HeatSource.Hotspots[0]
            : new HotspotSettings(0.5 * g.LengthX, 0.5 * g.LengthY, 450_000, 0.012);
        var k0 = _scenario.Material.Conductivity;
        var h0 = Math.Max(_scenario.Environment.EdgeHeatTransferCoefficient, 1);
        ParameterSpec[] specs =
        [
            new(ThermalParameter.Conductivity, "k", "W/(m·K)", k0, 0.2 * k0, 0.25 * k0, 3 * k0, 1e-3 * k0),
            new(ThermalParameter.EdgeHeatTransfer, "h_e", "W/(m²·K)", h0, 0.5 * h0, 0.05 * h0, 5 * h0, 1e-3 * h0),
            new(ThermalParameter.DefectPower, "Q", "W/m³", spot.PeakPower, spot.PeakPower / 3, 0, 5 * spot.PeakPower, 1e-3 * spot.PeakPower),
            new(ThermalParameter.DefectX, "x₀", "m", spot.X, 0.01, 0.02 * g.LengthX, 0.98 * g.LengthX, 1e-4),
            new(ThermalParameter.DefectY, "y₀", "m", spot.Y, 0.01, 0.02 * g.LengthY, 0.98 * g.LengthY, 1e-4),
        ];

        var sensors = _factory.Sensors();
        var model = new SensorResponseModel(_factory.ThermalModel(), sensors, _factory.LoadProfile(), _scenario.HeatSource.UniformJouleHeating,
            spot.Radius, _scenario.Cooling.BaselineLevel, _scenario.Environment.InitialTemperature, _scenario.Environment.AmbientTemperature,
            _scenario.Solver.Duration, _scenario.Control.PredictionTimeStep, 30);
        var noise = Math.Max(_scenario.Sensors.NoiseStd, 1e-3);
        var report = IdentifiabilityAnalysis.Analyze(model, specs, noise);

        // Sensitivity time series at the thermistor closest to the defect.
        var nearest = Enumerable.Range(0, sensors.Count).MinBy(i => Math.Pow(sensors[i].X - spot.X, 2) + Math.Pow(sensors[i].Y - spot.Y, 2));
        var traces = specs.Select((s, j) => new SensitivityTrace(s.Symbol,
            Enumerable.Range(0, model.SampleTimes.Length).Select(t => Math.Round(report.ScaledSensitivity[t * sensors.Count + nearest][j], 5)).ToArray())).ToArray();

        // Synthetic data from the refined plant grid (no inverse crime) with seeded sensor noise.
        var truth = specs.ToDictionary(s => s.Parameter, s => s.Nominal);
        var fine = new Grid2D(g.Nx * Math.Max(1, g.PlantRefinement), g.Ny * Math.Max(1, g.PlantRefinement), g.LengthX, g.LengthY);
        var data = model.Simulate(truth, fine, _scenario.Sensors.NoiseStd, _scenario.Sensors.Seed + 4242);
        var start = new Dictionary<ThermalParameter, double>
        {
            [ThermalParameter.Conductivity] = 0.8 * k0,
            [ThermalParameter.EdgeHeatTransfer] = 1.5 * h0,
            [ThermalParameter.DefectPower] = 2.0 / 3.0 * spot.PeakPower,
            [ThermalParameter.DefectX] = Math.Clamp(spot.X - 0.018, specs[3].Lower, specs[3].Upper),
            [ThermalParameter.DefectY] = Math.Clamp(spot.Y - 0.014, specs[4].Lower, specs[4].Upper),
        };
        var knownParameters = new Dictionary<ThermalParameter, double>(start)
        {
            [ThermalParameter.Conductivity] = k0,
            [ThermalParameter.EdgeHeatTransfer] = h0,
        };
        ThermalParameter[] all = [.. specs.Select(s => s.Parameter)];
        ThermalParameter[] defect = [ThermalParameter.DefectPower, ThermalParameter.DefectX, ThermalParameter.DefectY];
        var cases = new[]
        {
            new EstimationCase("joint", "Joint estimation of all five parameters",
                "k, h_e, Q, x₀, y₀ estimated together from a perturbed start (−20 % k, +50 % h_e, −33 % Q, −18/−14 mm).",
                BoundedLevenbergMarquardt.Estimate(model, specs, all, data, start, truth)),
            new EstimationCase("defect-known", "Defect only · k and h_e known exactly",
                "Q, x₀, y₀ estimated; k and h_e fixed at their true values.",
                BoundedLevenbergMarquardt.Estimate(model, specs, defect, data, knownParameters, truth)),
            new EstimationCase("defect-misspecified", "Defect only · k and h_e mis-specified",
                "Q, x₀, y₀ estimated; k and h_e fixed at the wrong prior values (−20 % k, +50 % h_e) — shows how parameter uncertainty biases the defect estimate.",
                BoundedLevenbergMarquardt.Estimate(model, specs, defect, data, start, truth)),
        };

        return new IdentifiabilityResult(SensitivityReportDto.From(report), sensors[nearest].Id, model.SampleTimes, traces, cases, _scenario.Sensors.NoiseStd,
            $"{fine.Nx}×{fine.Ny} plant grid, σ = {_scenario.Sensors.NoiseStd} K, {sensors.Count} sensors every 30 s");
    }

    // =====================================================================================

    /// <summary>
    /// Heat source identified by the inverse estimator from a <i>previous</i> charge cycle with baseline cooling
    /// (GCV) — the information an operator would actually have before planning the next charge.
    /// </summary>
    private double[] IdentifiedSource()
    {
        if (_identifiedSource is not null)
        {
            return _identifiedSource;
        }

        var plant = _factory.Plant(seedOffset: 500);
        var estimator = _factory.Estimator();
        var total = (int)Math.Round(_scenario.Solver.Duration / _scenario.Solver.TimeStep);
        for (var n = 0; n < total; n++)
        {
            estimator.Assimilate(_scenario.Cooling.BaselineLevel, plant.Step(_scenario.Cooling.BaselineLevel));
        }

        _identifiedSource = estimator.SolveAuto(_scenario.Estimator.LambdaSelection, _scenario.Estimator.Regularization,
            _scenario.Sensors.NoiseStd, _scenario.Estimator.FixedLambda).SourceField;
        return _identifiedSource;
    }

    /// <summary>Runs the ground-truth plant under a cooling policy u(t) and records max temperature (model grid).</summary>
    private (double[] Times, double[] MaxTemperature, double Energy) PlantRun(Func<double, double> policy)
    {
        var plant = _factory.Plant();
        var cooling = _factory.Cooling();
        var total = (int)Math.Round(_scenario.Solver.Duration / _scenario.Solver.TimeStep);
        var times = new double[total];
        var maxT = new double[total];
        var energy = 0.0;
        for (var n = 0; n < total; n++)
        {
            var u = policy(plant.Time);
            plant.Step(u);
            energy += cooling.Power(u) * _scenario.Solver.TimeStep;
            times[n] = plant.Time;
            maxT[n] = Vector.Max(plant.TemperatureOnModelGrid());
        }

        return (times, maxT, energy);
    }

    private static double Median(int repeats, Action action)
    {
        var times = new double[repeats];
        for (var i = 0; i < repeats; i++)
        {
            var sw = Stopwatch.StartNew();
            action();
            times[i] = sw.Elapsed.TotalMilliseconds;
        }

        Array.Sort(times);
        return times[repeats / 2];
    }
}
