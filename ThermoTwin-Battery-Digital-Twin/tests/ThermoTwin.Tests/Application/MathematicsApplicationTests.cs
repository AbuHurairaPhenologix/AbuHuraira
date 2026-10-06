using System.Text.Json;
using FluentValidation;
using ThermoTwin.Application;
using ThermoTwin.Application.Analysis;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Enums;

namespace ThermoTwin.Tests.Application;

public sealed class MathematicsApplicationTests
{
    [Theory]
    [InlineData(CoolingOptimizerKind.AdjointFullOrder)]
    [InlineData(CoolingOptimizerKind.AdjointReducedOrder)]
    [InlineData(CoolingOptimizerKind.PenaltyFiniteDifference)]
    public void Live_twin_runs_mpc_with_every_optimiser(CoolingOptimizerKind kind)
    {
        var scenario = TestScenarios.Quick with { Control = TestScenarios.Quick.Control with { Optimizer = kind, RomModes = 12 } };
        Assert.True(new ScenarioDefinitionValidator().Validate(scenario).IsValid);
        var engine = new DigitalTwinEngine(scenario, Guid.NewGuid());
        while (!engine.IsComplete)
        {
            engine.Advance();
        }

        Assert.True(engine.Statistics.Optimizations > 0);
        var frame = engine.BuildFrame(SimulationStatus.Completed, includeFields: false);
        Assert.Equal(kind.ToString(), frame.Cooling.Optimizer);
        if (kind == CoolingOptimizerKind.PenaltyFiniteDifference)
        {
            Assert.Equal(0, engine.Statistics.AdjointSolves);
        }
        else
        {
            Assert.True(engine.Statistics.AdjointSolves > 0);
        }

        if (kind == CoolingOptimizerKind.AdjointReducedOrder)
        {
            Assert.NotNull(frame.Cooling.RomValidationError);
        }
    }

    [Fact]
    public void Validator_rejects_invalid_reduced_order_settings()
    {
        var validator = new ScenarioDefinitionValidator();
        var badModes = ScenarioCatalog.RapidChargeHiddenHotspot with { Control = new ControlSettings(RomModes: 1) };
        var badThreshold = ScenarioCatalog.RapidChargeHiddenHotspot with { Control = new ControlSettings(RomValidationThreshold: 0) };
        var badSegment = ScenarioCatalog.RapidChargeHiddenHotspot with { Control = new ControlSettings(SegmentDuration: 105) };
        Assert.Contains(validator.Validate(badModes).Errors, e => e.PropertyName == "Control.RomModes");
        Assert.Contains(validator.Validate(badThreshold).Errors, e => e.PropertyName == "Control.RomValidationThreshold");
        Assert.Contains(validator.Validate(badSegment).Errors, e => e.PropertyName == "Control.SegmentDuration");

        // The legacy optimiser does not require segment/time-step alignment.
        var legacy = badSegment with { Control = badSegment.Control with { Optimizer = CoolingOptimizerKind.PenaltyFiniteDifference } };
        Assert.True(validator.Validate(legacy).IsValid);
    }

    [Fact]
    public void Adjoint_gradient_check_experiment_runs_and_serialises()
    {
        var (payload, summary) = ExperimentService.Execute(ExperimentKind.AdjointGradientCheck, TestScenarios.Quick);
        var result = Assert.IsType<AdjointCheckResult>(payload);
        Assert.True(result.BestRelativeError < 1e-5, $"best relative error {result.BestRelativeError:E2}");
        Assert.True(result.RomBestRelativeError < 1e-4);
        Assert.NotEmpty(result.Cost);
        Assert.All(result.Cost, c => Assert.Equal(2, c.AdjointSolves));
        Assert.Contains("Adjoint", summary, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options));
        Assert.True(json.RootElement.GetProperty("rows").GetArrayLength() > 0);
    }

    [Fact]
    public void Identifiability_experiment_runs_and_serialises()
    {
        var (payload, _) = ExperimentService.Execute(ExperimentKind.ParameterIdentifiability, TestScenarios.Quick);
        var result = Assert.IsType<IdentifiabilityResult>(payload);
        Assert.Equal(5, result.Report.Parameters.Length);
        Assert.Equal(5, result.Traces.Count);
        Assert.Equal(3, result.Estimations.Count);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(payload, payload.GetType(), JsonDefaults.Options));
        Assert.Equal(5, json.RootElement.GetProperty("report").GetProperty("correlation").GetArrayLength());
    }

    [Fact]
    public void Every_experiment_kind_has_a_description()
    {
        foreach (var kind in Enum.GetValues<ExperimentKind>())
        {
            Assert.NotEqual(kind.ToString(), ExperimentRunner.Describe(kind));
        }
    }

    [Fact]
    public void On_demand_analysis_validates_its_input()
    {
        var analysis = new OnDemandAnalysis(new FemMeshRequestValidator(), new GradientCheckRequestValidator());
        var mesh = analysis.FemMesh(new FemMeshRequest(4, 2));
        Assert.Equal(15, mesh.Nodes);
        Assert.Equal(16, mesh.Triangles.Length);
        Assert.Throws<ValidationException>(() => analysis.FemMesh(new FemMeshRequest(0, 2)));
        Assert.Throws<ValidationException>(() => analysis.GradientCheck(new GradientCheckRequest(SegmentDuration: 15)));
        Assert.Throws<ValidationException>(() => analysis.GradientCheck(new GradientCheckRequest(Segments: 40)));

        var check = analysis.GradientCheck(new GradientCheckRequest(Segments: 3, SegmentDuration: 100));
        Assert.True(check.RelativeError < 1e-4);
        Assert.Equal(3, check.AdjointGradient.Length);
    }
}
