using ThermoTwin.Numerics.Analysis;
using ThermoTwin.Numerics.Fem;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Validation;

namespace ThermoTwin.Tests.Numerics;

public sealed class FemTests
{
    private static readonly MaterialProperties Material = MaterialProperties.LithiumIonPouchCell;

    [Fact]
    public void Element_mass_matrix_is_spd_and_integrates_constants()
    {
        var e = new TriangleElement(0, new FemNode(0, 0.0, 0.0), new FemNode(1, 0.03, 0.01), new FemNode(2, 0.01, 0.02));
        var m = e.Mass();
        var sum = 0.0;
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(e.Area / 6, m[i, i], 15);
            for (var j = 0; j < 3; j++)
            {
                Assert.Equal(m[i, j], m[j, i], 15);
                sum += m[i, j];
                if (i != j)
                {
                    Assert.Equal(e.Area / 12, m[i, j], 15);
                }
            }
        }

        Assert.Equal(e.Area, sum, 14); // Σ_ij ∫ φ_i φ_j = ∫ 1
        var x = new[] { 0.3, -1.2, 0.7 };
        var quadratic = 0.0;
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                quadratic += x[i] * m[i, j] * x[j];
            }
        }

        Assert.True(quadratic > 0);
    }

    [Fact]
    public void Element_stiffness_matches_the_reference_right_triangle()
    {
        // Vertices (0,0), (h,0), (0,h): K = k/2 · [[2,−1,−1], [−1,1,0], [−1,0,1]] independent of h.
        const double h = 0.004, k = 20;
        var e = new TriangleElement(0, new FemNode(0, 0, 0), new FemNode(1, h, 0), new FemNode(2, 0, h));
        var s = e.Stiffness(k);
        double[,] expected = { { 2, -1, -1 }, { -1, 1, 0 }, { -1, 0, 1 } };
        for (var i = 0; i < 3; i++)
        {
            var row = 0.0;
            for (var j = 0; j < 3; j++)
            {
                Assert.Equal(k / 2 * expected[i, j], s[i, j], 10);
                row += s[i, j];
            }

            Assert.Equal(0, row, 10); // constants are in the kernel
        }

        Assert.Equal(h * h / 2, e.Area, 15);
        Assert.Equal(h * h, e.Jacobian, 15);
    }

    [Fact]
    public void Clockwise_or_degenerate_triangles_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new TriangleElement(0, new FemNode(0, 0, 0), new FemNode(1, 0, 1), new FemNode(2, 1, 0)));
        Assert.Throws<ArgumentException>(() => new TriangleElement(0, new FemNode(0, 0, 0), new FemNode(1, 1, 1), new FemNode(2, 2, 2)));
    }

    [Fact]
    public void Barycentric_gradients_reproduce_linear_functions()
    {
        var mesh = new FemMesh2D(7, 4, 0.2, 0.1);
        var f = mesh.CreateNodalField((x, y) => 3 + 50 * x - 20 * y);
        foreach (var e in mesh.Elements)
        {
            var gx = 0.0;
            var gy = 0.0;
            for (var i = 0; i < 3; i++)
            {
                gx += f[e.Nodes[i]] * e.GradX[i];
                gy += f[e.Nodes[i]] * e.GradY[i];
            }

            Assert.Equal(50, gx, 9);
            Assert.Equal(-20, gy, 9);
        }

        Assert.Equal(3 + 50 * 0.123 - 20 * 0.071, mesh.Evaluate(f, 0.123, 0.071), 10);
    }

    [Fact]
    public void Structured_mesh_has_expected_topology_and_bandwidth()
    {
        var mesh = new FemMesh2D(8, 4, 0.2, 0.1);
        Assert.Equal(9 * 5, mesh.NodeCount);
        Assert.Equal(2 * 8 * 4, mesh.ElementCount);
        Assert.Equal(2 * (8 + 4), mesh.BoundarySegments.Count);
        Assert.Equal(0.2 * 0.1, mesh.Elements.Sum(e => e.Area), 14);
        Assert.Equal(2 * (0.2 + 0.1), mesh.BoundarySegments.Sum(s => s.Length), 14);

        var matrices = FemAssembler.Assemble(mesh, BoundaryConditions.All(BoundaryCondition.Insulated), 20);
        Assert.Equal(4 + 2, matrices.Stiffness.HalfBandwidth());
        Assert.True(matrices.Stiffness.NonZeroCount <= 7 * mesh.NodeCount);
    }

    [Fact]
    public void Global_matrices_are_symmetric_with_the_right_definiteness()
    {
        var mesh = new FemMesh2D(10, 5, 0.2, 0.1);
        var fem = FemAssembler.Assemble(mesh, BoundaryConditions.All(BoundaryCondition.Insulated), 20);
        Assert.True(fem.Mass.SymmetryDefect() < 1e-15);
        Assert.True(fem.Stiffness.SymmetryDefect() < 1e-12);

        // Σ_ij M_ij = |Ω|, K·𝟙 = 0 (pure Neumann: constants in the kernel), xᵀKx ≥ 0, xᵀMx > 0.
        Assert.Equal(0.02, fem.LumpedMass.Sum(), 14);
        Assert.All(fem.Stiffness.RowSums(), r => Assert.Equal(0, r, 9));
        var rng = new Random(3);
        for (var trial = 0; trial < 20; trial++)
        {
            var x = Enumerable.Range(0, mesh.NodeCount).Select(_ => rng.NextDouble() - 0.5).ToArray();
            Assert.True(fem.Stiffness.QuadraticForm(x) >= -1e-12);
            Assert.True(fem.Mass.QuadraticForm(x) > 0);
        }
    }

    [Fact]
    public void Boundary_terms_integrate_exactly_along_the_edges()
    {
        var mesh = new FemMesh2D(6, 3, 0.2, 0.1);
        const double h = 12, ambient = 25, flux = 300;
        var perimeter = 2 * (0.2 + 0.1);

        var robin = FemAssembler.Assemble(mesh, BoundaryConditions.All(BoundaryCondition.Convective(ambient, h)), 20);
        var r = robin.RobinMass.ToDense();
        var total = 0.0;
        for (var i = 0; i < mesh.NodeCount; i++)
        {
            for (var j = 0; j < mesh.NodeCount; j++)
            {
                total += r[i, j];
            }
        }

        Assert.Equal(h * perimeter, total, 10);                       // Σ_ij ∫_Γ h φ_i φ_j = ∫_Γ h
        Assert.Equal(h * ambient * perimeter, robin.BoundaryLoad.Sum(), 9);
        Assert.True(robin.RobinMass.SymmetryDefect() < 1e-15);
        Assert.Equal(0, robin.RobinMass[mesh.NodeIndex(3, 1), mesh.NodeIndex(3, 1)]); // interior node untouched

        var neumann = FemAssembler.Assemble(mesh, BoundaryConditions.All(new BoundaryCondition(BoundaryKind.Neumann, flux)), 20);
        Assert.Equal(-flux * perimeter, neumann.BoundaryLoad.Sum(), 9); // outward flux removes energy
        Assert.Equal(0, neumann.RobinMass.NonZeroCount);

        var dirichlet = FemAssembler.Assemble(mesh, BoundaryConditions.All(BoundaryCondition.FixedTemperature(30)), 20);
        Assert.Equal(2 * (6 + 3), dirichlet.DirichletCount);
        Assert.All(mesh.EdgeNodes(MeshEdge.West), n => Assert.True(dirichlet.IsDirichlet[n]));
    }

    [Fact]
    public void Steady_robin_problem_converges_to_the_analytic_quadratic()
    {
        // −k T'' = q on (0, L) with −k ∂T/∂n = h (T − T∞) at x = 0, L; insulated in y.
        // Exact: T = T∞ + qL/(2h) + q(xL − x²)/(2k). The Robin data enter only through the boundary terms, so the
        // offset qL/(2h) ≈ 267 K is reproduced only if those terms are assembled correctly; the interior error is O(h²).
        const double lx = 0.2, ly = 0.05, q = 4e4, h = 15, tInf = 20;
        var errors = new List<double>();
        var l2 = new List<double>();
        foreach (var nx in new[] { 10, 20, 40 })
        {
            var grid = new Grid2D(nx, nx / 4, lx, ly);
            var bc = new BoundaryConditions(BoundaryCondition.Convective(tInf, h), BoundaryCondition.Convective(tInf, h), BoundaryCondition.Insulated, BoundaryCondition.Insulated);
            var model = new ThermalModel(grid, Material, bc, new CoolingModel(tInf, 0, 0, 0));
            var solver = new FemHeatEquationSolver(model, TimeScheme.ImplicitEuler, 1e12); // Δt → ∞: one step solves the steady problem
            var source = solver.Mesh.CreateNodalField(q);
            var t = solver.Mesh.CreateNodalField(tInf);
            var next = new double[t.Length];
            solver.Step(t, next, source, source, 0);
            var exact = solver.Mesh.CreateNodalField((x, _) => tInf + q * lx / (2 * h) + q * (x * lx - x * x) / (2 * Material.Conductivity));
            errors.Add(ErrorMetrics.MaxError(next, exact));
            l2.Add(FemErrorNorms.L2Error(solver.Mesh, next, (x, _) => tInf + q * lx / (2 * h) + q * (x * lx - x * x) / (2 * Material.Conductivity)));
        }

        // L² converges at order 2; the nodal maximum sits at a corner where the Robin edge meets the one-directional
        // diagonal of the triangulation and approaches order 2 more slowly (pre-asymptotic, ≈ 1.7 here).
        Assert.True(errors[^1] < 1e-2, $"max error {errors[^1]:E3} K on a ≈ 277 K solution");
        Assert.True(errors[2] < errors[1] && errors[1] < errors[0]);
        Assert.InRange(ErrorMetrics.ObservedOrder(l2[1], l2[2]), 1.9, 2.1);
    }

    [Fact]
    public void Dirichlet_values_are_imposed_exactly_and_symmetrically()
    {
        var grid = new Grid2D(10, 5, 0.2, 0.1);
        var model = new ThermalModel(grid, Material, BoundaryConditions.All(BoundaryCondition.FixedTemperature(30)), new CoolingModel(22, 5, 120, 150));
        var solver = new FemHeatEquationSolver(model, TimeScheme.CrankNicolson, 5);
        var t = solver.Mesh.CreateNodalField(25);
        var next = new double[t.Length];
        var q = solver.Mesh.CreateNodalField(1e5);
        for (var n = 0; n < 20; n++)
        {
            solver.Step(t, next, q, q, 0.5);
            (t, next) = (next, t);
        }

        for (var i = 0; i < t.Length; i++)
        {
            if (solver.Matrices.IsDirichlet[i])
            {
                Assert.Equal(30, t[i], 12);
            }
        }

        Assert.True(t.Max() > 30); // the source heats the interior
    }

    [Theory]
    [InlineData(TimeScheme.ImplicitEuler)]
    [InlineData(TimeScheme.CrankNicolson)]
    public void Fem_conserves_energy_on_an_insulated_cell(TimeScheme scheme)
    {
        var grid = new Grid2D(12, 6, 0.2, 0.1);
        var model = new ThermalModel(grid, Material, BoundaryConditions.All(BoundaryCondition.Insulated), new CoolingModel(25, 0, 0, 0));
        var solver = new FemHeatEquationSolver(model, scheme, 10);
        var q = solver.Mesh.CreateNodalField((x, _) => 5e4 * (1 + Math.Sin(Math.PI * x / 0.2)));
        var t = solver.Mesh.CreateNodalField(25);
        var next = new double[t.Length];
        var e0 = solver.ThermalEnergy(t);
        for (var n = 0; n < 30; n++)
        {
            solver.Step(t, next, q, q, 0);
            (t, next) = (next, t);
        }

        var injected = Vector.Dot(solver.Matrices.LumpedMass, q) * Material.Thickness * 30 * 10;
        Assert.True(Math.Abs(solver.ThermalEnergy(t) - e0 - injected) / injected < 1e-10);
    }

    [Fact]
    public void Fem_converges_at_second_order_in_l2_and_first_order_in_h1()
    {
        var rows = FemVerificationStudy.Run(FemVerificationStudy.ManufacturedCooling, [(10, 5), (20, 10), (40, 20)])
            .Where(r => r.Method == "FEM").ToArray();
        Assert.InRange(rows[^1].OrderL2!.Value, 1.85, 2.15);
        Assert.InRange(rows[^1].OrderH1!.Value, 0.9, 1.1);
        Assert.True(rows[^1].L2Error < rows[0].L2Error / 10);
    }

    [Fact]
    public void Fvm_and_fem_agree_on_the_battery_model_and_converge_to_each_other()
    {
        var grid = new Grid2D(20, 10, 0.2, 0.1);
        var model = new ThermalModel(grid, Material, BoundaryConditions.All(BoundaryCondition.Convective(25, 10)), new CoolingModel(22, 5, 120, 150));
        var source = new HeatSourceModel(55_000, [new GaussianHotspot(0.138, 0.064, 450_000, 0.012)], LoadProfile.Constant());
        var (rows, fields) = FemVerificationStudy.CompareOnBatteryModel(model, source, 0.15, 25, 300, 5, [(10, 5), (20, 10), (40, 20)], (20, 10), 300);

        Assert.True(rows[^1].FieldRmsDifference < rows[0].FieldRmsDifference / 8, "difference must shrink ≈ 4× per refinement");
        Assert.True(Math.Abs(rows[^1].FvmPeak - rows[^1].FemPeak) < 0.1);
        Assert.True(Math.Abs(rows[^1].FvmEnergyJoules - rows[^1].FemEnergyJoules) / rows[^1].FvmEnergyJoules < 1e-3);
        Assert.Equal(grid.CellCount, fields.Difference.Length);
    }
}
