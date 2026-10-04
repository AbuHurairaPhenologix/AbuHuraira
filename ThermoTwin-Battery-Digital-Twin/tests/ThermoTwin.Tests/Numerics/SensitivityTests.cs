using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Physics;
using ThermoTwin.Numerics.Sensitivity;
using ThermoTwin.Numerics.Sensors;

namespace ThermoTwin.Tests.Numerics;

public sealed class SensitivityTests
{
    private static readonly Grid2D Grid = new(16, 8, 0.2, 0.1);

    private static readonly ParameterSpec[] Specs =
    [
        new(ThermalParameter.Conductivity, "k", "W/(m·K)", 20, 4, 5, 60, 0.02),
        new(ThermalParameter.EdgeHeatTransfer, "h_e", "W/(m²·K)", 10, 5, 0.5, 50, 0.01),
        new(ThermalParameter.DefectPower, "Q", "W/m³", 450_000, 150_000, 0, 2e6, 450),
        new(ThermalParameter.DefectX, "x₀", "m", 0.13, 0.01, 0.005, 0.195, 1e-4),
        new(ThermalParameter.DefectY, "y₀", "m", 0.06, 0.01, 0.005, 0.095, 1e-4),
    ];

    private static SensorResponseModel Model() => new(
        new ThermalModel(Grid, MaterialProperties.LithiumIonPouchCell, BoundaryConditions.All(BoundaryCondition.Convective(25, 10)), new CoolingModel(22, 5, 120, 150)),
        SensorNetwork.Layout(0.2, 0.1, 12), LoadProfile.Constant(), 55_000, 0.014, 0.15, 25, 25, 600, 10, 60);

    private static Dictionary<ThermalParameter, double> Nominal() => Specs.ToDictionary(s => s.Parameter, s => s.Nominal);

    [Fact]
    public void Observation_vector_has_samples_times_sensors_entries_and_is_reproducible()
    {
        var model = Model();
        Assert.Equal(10, model.SampleTimes.Length);
        var y1 = model.Simulate(Nominal(), noiseStd: 0.1, seed: 3);
        var y2 = model.Simulate(Nominal(), noiseStd: 0.1, seed: 3);
        Assert.Equal(model.ObservationCount, y1.Length);
        Assert.Equal(y1, y2);
        Assert.NotEqual(y1, model.Simulate(Nominal(), noiseStd: 0.1, seed: 4));
    }

    [Fact]
    public void Sensitivity_to_the_defect_power_matches_the_exact_linear_response()
    {
        // y is affine in Q, so ∂y/∂Q = (y(Q₀) − y(0)) / Q₀ exactly; central differences must reproduce it.
        var model = Model();
        var theta = Nominal();
        var column = IdentifiabilityAnalysis.CentralDifference(model, theta, Specs[2], 450);
        var zero = new Dictionary<ThermalParameter, double>(theta) { [ThermalParameter.DefectPower] = 0 };
        var exact = Vector.Subtract(model.Simulate(theta), model.Simulate(zero)).Select(v => v / 450_000).ToArray();
        Assert.True(Vector.Norm2(Vector.Subtract(column, exact)) / Vector.Norm2(exact) < 1e-7);
    }

    [Fact]
    public void Finite_difference_sensitivities_are_step_size_consistent()
    {
        var model = Model();
        var a = IdentifiabilityAnalysis.CentralDifference(model, Nominal(), Specs[0], 0.02);
        var b = IdentifiabilityAnalysis.CentralDifference(model, Nominal(), Specs[0], 0.01);
        Assert.True(Vector.Norm2(Vector.Subtract(a, b)) / Vector.Norm2(a) < 1e-4);
    }

    [Fact]
    public void Identifiability_report_has_consistent_dimensions_and_is_deterministic()
    {
        var model = Model();
        var report = IdentifiabilityAnalysis.Analyze(model, Specs, 0.1);
        Assert.Equal(5, report.Parameters.Length);
        Assert.Equal(model.ObservationCount, report.ScaledSensitivity.Length);
        Assert.All(report.ScaledSensitivity, row => Assert.Equal(5, row.Length));
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(1, report.Correlation[i][i], 12);
            Assert.Equal(1, report.ParameterCorrelation[i][i], 9);
            for (var j = 0; j < 5; j++)
            {
                Assert.Equal(report.Correlation[i][j], report.Correlation[j][i], 12);
                Assert.InRange(report.Correlation[i][j], -1 - 1e-12, 1 + 1e-12);
            }

            Assert.True(report.CramerRaoStd[i] > 0);
        }

        Assert.Equal(26, report.Collinearity.Count); // all subsets of size ≥ 2 of five parameters
        Assert.True(report.ConditionNumber >= 1);
        var again = IdentifiabilityAnalysis.Analyze(model, Specs, 0.1);
        Assert.Equal(report.CramerRaoStd, again.CramerRaoStd);
    }

    [Fact]
    public void Levenberg_marquardt_recovers_parameters_from_exact_model_data()
    {
        var model = Model();
        var truth = Nominal();
        var data = model.Simulate(truth);
        var start = new Dictionary<ThermalParameter, double>(truth)
        {
            [ThermalParameter.DefectPower] = 300_000,
            [ThermalParameter.DefectX] = 0.115,
            [ThermalParameter.DefectY] = 0.05,
        };
        var result = BoundedLevenbergMarquardt.Estimate(model, Specs, [ThermalParameter.DefectPower, ThermalParameter.DefectX, ThermalParameter.DefectY],
            data, start, truth);

        Assert.All(result.RelativeErrors, e => Assert.True(Math.Abs(e) < 1e-3, $"relative error {e:E2}"));
        Assert.True(result.FinalResidual < 1e-3 * result.InitialResidual);
        Assert.All(result.AtBound, b => Assert.False(b));
    }

    [Fact]
    public void Estimation_respects_the_parameter_bounds()
    {
        var model = Model();
        var truth = Nominal();
        var data = model.Simulate(new Dictionary<ThermalParameter, double>(truth) { [ThermalParameter.DefectPower] = 3e6 });
        var narrow = Specs.Select(s => s.Parameter == ThermalParameter.DefectPower ? s with { Upper = 1e6 } : s).ToArray();
        var result = BoundedLevenbergMarquardt.Estimate(model, narrow, [ThermalParameter.DefectPower], data, truth, truth);
        Assert.True(result.Estimate[0] <= 1e6 + 1e-6);
        Assert.True(result.AtBound[0]);
    }
}
