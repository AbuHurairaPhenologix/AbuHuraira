using System.Diagnostics;
using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Fem;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Numerics.Validation;

/// <summary>One refinement level of one discretisation against an exact solution.</summary>
/// <param name="Method">"FVM" (cell-centred finite volumes) or "FEM" (P1 Galerkin).</param>
/// <param name="Problem">Verification problem key.</param>
/// <param name="H">Rectangle size Δx [m] (identical for both methods at a given level).</param>
/// <param name="Dofs">Degrees of freedom: cells (FVM) or nodes (FEM).</param>
/// <param name="NonZeros">Non-zeros of the implicit system matrix.</param>
/// <param name="Rmse">Discrete RMS error at the method's own DOF locations (cell centres / nodes) [K].</param>
/// <param name="MaxError">Maximum DOF error [K].</param>
/// <param name="L2Error">FEM only: ‖T − T_h‖_{L²}/√|Ω| (RMS-normalised continuous L² error) [K].</param>
/// <param name="H1Error">FEM only: |T − T_h|_{H¹}/√|Ω| [K/m].</param>
public sealed record MethodConvergenceRow(
    string Method,
    string Problem,
    int Nx,
    int Ny,
    double H,
    int Dofs,
    int NonZeros,
    int HalfBandwidth,
    double TimeStep,
    int Steps,
    double Rmse,
    double MaxError,
    double? L2Error,
    double? H1Error,
    double? OrderRmse,
    double? OrderMax,
    double? OrderL2,
    double? OrderH1,
    double RuntimeMs);

/// <summary>FVM vs FEM on the realistic battery problem (no exact solution) at one resolution.</summary>
public sealed record BatteryComparisonRow(
    int Nx,
    int Ny,
    double H,
    int FvmDofs,
    int FemDofs,
    int FvmNonZeros,
    int FemNonZeros,
    double FvmPeak,
    double FemPeak,
    double FvmFinalMax,
    double FemFinalMax,
    double FvmFinalMean,
    double FemFinalMean,
    double FieldRmsDifference,
    double FieldMaxDifference,
    double FvmRuntimeMs,
    double FemRuntimeMs,
    double FvmEnergyJoules,
    double FemEnergyJoules);

/// <summary>Fields of the FVM–FEM battery comparison at the display resolution (all on the FVM cell centres, except nodal FEM).</summary>
public sealed record BatteryComparisonFields(Grid2D Grid, FemMesh2D Mesh, double[] Fvm, double[] FemNodal, double[] FemAtCellCentres, double[] Difference, double Time);

/// <summary>
/// Verification of the finite-element solver and cross-validation of the two independent discretisations.
/// <list type="bullet">
/// <item><b>Problem A</b> — Dirichlet eigenmode T = T_b + A e^{−λt} sin(πx/Lx) sin(πy/Ly), q = 0 (as in <see cref="ConvergenceStudy"/>).</item>
/// <item><b>Problem B</b> — manufactured solution with every term of the battery model active: insulated edges,
///   cold-plate sink H(T − T_c) and a space–time varying source chosen so that
///   T = T_c + β + γ(t) cos(πx/Lx) cos(2πy/Ly), γ(t) = A(1 − e^{−t/τ}) solves the PDE exactly:
///   q = ρcₚγ′φ + (kκ² + H)γφ + Hβ, κ² = (π/Lx)² + (2π/Ly)².</item>
/// </list>
/// Crank–Nicolson with Δt ∝ h keeps the O(Δt²) temporal error at the order of the O(h²) spatial error,
/// so the observed orders measure the spatial discretisation.
/// </summary>
public static class FemVerificationStudy
{
    public const string DirichletEigenmode = "Dirichlet eigenmode";
    public const string ManufacturedCooling = "Manufactured (sink + source, insulated)";

    private const double Lx = 0.2;
    private const double Ly = 0.1;

    public static IReadOnlyList<MethodConvergenceRow> Run(string problem, IReadOnlyList<(int Nx, int Ny)> grids, double coarseTimeStep = 4.0)
    {
        var rows = new List<MethodConvergenceRow>();
        foreach (var method in new[] { "FVM", "FEM" })
        {
            var methodRows = new List<MethodConvergenceRow>();
            var dt = coarseTimeStep;
            foreach (var (nx, ny) in grids)
            {
                var row = method == "FVM" ? RunFvm(problem, nx, ny, dt) : RunFem(problem, nx, ny, dt);
                if (methodRows.Count > 0)
                {
                    var prev = methodRows[^1];
                    var ratio = prev.H / row.H;
                    row = row with
                    {
                        OrderRmse = ErrorMetrics.ObservedOrder(prev.Rmse, row.Rmse, ratio),
                        OrderMax = ErrorMetrics.ObservedOrder(prev.MaxError, row.MaxError, ratio),
                        OrderL2 = row.L2Error is { } l2 && prev.L2Error is { } pl2 ? ErrorMetrics.ObservedOrder(pl2, l2, ratio) : null,
                        OrderH1 = row.H1Error is { } h1 && prev.H1Error is { } ph1 ? ErrorMetrics.ObservedOrder(ph1, h1, ratio) : null,
                    };
                }

                methodRows.Add(row);
                dt /= 2;
            }

            rows.AddRange(methodRows);
        }

        return rows;
    }

    private static (ThermalModel Model, double FinalTime, double U, Func<double, double, double, double> Exact,
        Func<double, double, double, (double, double)> Gradient, Func<double, double, double, double> Source, Func<double, double, double> Initial)
        Problem(string problem, Grid2D grid)
    {
        var material = MaterialProperties.LithiumIonPouchCell;
        switch (problem)
        {
            case DirichletEigenmode:
            {
                const double tb = 25, a = 20;
                var lambda = material.Diffusivity * Math.PI * Math.PI * (1 / (Lx * Lx) + 1 / (Ly * Ly));
                var model = new ThermalModel(grid, material, BoundaryConditions.All(BoundaryCondition.FixedTemperature(tb)), new CoolingModel(tb, 0, 0, 0));
                return (model, 100, 0,
                    (x, y, t) => tb + a * Math.Exp(-lambda * t) * Math.Sin(Math.PI * x / Lx) * Math.Sin(Math.PI * y / Ly),
                    (x, y, t) => (a * Math.Exp(-lambda * t) * Math.PI / Lx * Math.Cos(Math.PI * x / Lx) * Math.Sin(Math.PI * y / Ly),
                        a * Math.Exp(-lambda * t) * Math.PI / Ly * Math.Sin(Math.PI * x / Lx) * Math.Cos(Math.PI * y / Ly)),
                    (_, _, _) => 0,
                    (x, y) => tb + a * Math.Sin(Math.PI * x / Lx) * Math.Sin(Math.PI * y / Ly));
            }

            case ManufacturedCooling:
            {
                const double tc = 22, beta = 10, a = 15, tau = 60, u = 0.5;
                var cooling = new CoolingModel(tc, 5, 120, 150);
                var model = new ThermalModel(grid, material, BoundaryConditions.All(BoundaryCondition.Insulated), cooling);
                var hv = cooling.VolumetricCoefficient(u, material);
                var rhoC = material.VolumetricHeatCapacity;
                var k = material.Conductivity;
                var kappa2 = Math.Pow(Math.PI / Lx, 2) + Math.Pow(2 * Math.PI / Ly, 2);
                double Phi(double x, double y) => Math.Cos(Math.PI * x / Lx) * Math.Cos(2 * Math.PI * y / Ly);
                double Gamma(double t) => a * (1 - Math.Exp(-t / tau));
                double GammaDot(double t) => a / tau * Math.Exp(-t / tau);
                return (model, 120, u,
                    (x, y, t) => tc + beta + Gamma(t) * Phi(x, y),
                    (x, y, t) => (-Gamma(t) * Math.PI / Lx * Math.Sin(Math.PI * x / Lx) * Math.Cos(2 * Math.PI * y / Ly),
                        -Gamma(t) * 2 * Math.PI / Ly * Math.Cos(Math.PI * x / Lx) * Math.Sin(2 * Math.PI * y / Ly)),
                    (x, y, t) => rhoC * GammaDot(t) * Phi(x, y) + (k * kappa2 + hv) * Gamma(t) * Phi(x, y) + hv * beta,
                    (_, _) => tc + beta);
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(problem), problem, "Unknown verification problem.");
        }
    }

    private static MethodConvergenceRow RunFvm(string problem, int nx, int ny, double dtTarget)
    {
        var grid = new Grid2D(nx, ny, Lx, Ly);
        var p = Problem(problem, grid);
        var steps = (int)Math.Ceiling(p.FinalTime / dtTarget - 1e-9);
        var dt = p.FinalTime / steps;
        var sw = Stopwatch.StartNew();
        var solver = new HeatEquationSolver(p.Model, TimeScheme.CrankNicolson, dt);
        var u = grid.CreateField(p.Initial);
        var next = new double[u.Length];
        var qNow = new double[u.Length];
        var qNext = new double[u.Length];
        for (var s = 0; s < steps; s++)
        {
            Fill(grid, qNow, s * dt, p.Source);
            Fill(grid, qNext, (s + 1) * dt, p.Source);
            solver.Step(u, next, qNow, qNext, p.U);
            (u, next) = (next, u);
        }

        sw.Stop();
        var exact = grid.CreateField((x, y) => p.Exact(x, y, p.FinalTime));
        return new MethodConvergenceRow("FVM", problem, nx, ny, grid.Dx, grid.CellCount, solver.Laplacian.Matrix.NonZeroCount,
            solver.Laplacian.Matrix.HalfBandwidth(), dt, steps, ErrorMetrics.Rmse(u, exact), ErrorMetrics.MaxError(u, exact),
            null, null, null, null, null, null, sw.Elapsed.TotalMilliseconds);
    }

    private static MethodConvergenceRow RunFem(string problem, int nx, int ny, double dtTarget)
    {
        var grid = new Grid2D(Math.Max(nx, 2), Math.Max(ny, 2), Lx, Ly);
        var p = Problem(problem, grid);
        var steps = (int)Math.Ceiling(p.FinalTime / dtTarget - 1e-9);
        var dt = p.FinalTime / steps;
        var sw = Stopwatch.StartNew();
        var mesh = new FemMesh2D(nx, ny, Lx, Ly);
        var solver = new FemHeatEquationSolver(mesh, p.Model, TimeScheme.CrankNicolson, dt);
        var u = mesh.CreateNodalField(p.Initial);
        var next = new double[u.Length];
        double[] Source(double t) => mesh.CreateNodalField((x, y) => p.Source(x, y, t));
        for (var s = 0; s < steps; s++)
        {
            solver.Step(u, next, Source(s * dt), Source((s + 1) * dt), p.U);
            (u, next) = (next, u);
        }

        sw.Stop();
        var t1 = p.FinalTime;
        var exact = mesh.CreateNodalField((x, y) => p.Exact(x, y, t1));
        var area = Math.Sqrt(Lx * Ly);
        return new MethodConvergenceRow("FEM", problem, nx, ny, mesh.Hx, mesh.NodeCount, solver.SystemNonZeros, solver.SystemHalfBandwidth, dt, steps,
            ErrorMetrics.Rmse(u, exact), ErrorMetrics.MaxError(u, exact),
            FemErrorNorms.L2Error(mesh, u, (x, y) => p.Exact(x, y, t1)) / area,
            FemErrorNorms.H1SeminormError(mesh, u, (x, y) => p.Gradient(x, y, t1)) / area,
            null, null, null, null, sw.Elapsed.TotalMilliseconds);
    }

    private static void Fill(Grid2D grid, double[] target, double t, Func<double, double, double, double> f)
    {
        for (var k = 0; k < target.Length; k++)
        {
            var (x, y) = grid.CellCentre(k);
            target[k] = f(x, y, t);
        }
    }

    /// <summary>
    /// Runs the battery model (Robin edges, cold-plate sink, Gaussian defect, CC–CV load, constant cooling u)
    /// with both discretisations on each grid. No exact solution exists, so agreement is measured directly
    /// (FEM evaluated at the FVM cell centres) and convergence of both methods to a common limit is read from
    /// the shrinking FVM–FEM difference.
    /// </summary>
    public static (IReadOnlyList<BatteryComparisonRow> Rows, BatteryComparisonFields Fields) CompareOnBatteryModel(
        ThermalModel model,
        HeatSourceModel source,
        double coolingLevel,
        double initialTemperature,
        double duration,
        double timeStep,
        IReadOnlyList<(int Nx, int Ny)> grids,
        (int Nx, int Ny) displayGrid,
        double snapshotTime)
    {
        var rows = new List<BatteryComparisonRow>();
        BatteryComparisonFields? fields = null;
        var steps = (int)Math.Round(duration / timeStep);
        var snapshotStep = (int)Math.Round(snapshotTime / timeStep);

        foreach (var (nx, ny) in grids)
        {
            var grid = new Grid2D(nx, ny, model.Grid.LengthX, model.Grid.LengthY);
            var fvmModel = model.WithGrid(grid);

            // ---- finite volumes ----
            var sw = Stopwatch.StartNew();
            var fvm = new HeatEquationSolver(fvmModel, TimeScheme.CrankNicolson, timeStep);
            var spatial = source.SpatialField(grid);
            var t = grid.CreateField(initialTemperature);
            var next = new double[t.Length];
            var qNow = new double[t.Length];
            var qNext = new double[t.Length];
            var fvmPeak = Vector.Max(t);
            double[]? fvmSnapshot = null;
            for (var s = 0; s < steps; s++)
            {
                source.Evaluate(grid, s * timeStep, qNow, spatial);
                source.Evaluate(grid, (s + 1) * timeStep, qNext, spatial);
                fvm.Step(t, next, qNow, qNext, coolingLevel);
                (t, next) = (next, t);
                fvmPeak = Math.Max(fvmPeak, Vector.Max(t));
                if (s + 1 == snapshotStep)
                {
                    fvmSnapshot = (double[])t.Clone();
                }
            }

            var fvmMs = sw.Elapsed.TotalMilliseconds;
            var fvmFinal = t;

            // ---- finite elements ----
            sw.Restart();
            var mesh = FemMesh2D.FromGrid(grid);
            var fem = new FemHeatEquationSolver(mesh, fvmModel, TimeScheme.CrankNicolson, timeStep);
            var shape = mesh.CreateNodalField(source.ShapeAt);
            var u = mesh.CreateNodalField(initialTemperature);
            var unext = new double[u.Length];
            var fNow = new double[u.Length];
            var fNext = new double[u.Length];
            var femPeak = Vector.Max(u);
            double[]? femSnapshot = null;
            for (var s = 0; s < steps; s++)
            {
                Scale(shape, source.Load.At(s * timeStep), fNow);
                Scale(shape, source.Load.At((s + 1) * timeStep), fNext);
                fem.Step(u, unext, fNow, fNext, coolingLevel);
                (u, unext) = (unext, u);
                femPeak = Math.Max(femPeak, Vector.Max(u));
                if (s + 1 == snapshotStep)
                {
                    femSnapshot = (double[])u.Clone();
                }
            }

            var femMs = sw.Elapsed.TotalMilliseconds;
            var femAtCentres = mesh.SampleAtCellCentres(u, grid);
            var difference = ErrorMetrics.Difference(femAtCentres, fvmFinal);

            rows.Add(new BatteryComparisonRow(
                nx, ny, grid.Dx, grid.CellCount, mesh.NodeCount, fvm.Laplacian.Matrix.NonZeroCount, fem.SystemNonZeros,
                fvmPeak, femPeak, Vector.Max(fvmFinal), Vector.Max(u),
                Vector.Mean(fvmFinal), fem.ThermalEnergy(u) / (fvmModel.Material.VolumetricHeatCapacity * fvmModel.Material.Thickness * grid.LengthX * grid.LengthY),
                Math.Sqrt(difference.Select(d => d * d).Average()), Vector.NormInf(difference),
                fvmMs, femMs, fvm.ThermalEnergy(fvmFinal), fem.ThermalEnergy(u)));

            if ((nx, ny) == displayGrid && fvmSnapshot is not null && femSnapshot is not null)
            {
                var femCells = mesh.SampleAtCellCentres(femSnapshot, grid);
                fields = new BatteryComparisonFields(grid, mesh, fvmSnapshot, femSnapshot, femCells,
                    ErrorMetrics.Difference(femCells, fvmSnapshot), snapshotStep * timeStep);
            }
        }

        return (rows, fields ?? throw new ArgumentException("The display grid must be one of the compared grids.", nameof(displayGrid)));
    }

    private static void Scale(double[] shape, double s, double[] target)
    {
        for (var k = 0; k < shape.Length; k++)
        {
            target[k] = s * shape[k];
        }
    }
}
