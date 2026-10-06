using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Inverse;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;
using ThermoTwin.Numerics.ReducedOrder;

namespace ThermoTwin.Tests.Numerics;

public sealed class ReducedOrderTests
{
    private static readonly Grid2D Grid = new(16, 8, 0.2, 0.1);
    private static readonly CoolingModel Cooling = new(22, 5, 120, 150);
    private static readonly LoadProfile Load = LoadProfile.FastChargeCcCv(500, 900, 0.3);

    private static HeatEquationSolver Solver(Grid2D? grid = null) => new(
        new ThermalModel(grid ?? Grid, MaterialProperties.LithiumIonPouchCell, BoundaryConditions.All(BoundaryCondition.Convective(25, 10)), Cooling),
        TimeScheme.CrankNicolson, 10);

    private static (PodBasis Basis, HeatEquationSolver Solver) Train()
    {
        var solver = Solver();
        var plan = new CoolingPlan(100, Enumerable.Range(0, 9).Select(k => k / 8.0).ToArray());
        var cases = PodTrainer.SourceBasisCases(new SourceBasis(Grid, 6, 4), 55_000, 450_000, plan);
        var snapshots = PodTrainer.CollectSnapshots(new FullOrderThermalDynamics(solver), Load, Grid.CreateField(25), cases, PodTrainer.GeometricSampleSteps(90, 5));
        return (PodBasis.Compute(snapshots, 60), solver);
    }

    [Fact]
    public void Tridiagonal_ql_and_jacobi_eigensolvers_agree_and_diagonalise()
    {
        var rng = new Random(5);
        var a = new DenseMatrix(60, 60);
        for (var i = 0; i < 60; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                a[i, j] = a[j, i] = rng.NextDouble() - 0.5;
            }
        }

        var ql = SymmetricEigensolver.DecomposeTridiagonalQl(a);
        var jacobi = SymmetricEigensolver.DecomposeJacobi(a);
        for (var i = 0; i < 60; i++)
        {
            Assert.Equal(jacobi.Values[i], ql.Values[i], 10);
            if (i > 0)
            {
                Assert.True(ql.Values[i] <= ql.Values[i - 1]);
            }

            var v = ql.Vector(i);
            var av = a.Multiply(v);
            for (var k = 0; k < 60; k++)
            {
                Assert.Equal(ql.Values[i] * v[k], av[k], 9);
            }
        }

        var vtv = ql.Vectors.Transpose().Multiply(ql.Vectors);
        for (var i = 0; i < 60; i++)
        {
            for (var j = 0; j < 60; j++)
            {
                Assert.Equal(i == j ? 1 : 0, vtv[i, j], 10);
            }
        }
    }

    [Fact]
    public void Pod_modes_are_orthonormal_and_the_spectrum_is_ordered()
    {
        var (pod, _) = Train();
        Assert.True(pod.Rank >= 20);
        for (var i = 0; i < pod.Rank; i++)
        {
            for (var j = 0; j <= i; j++)
            {
                Assert.Equal(i == j ? 1 : 0, Vector.Dot(pod.Modes[i], pod.Modes[j]), 9);
            }
        }

        for (var i = 1; i < pod.Eigenvalues.Length; i++)
        {
            Assert.True(pod.Eigenvalues[i] <= pod.Eigenvalues[i - 1] + 1e-12);
            Assert.True(pod.Eigenvalues[i] >= 0);
            Assert.True(pod.CumulativeEnergy[i] >= pod.CumulativeEnergy[i - 1]);
        }

        Assert.Equal(1, pod.CumulativeEnergy[^1], 10);
    }

    [Fact]
    public void Mean_projection_error_of_the_snapshots_equals_the_discarded_eigenvalues()
    {
        // POD optimality: (1/m) Σ_j ‖x_j − T̄ − Φ_r Φ_rᵀ (x_j − T̄)‖² = Σ_{i > r} λ_i.
        var rng = new Random(2);
        var snapshots = new SnapshotSet(30);
        for (var j = 0; j < 12; j++)
        {
            var s = new double[30];
            for (var k = 0; k < 30; k++)
            {
                s[k] = Math.Sin(0.3 * k * (j + 1)) + 0.1 * rng.NextDouble();
            }

            snapshots.Add(s);
        }

        var pod = PodBasis.Compute(snapshots, 30);
        foreach (var r in new[] { 1, 3, 6, 9 })
        {
            var error = snapshots.Snapshots.Average(s => Math.Pow(pod.ProjectionError(s, r), 2));
            var tail = pod.Eigenvalues.Skip(r).Sum();
            Assert.Equal(tail, error, 9);
        }
    }

    [Fact]
    public void Rom_error_decreases_with_r_and_tracks_the_projection_error()
    {
        var (pod, solver) = Train();
        var fom = new FullOrderThermalDynamics(solver);
        var source = Grid.CreateField((x, y) => 55_000 + new GaussianHotspot(0.12, 0.055, 450_000, 0.012).Evaluate(x, y));
        var plan = new CoolingPlan(100, [0.1, 0.5, 0.2, 0.8, 0.3, 0.0, 0.4, 0.6, 0.2]);
        var full = ThermalTrajectorySimulator.Simulate(fom, Load, Grid.CreateField(25), source, 0, plan);
        var previous = double.PositiveInfinity;
        foreach (var r in new[] { 2, 5, 10, 20 })
        {
            var rom = new ReducedThermalModel(pod, r, solver);
            Assert.Equal(r, rom.StateDimension);
            Assert.Equal(r, rom.ConstantForcing.Length);
            Assert.Equal(r, rom.CoolantForcing.Length);
            Assert.All(rom.ReducedSpectrum, l => Assert.True(l <= 1e-12)); // Φᵀ A Φ is negative semi-definite

            var reduced = ThermalTrajectorySimulator.Simulate(rom, Load, Grid.CreateField(25), source, 0, plan);
            var error = Math.Sqrt(full.Fields.Zip(reduced.Fields, (a, b) => Math.Pow(ErrorMetrics.Rmse(a, b), 2)).Average());
            var projection = Math.Sqrt(full.Fields.Average(f => Math.Pow(pod.ProjectionError(f, r), 2) / f.Length));
            Assert.True(error < previous, $"r = {r}: {error:E3} ≥ {previous:E3}");
            // Galerkin quasi-optimality: the ROM error is close to the best approximation error in span(Φ_r).
            Assert.True(error <= 1.3 * projection + 1e-3, $"r = {r}: ROM error {error:E3} vs projection error {projection:E3}");
            previous = error;
        }

        Assert.True(previous < 0.15, $"r = 20 error {previous:E3} K");
    }

    [Fact]
    public void Rom_with_a_complete_basis_reproduces_the_full_model()
    {
        var grid = new Grid2D(4, 3, 0.2, 0.1);
        var solver = Solver(grid);
        var rng = new Random(9);
        var snapshots = new SnapshotSet(grid.CellCount);
        for (var j = 0; j < 20; j++)
        {
            snapshots.Add(Enumerable.Range(0, grid.CellCount).Select(_ => 25 + 10 * rng.NextDouble()).ToArray());
        }

        var pod = PodBasis.Compute(snapshots, grid.CellCount);
        Assert.Equal(grid.CellCount, pod.Rank);
        var rom = new ReducedThermalModel(pod, pod.Rank, solver);
        var fom = new FullOrderThermalDynamics(solver);
        var source = grid.CreateField((x, y) => 50_000 + 2e5 * x);
        var plan = new CoolingPlan(50, [0.2, 0.9, 0.0, 0.5]);
        var a = ThermalTrajectorySimulator.Simulate(fom, Load, grid.CreateField(25), source, 0, plan);
        var b = ThermalTrajectorySimulator.Simulate(rom, Load, grid.CreateField(25), source, 0, plan);
        Assert.True(ErrorMetrics.MaxError(a.Fields[^1], b.Fields[^1]) < 1e-9);
    }

    [Fact]
    public void Reduced_adjoint_gradient_matches_finite_differences()
    {
        var (pod, solver) = Train();
        var rom = new ReducedThermalModel(pod, 15, solver);
        var source = Grid.CreateField((x, y) => 55_000 + new GaussianHotspot(0.13, 0.06, 450_000, 0.015).Evaluate(x, y));
        var objective = new PdeConstrainedObjective(rom, Load, Cooling, Grid.CreateField(25), source, 0, new ControlProblemSettings(38, 6, 150, 0.02));
        var u = new[] { 0.12, 0.35, 0.2, 0.55, 0.3, 0.15 };
        var adjoint = objective.EvaluateWithGradient(u, 1e4).Gradient!;
        var fd = objective.FiniteDifferenceGradient(u, 1e4, 1e-6, central: true);
        Assert.True(Vector.Norm2(Vector.Subtract(adjoint, fd)) / Vector.Norm2(fd) < 1e-5);
    }

    [Fact]
    public void Output_restricted_rom_reconstructs_the_selected_cells_exactly()
    {
        var (pod, solver) = Train();
        var rom = new ReducedThermalModel(pod, 10, solver);
        int[] cells = [3, 40, 77, 101];
        var restricted = rom.WithOutputCells(cells);
        Assert.Equal(cells.Length, restricted.FieldDimension);
        Assert.Equal(Grid.CellCount, restricted.DomainCells);
        var state = Enumerable.Range(0, 10).Select(i => Math.Sin(i)).ToArray();
        var full = rom.LiftFull(state);
        var partial = new double[cells.Length];
        restricted.Lift(state, partial);
        for (var i = 0; i < cells.Length; i++)
        {
            Assert.Equal(full[cells[i]], partial[i], 12);
        }
    }

    [Fact]
    public void Rom_optimiser_returns_a_plan_that_is_feasible_on_the_full_model()
    {
        var (pod, solver) = Train();
        var source = Grid.CreateField((x, y) => 55_000 + new GaussianHotspot(0.13, 0.06, 450_000, 0.015).Evaluate(x, y));
        var optimizer = new ReducedOrderCoolingOptimizer(new ReducedThermalModel(pod, 20, solver), solver, Load, Cooling, null, validationThreshold: 0.5);
        var options = new CoolingOptimizationOptions(SafeTemperature: 38, Segments: 6, SegmentDuration: 150, MaxIterationsPerStage: 30);
        var result = optimizer.Optimize(Grid.CreateField(25), source, 0, options, Enumerable.Repeat(0.3, 6).ToArray());

        var check = new PdeConstrainedObjective(new FullOrderThermalDynamics(solver), Load, Cooling, Grid.CreateField(25), source, 0,
            new ControlProblemSettings(38, 6, 150, 0.02)).Evaluate(result.Plan.Levels, 0);
        Assert.True(check.MaxViolation <= options.FeasibilityTolerance + 1e-9, $"violation {check.MaxViolation:0.0000} K on the FOM");
        Assert.NotNull(optimizer.LastReport);
        Assert.True(optimizer.LastReport!.ScreenedCells < Grid.CellCount);
    }

    [Fact]
    public void Rom_with_unacceptable_error_falls_back_to_the_full_model()
    {
        var (pod, solver) = Train();
        var source = Grid.CreateField((x, y) => 55_000 + new GaussianHotspot(0.13, 0.06, 450_000, 0.015).Evaluate(x, y));
        var optimizer = new ReducedOrderCoolingOptimizer(new ReducedThermalModel(pod, 2, solver), solver, Load, Cooling, null, validationThreshold: 1e-4);
        var options = new CoolingOptimizationOptions(SafeTemperature: 38, Segments: 6, SegmentDuration: 150, MaxIterationsPerStage: 20);
        var result = optimizer.Optimize(Grid.CreateField(25), source, 0, options, Enumerable.Repeat(0.3, 6).ToArray());
        Assert.True(optimizer.LastReport!.FellBack);
        Assert.Contains("fallback", result.Model, StringComparison.OrdinalIgnoreCase);
        Assert.True(result.Feasible);
    }
}
