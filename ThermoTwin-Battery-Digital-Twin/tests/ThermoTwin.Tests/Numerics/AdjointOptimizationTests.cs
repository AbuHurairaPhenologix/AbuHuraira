using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;
using ThermoTwin.Numerics.Pde;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Prediction;

namespace ThermoTwin.Tests.Numerics;

public sealed class AdjointOptimizationTests
{
    private static readonly Grid2D Grid = new(16, 8, 0.2, 0.1);
    private static readonly CoolingModel Cooling = new(22, 5, 120, 150);
    private static readonly LoadProfile Load = LoadProfile.FastChargeCcCv(500, 900, 0.3);

    private static HeatEquationSolver Solver(TimeScheme scheme = TimeScheme.CrankNicolson) => new(
        new ThermalModel(Grid, MaterialProperties.LithiumIonPouchCell, BoundaryConditions.All(BoundaryCondition.Convective(25, 10)), Cooling),
        scheme, 10);

    private static double[] Source() => Grid.CreateField((x, y) => 55_000 + new GaussianHotspot(0.13, 0.06, 450_000, 0.015).Evaluate(x, y));

    private static PdeConstrainedObjective Objective(IThermalDynamics dynamics, double safe = 38, int segments = 6) =>
        new(dynamics, Load, Cooling, Grid.CreateField(25), Source(), 0, new ControlProblemSettings(safe, segments, 150, 0.02));

    [Theory]
    [InlineData(TimeScheme.CrankNicolson)]
    [InlineData(TimeScheme.ImplicitEuler)]
    public void Adjoint_gradient_matches_central_finite_differences(TimeScheme scheme)
    {
        var objective = Objective(new FullOrderThermalDynamics(Solver(scheme)));
        var u = new[] { 0.12, 0.35, 0.2, 0.55, 0.3, 0.15 };
        var evaluation = objective.EvaluateWithGradient(u, 1e4);
        Assert.True(evaluation.ViolatingPoints > 0, "the test requires an active state-constraint penalty");

        var fd = objective.FiniteDifferenceGradient(u, 1e4, 1e-6, central: true);
        var error = Vector.Norm2(Vector.Subtract(evaluation.Gradient!, fd)) / Vector.Norm2(fd);
        Assert.True(error < 1e-5, $"relative gradient error {error:E3}");
        Assert.Equal(1, objective.AdjointSolves);
    }

    [Fact]
    public void Without_active_constraint_the_gradient_is_the_explicit_control_cost()
    {
        var objective = Objective(new FullOrderThermalDynamics(Solver()), safe: 500);
        var u = new[] { 0.1, 0.4, 0.4, 0.2, 0.9, 0.0 };
        var g = objective.EvaluateWithGradient(u, 1e4).Gradient!;
        var eRef = Cooling.RatedPower * 6 * 150;
        for (var k = 0; k < u.Length; k++)
        {
            var expected = 3 * Cooling.RatedPower * u[k] * u[k] * 150 / eRef
                           + (k > 0 ? 2 * 0.02 * (u[k] - u[k - 1]) : 0)
                           - (k < u.Length - 1 ? 2 * 0.02 * (u[k + 1] - u[k]) : 0);
            Assert.Equal(expected, g[k], 12);
        }
    }

    [Fact]
    public void Full_order_dynamics_reproduce_the_forward_predictor()
    {
        var solver = Solver();
        var plan = new CoolingPlan(150, [0.1, 0.5, 0.2, 0.8, 0.3, 0.0]);
        var reference = new ThermalPredictor(solver, Load).Predict(Grid.CreateField(25), Source(), 0, plan);
        var evaluation = Objective(new FullOrderThermalDynamics(solver)).Evaluate(plan.Levels, 0);
        for (var n = 0; n < reference.MaxTemperature.Length; n++)
        {
            Assert.Equal(reference.MaxTemperature[n], evaluation.Forecast.MaxTemperature[n], 10);
        }

        Assert.Equal(reference.CoolingEnergy, evaluation.EnergyJoules, 9);
    }

    [Fact]
    public void Projected_gradient_norm_vanishes_exactly_at_kkt_points()
    {
        Assert.Equal(0, AdjointCoolingOptimizer.ProjectedGradientNorm([0, 1, 0.4], [2.0, -3.0, 0.0]));
        Assert.Equal(0.25, AdjointCoolingOptimizer.ProjectedGradientNorm([0, 1, 0.4], [2.0, -3.0, 0.25]), 15);
        Assert.Equal(1, AdjointCoolingOptimizer.ProjectedGradientNorm([0, 0.5], [-5.0, 0.0]), 15); // projection clips at u = 1
    }

    [Fact]
    public void Adjoint_optimiser_is_feasible_within_bounds_and_beats_the_best_constant_level()
    {
        var solver = Solver();
        var optimizer = new AdjointCoolingOptimizer(new FullOrderThermalDynamics(solver), Load, Cooling);
        var options = new CoolingOptimizationOptions(SafeTemperature: 38, Segments: 6, SegmentDuration: 150, MaxIterationsPerStage: 40);
        var initial = Grid.CreateField(25);
        var result = optimizer.Optimize(initial, Source(), 0, options, Enumerable.Repeat(0.3, 6).ToArray());

        Assert.True(result.Feasible, $"peak {result.PeakTemperature:0.000}");
        Assert.All(result.Plan.Levels, u => Assert.InRange(u, 0, 1));
        Assert.True(result.AdjointSolves > 0);

        var legacy = new CoolingOptimizer(new ThermalPredictor(solver, Load));
        var constant = legacy.MinimumFeasibleConstantLevel(initial, Source(), 0, options);
        var constantEnergy = legacy.EvaluatePlan(initial, Source(), 0, CoolingPlan.Constant(constant, 6, 150), options).Forecast.CoolingEnergy;
        Assert.True(result.CoolingEnergy <= constantEnergy * 1.001, $"{result.CoolingEnergy:0} J vs constant {constantEnergy:0} J");

        // Objective decreases monotonically within each penalty stage (Armijo).
        foreach (var stage in optimizer.LastHistory.GroupBy(h => h.Mu))
        {
            var values = stage.Select(h => h.Value).ToArray();
            for (var i = 1; i < values.Length; i++)
            {
                Assert.True(values[i] <= values[i - 1] + 1e-12);
            }
        }

        // First-order optimality of the last stage.
        Assert.NotNull(result.Kkt);
        Assert.True(result.Kkt!.ProjectedGradientNorm < 1e-3, $"‖P(u − g) − u‖ = {result.Kkt.ProjectedGradientNorm:E2}");
        Assert.True(result.Kkt.MinLowerMultiplier >= -1e-6 && result.Kkt.MinUpperMultiplier >= -1e-6);
    }

    [Fact]
    public void Finite_difference_and_adjoint_optimisers_reach_the_same_optimum()
    {
        var dynamics = new FullOrderThermalDynamics(Solver());
        var options = new CoolingOptimizationOptions(SafeTemperature: 38, Segments: 4, SegmentDuration: 150, MaxIterationsPerStage: 30) with { };
        var shortLoad = Load;
        var guess = Enumerable.Repeat(0.3, 4).ToArray();
        var adjoint = new AdjointCoolingOptimizer(dynamics, shortLoad, Cooling).Optimize(Grid.CreateField(25), Source(), 0, options, guess);
        var fd = new AdjointCoolingOptimizer(dynamics, shortLoad, Cooling, new PdeOptimizerSettings(GradientMethod.FiniteDifference))
            .Optimize(Grid.CreateField(25), Source(), 0, options, guess);

        Assert.True(Math.Abs(adjoint.Objective - fd.Objective) / adjoint.Objective < 0.02, $"{adjoint.Objective:E4} vs {fd.Objective:E4}");
        Assert.True(fd.Evaluations > adjoint.Evaluations, "finite differences need more forward solves");
        Assert.Equal(0, fd.AdjointSolves);
    }

    [Fact]
    public void Kkt_diagnostics_classify_active_bounds()
    {
        var objective = Objective(new FullOrderThermalDynamics(Solver()), safe: 500);
        var kkt = AdjointCoolingOptimizer.Diagnose(objective, [0, 0, 1, 0.5, 0.5, 0.5], 1e4);
        Assert.Equal(2, kkt.ActiveLower);
        Assert.Equal(1, kkt.ActiveUpper);
        Assert.Equal(3, kkt.Inactive);
        Assert.Equal(0, kkt.StateActivePoints);
    }

    [Fact]
    public void Segment_duration_must_be_a_multiple_of_the_time_step()
    {
        Assert.Throws<ArgumentException>(() => new PdeConstrainedObjective(new FullOrderThermalDynamics(Solver()), Load, Cooling,
            Grid.CreateField(25), Source(), 0, new ControlProblemSettings(40, 3, 15, 0.02)));
    }
}
