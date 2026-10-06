using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Numerics.ReducedOrder;

/// <summary>Temperature fields of one simulated trajectory at the sampled times.</summary>
public sealed record SimulatedTrajectory(double[] Times, double[][] Fields, double[] MaxTemperature, double ElapsedMs);

/// <summary>One high-fidelity training (or test) run: a heat-source shape and a cooling schedule.</summary>
public sealed record TrainingCase(string Name, double[] SourceShape, CoolingPlan Plan);

/// <summary>Integrates any <see cref="IThermalDynamics"/> (full or reduced) and reconstructs the temperature field.</summary>
public static class ThermalTrajectorySimulator
{
    /// <param name="sampleEvery">Store every k-th field (always including t₀ and the final time).</param>
    public static SimulatedTrajectory Simulate(
        IThermalDynamics dynamics,
        LoadProfile load,
        ReadOnlySpan<double> initialField,
        ReadOnlySpan<double> sourceShape,
        double startTime,
        CoolingPlan plan,
        int sampleEvery = 1)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var dt = dynamics.TimeStep;
        var steps = Math.Max(1, (int)Math.Round(plan.Horizon / dt));
        var r = dynamics.StateDimension;
        var theta = dynamics.Theta;
        var c = dynamics.ConstantForcing.ToArray();
        var d = dynamics.CoolantForcing.ToArray();
        var p = dynamics.ProjectSource(sourceShape);
        var x = dynamics.Encode(initialField);
        var next = new double[r];
        var field = new double[dynamics.FieldDimension];
        var times = new List<double>();
        var fields = new List<double[]>();
        var maxT = new double[steps + 1];

        dynamics.Lift(x, field);
        times.Add(startTime);
        fields.Add((double[])field.Clone());
        maxT[0] = field.Max();

        for (var n = 0; n < steps; n++)
        {
            var u = plan.LevelAt(n * dt);
            var t = startTime + n * dt;
            var sNow = load.At(t);
            var sNext = load.At(t + dt);
            var sigma = dynamics.SinkRate(u);
            dynamics.ApplyExplicit(x, next, u);
            for (var i = 0; i < r; i++)
            {
                next[i] += dt * (theta * (c[i] + sNext * p[i] + sigma * d[i]) + (1 - theta) * (c[i] + sNow * p[i] + sigma * d[i]));
            }

            dynamics.SolveImplicit(next, u);
            (x, next) = (next, x);
            dynamics.Lift(x, field);
            maxT[n + 1] = field.Max();
            if ((n + 1) % sampleEvery == 0 || n + 1 == steps)
            {
                times.Add(t + dt);
                fields.Add((double[])field.Clone());
            }
        }

        return new SimulatedTrajectory([.. times], [.. fields], maxT, sw.Elapsed.TotalMilliseconds);
    }
}

/// <summary>
/// Offline stage of the reduced-order model: run representative high-fidelity simulations, collect
/// snapshots, compute the POD basis. The training set should excite the dynamics the ROM will be asked to
/// reproduce online (here: a family of hotspot locations × cooling schedules), but must not contain the
/// test configuration itself.
/// </summary>
public static class PodTrainer
{
    /// <param name="sampleSteps">Time-step indices stored as snapshots (t₀ = step 0 is stored once for the whole set).</param>
    public static SnapshotSet CollectSnapshots(
        IThermalDynamics fullOrder,
        LoadProfile load,
        ReadOnlySpan<double> initialField,
        IEnumerable<TrainingCase> cases,
        IReadOnlyCollection<int> sampleSteps)
    {
        var snapshots = new SnapshotSet(fullOrder.FieldDimension);
        var initial = initialField.ToArray();
        var trajectories = cases.AsParallel().AsOrdered()
            .Select(c => ThermalTrajectorySimulator.Simulate(fullOrder, load, initial, c.SourceShape, 0, c.Plan))
            .ToArray();
        var first = true;
        foreach (var trajectory in trajectories)
        {
            for (var n = 0; n < trajectory.Fields.Length; n++)
            {
                if (sampleSteps.Contains(n) && (n > 0 || first))
                {
                    snapshots.Add(trajectory.Fields[n]);
                }
            }

            first = false;
        }

        return snapshots;
    }

    /// <summary>Roughly geometric sample times (in steps) — early times resolve the short diffusion scales.</summary>
    public static int[] GeometricSampleSteps(int steps, int count)
    {
        var set = new SortedSet<int> { 0, steps };
        for (var i = 0; i < count; i++)
        {
            set.Add(Math.Clamp((int)Math.Round(Math.Pow(steps, (i + 1.0) / count)), 1, steps));
        }

        return [.. set];
    }

    /// <summary>
    /// Source-basis training family: uniform Joule heating alone, plus Joule heating with a localised source
    /// of the given amplitude on each hat function φ_j of the inverse problem's <see cref="Inverse.SourceBasis"/>.
    /// Because the state is linear in q, the trajectories span (approximately) every response the twin can
    /// produce from a reconstructed source q̂ = Σ q_j φ_j — the n-width of a moving defect is covered by the
    /// basis instead of by a few sampled defect locations.
    /// </summary>
    public static IReadOnlyList<TrainingCase> SourceBasisCases(Inverse.SourceBasis basis, double jouleHeating, double amplitude, CoolingPlan plan)
    {
        var grid = basis.Grid;
        var cases = new List<TrainingCase> { new("joule only", grid.CreateField(jouleHeating), plan) };
        for (var j = 0; j < basis.Count; j++)
        {
            var shape = basis.BasisField(j, amplitude);
            for (var k = 0; k < shape.Length; k++)
            {
                shape[k] += jouleHeating;
            }

            var (x, y) = basis.NodePosition(j);
            cases.Add(new TrainingCase($"hat φ{j} at ({x * 1000:0}, {y * 1000:0}) mm", shape, plan));
        }

        return cases;
    }

    /// <summary>
    /// Defect-lattice training family (the naive choice, kept for comparison) for a rectangular cell: uniform Joule heating plus a Gaussian defect at each
    /// point of a coarse lattice of candidate locations (and the defect-free cell), under constant and
    /// time-varying cooling schedules.
    /// </summary>
    public static IReadOnlyList<TrainingCase> DefectLatticeCases(
        Grid.Grid2D grid,
        double jouleHeating,
        double defectPower,
        double defectRadius,
        double horizon,
        double segmentDuration)
    {
        var segments = Math.Max(1, (int)Math.Round(horizon / segmentDuration));
        var plans = new (string Name, double[] Levels)[]
        {
            ("u=0", Enumerable.Repeat(0.0, segments).ToArray()),
            ("u=0.3", Enumerable.Repeat(0.3, segments).ToArray()),
            ("u=1", Enumerable.Repeat(1.0, segments).ToArray()),
            ("ramp", Enumerable.Range(0, segments).Select(k => (double)k / Math.Max(1, segments - 1)).ToArray()),
        };

        var sources = new List<(string Name, double[] Shape)> { ("joule only", grid.CreateField(jouleHeating)) };
        foreach (var fx in new[] { 0.2, 0.5, 0.8 })
        {
            foreach (var fy in new[] { 0.3, 0.7 })
            {
                var spot = new GaussianHotspot(fx * grid.LengthX, fy * grid.LengthY, defectPower, defectRadius);
                sources.Add(($"defect ({fx:0.0}Lx, {fy:0.0}Ly)", grid.CreateField((x, y) => jouleHeating + spot.Evaluate(x, y))));
            }
        }

        return sources
            .SelectMany(s => plans.Select(p => new TrainingCase($"{s.Name}, {p.Name}", s.Shape, new CoolingPlan(segmentDuration, p.Levels))))
            .ToArray();
    }
}
