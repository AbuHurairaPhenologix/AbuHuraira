using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;

namespace ThermoTwin.Tests.Numerics;

public sealed class LinearAlgebraTests
{
    [Fact]
    public void BandedCholesky_matches_dense_Cholesky_on_random_spd_band_matrix()
    {
        const int n = 60, p = 5;
        var rng = new Random(3);
        var band = new BandedSymmetricMatrix(n, p);
        var dense = new DenseMatrix(n, n);
        for (var i = 0; i < n; i++)
        {
            for (var j = Math.Max(0, i - p); j < i; j++)
            {
                var v = rng.NextDouble() - 0.5;
                band[i, j] = v;
                dense[i, j] = v;
                dense[j, i] = v;
            }

            band[i, i] = 2 * p + 1; // diagonally dominant ⇒ SPD
            dense[i, i] = 2 * p + 1;
        }

        var b = Enumerable.Range(0, n).Select(i => Math.Sin(i)).ToArray();
        var x1 = b.ToArray();
        BandedCholesky.Factor(band).SolveInPlace(x1);
        var x2 = dense.SolveSpd(b);

        for (var i = 0; i < n; i++)
        {
            Assert.Equal(x2[i], x1[i], 12);
        }
    }

    [Fact]
    public void Cholesky_rejects_indefinite_matrix()
    {
        var m = new DenseMatrix(2, 2) { [0, 0] = 1, [0, 1] = 2, [1, 0] = 2, [1, 1] = 1 };
        Assert.Throws<InvalidOperationException>(() => m.SolveSpd([1.0, 1.0]));
    }

    [Fact]
    public void Implicit_step_agrees_with_conjugate_gradient_solution()
    {
        var grid = new Grid2D(16, 8, 0.2, 0.1);
        var model = new ThermalModel(grid, MaterialProperties.LithiumIonPouchCell,
            BoundaryConditions.All(BoundaryCondition.Convective(25, 15)), new CoolingModel(20, 5, 100, 100));
        var solver = new HeatEquationSolver(model, TimeScheme.ImplicitEuler, 10);
        var t0 = grid.CreateField((x, y) => 25 + 10 * x + 5 * y);
        var direct = new double[grid.CellCount];
        solver.Step(t0, direct, [], [], 0.4, homogeneous: true);

        // With homogeneous data and no source the implicit step is (I − Δt M) T¹ = T⁰.
        var (cg, iterations, residual) = ConjugateGradient.Solve(v => solver.ApplyImplicitOperator(v, 0.4), t0, 1e-13);

        Assert.True(residual < 1e-12);
        Assert.InRange(iterations, 1, grid.CellCount);
        for (var k = 0; k < grid.CellCount; k++)
        {
            Assert.Equal(cg[k], direct[k], 9);
        }
    }
}
