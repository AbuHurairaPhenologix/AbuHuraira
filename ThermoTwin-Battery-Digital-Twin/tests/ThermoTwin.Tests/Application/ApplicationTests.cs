using ThermoTwin.Application.Experiments;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;
using ThermoTwin.Domain.Services;
using ThermoTwin.Numerics.Pde;

namespace ThermoTwin.Tests.Application;

public static class TestScenarios
{
    /// <summary>Small, fast version of the demo (20×10 grid, 2 minutes) for tests.</summary>
    public static ScenarioDefinition Quick { get; } = ScenarioCatalog.RapidChargeHiddenHotspot with
    {
        Key = "test-quick",
        Name = "Quick test scenario",
        Geometry = new GeometrySettings(Nx: 20, Ny: 10),
        Solver = new SolverSettings(TimeStep: 5, Duration: 180),
        Estimator = new EstimatorSettings(BasisNodesX: 7, BasisNodesY: 4, EstimationInterval: 30),
        Control = new ControlSettings(HorizonSegments: 3, ControlInterval: 60),
        Playback = new PlaybackSettings(StepsPerFrame: 6, FrameIntervalMs: 0),
    };
}

public sealed class ApplicationTests
{
    [Fact]
    public void Built_in_scenarios_are_valid()
    {
        var validator = new ScenarioDefinitionValidator();
        foreach (var scenario in ScenarioCatalog.All)
        {
            var result = validator.Validate(scenario);
            Assert.True(result.IsValid, string.Join("; ", result.Errors));
        }
    }

    [Fact]
    public void Validator_rejects_unstable_explicit_time_step()
    {
        var scenario = ScenarioCatalog.RapidChargeHiddenHotspot with { Solver = new SolverSettings(TimeScheme.ExplicitEuler, 5, 1800) };
        var result = new ScenarioDefinitionValidator().Validate(scenario);
        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.ErrorMessage.Contains("unstable", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validator_accepts_explicit_scheme_with_small_time_step()
    {
        var scenario = TestScenarios.Quick with { Solver = new SolverSettings(TimeScheme.ExplicitEuler, 0.5, 60) };
        Assert.True(new ScenarioDefinitionValidator().Validate(scenario).IsValid);
    }

    [Fact]
    public void Validator_rejects_out_of_range_physics()
    {
        var scenario = ScenarioCatalog.RapidChargeHiddenHotspot with
        {
            Sensors = new SensorSettings(Count: 0, NoiseStd: -1),
            Control = new ControlSettings(SafeTemperature: 60, CriticalTemperature: 50),
        };
        var result = new ScenarioDefinitionValidator().Validate(scenario);
        Assert.Contains(result.Errors, e => e.PropertyName == "Sensors.Count");
        Assert.Contains(result.Errors, e => e.PropertyName == "Sensors.NoiseStd");
        Assert.Contains(result.Errors, e => e.PropertyName == "Control.CriticalTemperature");
    }

    [Theory]
    [InlineData(35, 36, ThermalRisk.Normal)]
    [InlineData(41, 42, ThermalRisk.Elevated)]
    [InlineData(43, 46, ThermalRisk.Warning)]
    [InlineData(45.5, 46, ThermalRisk.Critical)]
    [InlineData(40, 56, ThermalRisk.Critical)]
    public void Risk_policy_classifies_current_and_forecast_state(double current, double forecast, ThermalRisk expected) =>
        Assert.Equal(expected, ThermalRiskPolicy.Classify(current, forecast, 45, 55));

    [Fact]
    public void Simulation_run_enforces_its_lifecycle()
    {
        var run = new SimulationRun("demo", "key", "{}", DateTimeOffset.UnixEpoch);
        Assert.Throws<DomainException>(run.Pause);
        run.Start(DateTimeOffset.UnixEpoch);
        run.Pause();
        Assert.Equal(SimulationStatus.Paused, run.Status);
        run.Resume();
        run.RecordProgress(100, 40, 42, 10);
        run.RecordProgress(200, 39, 43, 20);
        Assert.Equal(40, run.PeakTemperature);
        Assert.Equal(43, run.PeakCounterfactualTemperature);
        run.Complete(DateTimeOffset.UnixEpoch, 0.1, 0.4, 3, stoppedEarly: false);
        Assert.Equal(SimulationStatus.Completed, run.Status);
        Assert.Throws<DomainException>(() => run.Start(DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Digital_twin_engine_estimates_predicts_and_controls()
    {
        var engine = new DigitalTwinEngine(TestScenarios.Quick, Guid.NewGuid());
        while (!engine.IsComplete)
        {
            engine.Advance();
        }

        var frame = engine.BuildFrame(SimulationStatus.Completed);
        Assert.Equal(36, engine.History.Count);
        Assert.NotNull(frame.Estimation);
        Assert.NotNull(frame.Forecast);
        Assert.NotNull(frame.Cooling.RecommendedPlan);
        Assert.True(frame.Estimation!.TemperatureRmse < 0.5, $"RMSE {frame.Estimation.TemperatureRmse}");
        Assert.True(frame.Truth.Max > 25);
        Assert.Contains("estimatedSource", frame.Fields!.Keys);
        Assert.Contains("predictedTemperature", frame.Fields.Keys);
        Assert.Equal(36, frame.NewHistory.Count);
        Assert.Contains(engine.History, h => h.LeadPrediction is null);
    }

    [Fact]
    public void Experiment_service_executes_forecast_experiment_on_quick_scenario()
    {
        var (payload, summary) = ExperimentService.Execute(ExperimentKind.ForecastAccuracy, TestScenarios.Quick with
        {
            Solver = new SolverSettings(TimeStep: 5, Duration: 900),
        });
        var result = Assert.IsType<ForecastExperimentResult>(payload);
        Assert.NotEmpty(result.Forecasts);
        Assert.All(result.Forecasts, f => Assert.True(f.MeanAbsoluteError < 3));
        Assert.Contains("MAE", summary);
    }
}
