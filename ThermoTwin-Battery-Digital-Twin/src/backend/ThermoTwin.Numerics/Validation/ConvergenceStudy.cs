using System.Diagnostics;
using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Validation;

public sealed record ConvergenceRow(
    TimeScheme Scheme,
    int Nx,
    int Ny,
    double Dx,
    double TimeStep,
    int Steps,
    double Rmse,
    double MaxError,
    double RuntimeMs,
    double? ObservedOrderRmse,
    double? ObservedOrderMax);

public sealed record StabilityProbe(TimeScheme Scheme, double TimeStepRatio, double TimeStep, int Steps, double FinalAmplitude, bool Diverged);

public sealed record EnergyConservationResult(double InjectedEnergy, double StoredEnergyChange, double RelativeImbalance);

/// <summary>
/// Verification of the discretisation against an exact solution.
/// <para>
/// Test problem (homogeneous Dirichlet T = T_b on all edges, no source, no cooling):
/// <code>
///   T(x, y, t) = T_b + A·exp(−απ²(1/Lx² + 1/Ly²) t)·sin(πx/Lx)·sin(πy/Ly)
/// </code>
/// The initial mode sin(πx/Lx)sin(πy/Ly) sampled at cell centres is an exact eigenvector of the
/// discrete Laplacian with ghost-cell Dirichlet closure, with eigenvalue
/// μ_h = α[(2cos(πΔx/Lx) − 2)/Δx² + (2cos(πΔy/Ly) − 2)/Δy²]. This gives two exact references:
/// the PDE solution (spatial + temporal error) and the semi-discrete solution e^{μ_h t}v (temporal error only).
/// </para>
/// </summary>
public static class ConvergenceStudy
{
    public static ThermalModel TestModel(Grid2D grid, double boundaryTemperature) => new(
        grid,
        MaterialProperties.LithiumIonPouchCell,
        BoundaryConditions.All(BoundaryCondition.FixedTemperature(boundaryTemperature)),
        new CoolingModel(boundaryTemperature, 0, 0, 0));

    /// <summary>
    /// Spatial refinement study: h halves while Δt = r·h²/α shrinks with h² so that every scheme's
    /// time error is O(h²) as well — the observed order should approach 2.
    /// </summary>
    public static IReadOnlyList<ConvergenceRow> SpatialStudy(
        TimeScheme scheme,
        IReadOnlyList<(int Nx, int Ny)> grids,
        double lengthX = 0.2,
        double lengthY = 0.1,
        double finalTime = 100,
        double fourierRatio = 0.2)
    {
        const double tb = 25, amplitude = 20;
        var rows = new List<ConvergenceRow>();
        foreach (var (nx, ny) in grids)
        {
            var grid = new Grid2D(nx, ny, lengthX, lengthY);
            var model = TestModel(grid, tb);
            var alpha = model.Material.Diffusivity;
            var dtTarget = fourierRatio * grid.Dx * grid.Dx / alpha;
            var steps = (int)Math.Ceiling(finalTime / dtTarget);
            var dt = finalTime / steps;
            var solver = new HeatEquationSolver(model, scheme, dt);

            var u = grid.CreateField((x, y) => tb + amplitude * Math.Sin(Math.PI * x / lengthX) * Math.Sin(Math.PI * y / lengthY));
            var next = new double[u.Length];
            var sw = Stopwatch.StartNew();
            for (var s = 0; s < steps; s++)
            {
                solver.Step(u, next, [], [], 0);
                (u, next) = (next, u);
            }

            sw.Stop();
            var decay = Math.Exp(-alpha * Math.PI * Math.PI * (1 / (lengthX * lengthX) + 1 / (lengthY * lengthY)) * finalTime);
            var exact = grid.CreateField((x, y) => tb + amplitude * decay * Math.Sin(Math.PI * x / lengthX) * Math.Sin(Math.PI * y / lengthY));
            var rmse = ErrorMetrics.Rmse(u, exact);
            var max = ErrorMetrics.MaxError(u, exact);
            double? pr = null, pm = null;
            if (rows.Count > 0)
            {
                var prev = rows[^1];
                var ratio = prev.Dx / grid.Dx;
                pr = ErrorMetrics.ObservedOrder(prev.Rmse, rmse, ratio);
                pm = ErrorMetrics.ObservedOrder(prev.MaxError, max, ratio);
            }

            rows.Add(new ConvergenceRow(scheme, nx, ny, grid.Dx, dt, steps, rmse, max, sw.Elapsed.TotalMilliseconds, pr, pm));
        }

        return rows;
    }

    /// <summary>
    /// Temporal refinement study on a fixed grid against the exact semi-discrete solution,
    /// isolating the time-integration error: Euler schemes → order 1, Crank–Nicolson → order 2.
    /// </summary>
    public static IReadOnlyList<ConvergenceRow> TemporalStudy(
        TimeScheme scheme,
        IReadOnlyList<double> timeSteps,
        int nx = 40,
        int ny = 20,
        double lengthX = 0.2,
        double lengthY = 0.1,
        double finalTime = 200)
    {
        const double tb = 25, amplitude = 20;
        var grid = new Grid2D(nx, ny, lengthX, lengthY);
        var model = TestModel(grid, tb);
        var alpha = model.Material.Diffusivity;
        var mu = alpha * ((2 * Math.Cos(Math.PI * grid.Dx / lengthX) - 2) / (grid.Dx * grid.Dx)
                          + (2 * Math.Cos(Math.PI * grid.Dy / lengthY) - 2) / (grid.Dy * grid.Dy));
        var exact = grid.CreateField((x, y) => tb + amplitude * Math.Exp(mu * finalTime) * Math.Sin(Math.PI * x / lengthX) * Math.Sin(Math.PI * y / lengthY));

        var rows = new List<ConvergenceRow>();
        foreach (var dtTarget in timeSteps)
        {
            var steps = (int)Math.Round(finalTime / dtTarget);
            var dt = finalTime / steps;
            var solver = new HeatEquationSolver(model, scheme, dt);
            var u = grid.CreateField((x, y) => tb + amplitude * Math.Sin(Math.PI * x / lengthX) * Math.Sin(Math.PI * y / lengthY));
            var next = new double[u.Length];
            var sw = Stopwatch.StartNew();
            for (var s = 0; s < steps; s++)
            {
                solver.Step(u, next, [], [], 0);
                (u, next) = (next, u);
            }

            sw.Stop();
            var rmse = ErrorMetrics.Rmse(u, exact);
            var max = ErrorMetrics.MaxError(u, exact);
            double? pr = null, pm = null;
            if (rows.Count > 0)
            {
                var prev = rows[^1];
                var ratio = prev.TimeStep / dt;
                pr = ErrorMetrics.ObservedOrder(prev.Rmse, rmse, ratio);
                pm = ErrorMetrics.ObservedOrder(prev.MaxError, max, ratio);
            }

            rows.Add(new ConvergenceRow(scheme, nx, ny, grid.Dx, dt, steps, rmse, max, sw.Elapsed.TotalMilliseconds, pr, pm));
        }

        return rows;
    }

    /// <summary>
    /// Runs explicit Euler just below and above its stability limit from a rough initial field
    /// and reports whether the high-frequency content grows without bound.
    /// </summary>
    public static IReadOnlyList<StabilityProbe> ExplicitStabilityProbe(IReadOnlyList<double> ratios, int nx = 40, int ny = 20, int steps = 400)
    {
        var grid = new Grid2D(nx, ny, 0.2, 0.1);
        var model = TestModel(grid, 0);
        var reference = new HeatEquationSolver(model, TimeScheme.ExplicitEuler, 1);
        var limit = StabilityAnalyzer.Analyze(reference).ExplicitCriticalTimeStep;
        var probes = new List<StabilityProbe>();
        foreach (var ratio in ratios)
        {
            var solver = new HeatEquationSolver(model, TimeScheme.ExplicitEuler, ratio * limit);
            var rng = new Random(11);
            var u = grid.CreateField(0);
            for (var k = 0; k < u.Length; k++)
            {
                u[k] = rng.NextDouble() - 0.5;
            }

            var next = new double[u.Length];
            for (var s = 0; s < steps; s++)
            {
                solver.Step(u, next, [], [], 0);
                (u, next) = (next, u);
            }

            var amplitude = Vector.NormInf(u);
            probes.Add(new StabilityProbe(TimeScheme.ExplicitEuler, ratio, solver.TimeStep, steps, amplitude, double.IsNaN(amplitude) || amplitude > 1));
        }

        return probes;
    }

    /// <summary>
    /// Discrete energy balance on an insulated cell with uniform heating: the finite-volume scheme
    /// is conservative, so ∫ρcₚ ΔT dV must equal ∫∫ q dV dt to round-off.
    /// </summary>
    public static EnergyConservationResult EnergyConservation(TimeScheme scheme, double power = 5e4, double duration = 600, double dt = 5)
    {
        var grid = new Grid2D(40, 20, 0.2, 0.1);
        var model = new ThermalModel(grid, MaterialProperties.LithiumIonPouchCell,
            BoundaryConditions.All(BoundaryCondition.Insulated), new CoolingModel(25, 0, 0, 0));
        var solver = new HeatEquationSolver(model, scheme, scheme == TimeScheme.ExplicitEuler ? 0.5 : dt);
        var source = grid.CreateField((x, y) => power * (1 + Math.Sin(Math.PI * x / 0.2)));
        var u = grid.CreateField(25);
        var next = new double[u.Length];
        var e0 = solver.ThermalEnergy(u);
        var steps = (int)Math.Round(duration / solver.TimeStep);
        for (var s = 0; s < steps; s++)
        {
            solver.Step(u, next, source, source, 0);
            (u, next) = (next, u);
        }

        var injected = source.Sum() * grid.CellArea * model.Material.Thickness * steps * solver.TimeStep;
        var stored = solver.ThermalEnergy(u) - e0;
        return new EnergyConservationResult(injected, stored, Math.Abs(stored - injected) / injected);
    }
}
