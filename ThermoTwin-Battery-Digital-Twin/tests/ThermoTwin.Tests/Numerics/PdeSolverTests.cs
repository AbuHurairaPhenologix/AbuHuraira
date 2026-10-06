using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Tests.Numerics;

public sealed class PdeSolverTests
{
    private static ThermalModel Model(Grid2D grid, BoundaryConditions bc) =>
        new(grid, MaterialProperties.LithiumIonPouchCell, bc, new CoolingModel(25, 0, 0, 0));

    [Fact]
    public void Discrete_laplacian_is_symmetric_and_negative_definite_with_dirichlet_edges()
    {
        var grid = new Grid2D(10, 6, 0.2, 0.1);
        var lap = DiscreteLaplacian.Assemble(grid, BoundaryConditions.All(BoundaryCondition.FixedTemperature(0)), 20);
        var a = lap.Matrix;
        for (var i = 0; i < grid.CellCount; i++)
        {
            Assert.True(a[i, i] < 0);
            for (var j = 0; j < grid.CellCount; j++)
            {
                Assert.Equal(a[i, j], a[j, i], 12);
            }
        }

        Assert.Equal(grid.Ny, a.HalfBandwidth());
    }

    [Fact]
    public void Discrete_laplacian_is_exact_for_quadratics_in_the_interior()
    {
        var grid = new Grid2D(20, 10, 0.2, 0.1);
        var lap = DiscreteLaplacian.Assemble(grid, BoundaryConditions.All(BoundaryCondition.Insulated), 20);
        var f = grid.CreateField((x, y) => x * x + 3 * y * y); // ∇²f = 2 + 6 = 8
        var result = new double[f.Length];
        lap.Matrix.Multiply(f, result);
        for (var i = 1; i < grid.Nx - 1; i++)
        {
            for (var j = 1; j < grid.Ny - 1; j++)
            {
                Assert.Equal(8.0, result[grid.Index(i, j)] + lap.BoundaryVector[grid.Index(i, j)], 8);
            }
        }
    }

    [Fact]
    public void Insulated_laplacian_annihilates_constants()
    {
        var grid = new Grid2D(12, 6, 0.2, 0.1);
        var lap = DiscreteLaplacian.Assemble(grid, BoundaryConditions.All(BoundaryCondition.Insulated), 20);
        var result = new double[grid.CellCount];
        lap.Matrix.Multiply(grid.CreateField(42), result);
        Assert.All(result, v => Assert.Equal(0, v, 9));
    }

    [Theory]
    [InlineData(TimeScheme.ExplicitEuler)]
    [InlineData(TimeScheme.ImplicitEuler)]
    [InlineData(TimeScheme.CrankNicolson)]
    public void Equilibrium_is_preserved_when_everything_is_at_boundary_temperature(TimeScheme scheme)
    {
        var grid = new Grid2D(20, 10, 0.2, 0.1);
        var solver = new HeatEquationSolver(Model(grid, BoundaryConditions.All(BoundaryCondition.FixedTemperature(30))), scheme, 0.5);
        var t = grid.CreateField(30);
        var next = new double[t.Length];
        for (var s = 0; s < 50; s++)
        {
            solver.Step(t, next, [], [], 0);
            (t, next) = (next, t);
        }

        Assert.All(t, v => Assert.Equal(30, v, 10));
    }

    [Fact]
    public void Robin_edges_relax_towards_ambient()
    {
        var grid = new Grid2D(20, 10, 0.2, 0.1);
        var solver = new HeatEquationSolver(Model(grid, BoundaryConditions.All(BoundaryCondition.Convective(20, 50))), TimeScheme.CrankNicolson, 20);
        var t = grid.CreateField(60);
        var next = new double[t.Length];
        var previous = Vector.Mean(t);
        for (var s = 0; s < 200; s++)
        {
            solver.Step(t, next, [], [], 0);
            (t, next) = (next, t);
            var mean = Vector.Mean(t);
            Assert.True(mean <= previous + 1e-12);
            previous = mean;
        }

        Assert.InRange(previous, 20, 60);
    }

    [Theory]
    [InlineData(TimeScheme.ExplicitEuler)]
    [InlineData(TimeScheme.ImplicitEuler)]
    [InlineData(TimeScheme.CrankNicolson)]
    public void Spatial_convergence_is_second_order(TimeScheme scheme)
    {
        var rows = ConvergenceStudy.SpatialStudy(scheme, [(10, 5), (20, 10), (40, 20)]);
        Assert.InRange(rows[^1].ObservedOrderRmse!.Value, 1.9, 2.1);
        Assert.True(rows[^1].Rmse < rows[0].Rmse);
    }

    [Theory]
    [InlineData(TimeScheme.ImplicitEuler, 1.0)]
    [InlineData(TimeScheme.CrankNicolson, 2.0)]
    public void Temporal_convergence_matches_theoretical_order(TimeScheme scheme, double expected)
    {
        var rows = ConvergenceStudy.TemporalStudy(scheme, [20, 10, 5], nx: 20, ny: 10);
        Assert.InRange(rows[^1].ObservedOrderRmse!.Value, expected - 0.1, expected + 0.1);
    }

    [Fact]
    public void Explicit_euler_is_stable_below_and_unstable_above_the_critical_time_step()
    {
        var probes = ConvergenceStudy.ExplicitStabilityProbe([0.95, 1.05], nx: 20, ny: 10, steps: 600);
        Assert.False(probes[0].Diverged);
        Assert.True(probes[1].Diverged);
    }

    [Fact]
    public void Stability_analysis_agrees_with_classical_bound_for_dirichlet_problem()
    {
        var grid = new Grid2D(40, 20, 0.2, 0.1);
        var solver = new HeatEquationSolver(Model(grid, BoundaryConditions.All(BoundaryCondition.FixedTemperature(0))), TimeScheme.ExplicitEuler, 0.5);
        var report = StabilityAnalyzer.Analyze(solver);

        Assert.True(report.SpectralRadiusEstimate <= report.SpectralRadiusBound * (1 + 1e-9));
        Assert.InRange(report.ExplicitCriticalTimeStep, report.ClassicalExplicitLimit, report.ClassicalExplicitLimit * 1.05);
        Assert.True(report.IsStable);

        var cn = StabilityAnalyzer.Analyze(new HeatEquationSolver(solver.Model, TimeScheme.CrankNicolson, 50));
        Assert.True(cn.IsStable);
        Assert.True(cn.StiffModeAmplification < 0); // oscillatory damping of stiff modes
    }

    [Theory]
    [InlineData(TimeScheme.ExplicitEuler)]
    [InlineData(TimeScheme.ImplicitEuler)]
    [InlineData(TimeScheme.CrankNicolson)]
    public void Finite_volume_scheme_conserves_energy(TimeScheme scheme)
    {
        var result = ConvergenceStudy.EnergyConservation(scheme, duration: 120);
        Assert.True(result.RelativeImbalance < 1e-10, $"imbalance {result.RelativeImbalance}");
    }
}
