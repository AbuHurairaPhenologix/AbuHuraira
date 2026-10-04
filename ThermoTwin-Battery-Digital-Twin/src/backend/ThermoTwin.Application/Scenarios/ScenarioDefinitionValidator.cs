using FluentValidation;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Pde;

namespace ThermoTwin.Application.Scenarios;

/// <summary>
/// Validates scenario input, including the numerical admissibility of the configuration:
/// grid resolution, solver stability (explicit Euler must satisfy Δt ≤ Δt_crit) and
/// identifiability limits of the inverse problem.
/// </summary>
public sealed class ScenarioDefinitionValidator : AbstractValidator<ScenarioDefinition>
{
    public ScenarioDefinitionValidator()
    {
        RuleFor(s => s.Name).NotEmpty().MaximumLength(120);

        RuleFor(s => s.Geometry.LengthX).InclusiveBetween(0.02, 2.0).WithName("Geometry.LengthX");
        RuleFor(s => s.Geometry.LengthY).InclusiveBetween(0.02, 2.0).WithName("Geometry.LengthY");
        RuleFor(s => s.Geometry.Nx).InclusiveBetween(8, 120).WithName("Geometry.Nx");
        RuleFor(s => s.Geometry.Ny).InclusiveBetween(4, 60).WithName("Geometry.Ny");
        RuleFor(s => s.Geometry.PlantRefinement).InclusiveBetween(1, 3).WithName("Geometry.PlantRefinement");

        RuleFor(s => s.Material.Density).InclusiveBetween(100, 20_000).WithName("Material.Density");
        RuleFor(s => s.Material.SpecificHeat).InclusiveBetween(100, 5_000).WithName("Material.SpecificHeat");
        RuleFor(s => s.Material.Conductivity).InclusiveBetween(0.1, 400).WithName("Material.Conductivity");
        RuleFor(s => s.Material.Thickness).InclusiveBetween(0.001, 0.1).WithName("Material.Thickness");

        RuleFor(s => s.Environment.AmbientTemperature).InclusiveBetween(-40, 80).WithName("Environment.AmbientTemperature");
        RuleFor(s => s.Environment.InitialTemperature).InclusiveBetween(-40, 80).WithName("Environment.InitialTemperature");
        RuleFor(s => s.Environment.EdgeHeatTransferCoefficient).InclusiveBetween(0, 1_000).WithName("Environment.EdgeHeatTransferCoefficient");

        RuleFor(s => s.Cooling.CoolantTemperature).InclusiveBetween(-20, 60).WithName("Cooling.CoolantTemperature");
        RuleFor(s => s.Cooling.MinHeatTransferCoefficient).GreaterThanOrEqualTo(0).WithName("Cooling.MinHeatTransferCoefficient");
        RuleFor(s => s.Cooling.MaxHeatTransferCoefficient)
            .GreaterThanOrEqualTo(s => s.Cooling.MinHeatTransferCoefficient)
            .LessThanOrEqualTo(5_000)
            .WithName("Cooling.MaxHeatTransferCoefficient");
        RuleFor(s => s.Cooling.RatedPowerWatts).InclusiveBetween(0, 10_000).WithName("Cooling.RatedPowerWatts");
        RuleFor(s => s.Cooling.BaselineLevel).InclusiveBetween(0, 1).WithName("Cooling.BaselineLevel");

        RuleFor(s => s.HeatSource.UniformJouleHeating).InclusiveBetween(0, 1e7).WithName("HeatSource.UniformJouleHeating");
        RuleFor(s => s.HeatSource.Hotspots.Count).LessThanOrEqualTo(5).WithName("HeatSource.Hotspots");
        RuleForEach(s => s.HeatSource.Hotspots).ChildRules(h =>
        {
            h.RuleFor(x => x.PeakPower).InclusiveBetween(0, 1e8);
            h.RuleFor(x => x.Radius).InclusiveBetween(0.002, 0.2);
        }).WithName("HeatSource.Hotspots");
        RuleForEach(s => s.HeatSource.Hotspots)
            .Must((s, h) => h.X >= 0 && h.X <= s.Geometry.LengthX && h.Y >= 0 && h.Y <= s.Geometry.LengthY)
            .WithMessage("Hotspot centres must lie inside the cell.")
            .WithName("HeatSource.Hotspots");
        RuleFor(s => s.HeatSource.TaperFactor).InclusiveBetween(0, 1).WithName("HeatSource.TaperFactor");

        RuleFor(s => s.Sensors.Count).InclusiveBetween(1, 64).WithName("Sensors.Count");
        RuleFor(s => s.Sensors.NoiseStd).InclusiveBetween(0, 5).WithName("Sensors.NoiseStd");

        RuleFor(s => s.Solver.TimeStep).InclusiveBetween(0.01, 60).WithName("Solver.TimeStep");
        RuleFor(s => s.Solver.Duration).InclusiveBetween(10, 7_200).WithName("Solver.Duration");
        RuleFor(s => s.Solver.Duration / s.Solver.TimeStep).LessThanOrEqualTo(20_000)
            .WithName("Solver.Duration").WithMessage("Too many time steps (Duration / TimeStep must be ≤ 20 000).");
        RuleFor(s => s)
            .Must(BeStableForExplicitScheme)
            .When(s => s.Solver.Scheme == TimeScheme.ExplicitEuler)
            .WithName("Solver.TimeStep")
            .WithMessage(s => $"Explicit Euler is unstable for Δt = {s.Solver.TimeStep} s on this grid (Δt must be ≤ {ExplicitLimit(s):0.###} s). Reduce the time step or choose Crank–Nicolson.");

        RuleFor(s => s.Estimator.BasisNodesX).InclusiveBetween(2, 30).WithName("Estimator.BasisNodesX");
        RuleFor(s => s.Estimator.BasisNodesY).InclusiveBetween(2, 20).WithName("Estimator.BasisNodesY");
        RuleFor(s => s.Estimator.FixedLambda).GreaterThan(0).WithName("Estimator.FixedLambda");
        RuleFor(s => s.Estimator.EstimationInterval)
            .GreaterThanOrEqualTo(s => s.Solver.TimeStep).WithName("Estimator.EstimationInterval");

        RuleFor(s => s.Control.CriticalTemperature)
            .GreaterThan(s => s.Control.SafeTemperature).WithName("Control.CriticalTemperature");
        RuleFor(s => s.Control.ControlMargin).InclusiveBetween(0, 10).WithName("Control.ControlMargin");
        RuleFor(s => s.Control.HorizonSegments).InclusiveBetween(1, 24).WithName("Control.HorizonSegments");
        RuleFor(s => s.Control.SegmentDuration).InclusiveBetween(10, 1_800).WithName("Control.SegmentDuration");
        RuleFor(s => s.Control.PredictionTimeStep).InclusiveBetween(0.5, 60).WithName("Control.PredictionTimeStep");
        RuleFor(s => s.Control.ControlInterval).GreaterThanOrEqualTo(s => s.Solver.TimeStep).WithName("Control.ControlInterval");
        RuleFor(s => s.Control.Optimizer).IsInEnum().WithName("Control.Optimizer");
        RuleFor(s => s.Control.RomModes).InclusiveBetween(2, 80).WithName("Control.RomModes");
        RuleFor(s => s.Control.RomValidationThreshold).InclusiveBetween(0.01, 5).WithName("Control.RomValidationThreshold");
        RuleFor(s => s.Control)
            .Must(c => IsMultiple(c.SegmentDuration, c.PredictionTimeStep))
            .When(s => s.Control.Optimizer != CoolingOptimizerKind.PenaltyFiniteDifference)
            .OverridePropertyName("Control.SegmentDuration")
            .WithMessage("For the PDE-constrained optimiser the segment duration must be a multiple of the prediction time step.");

        RuleFor(s => s.Playback.StepsPerFrame).InclusiveBetween(1, 50).WithName("Playback.StepsPerFrame");
        RuleFor(s => s.Playback.FrameIntervalMs).InclusiveBetween(0, 5_000).WithName("Playback.FrameIntervalMs");
    }

    private static bool BeStableForExplicitScheme(ScenarioDefinition s) => s.Solver.TimeStep <= ExplicitLimit(s);

    private static bool IsMultiple(double value, double step) =>
        step > 0 && Math.Abs(value / step - Math.Round(value / step)) < 1e-9 && value >= step;

    /// <summary>Explicit stability limit on the (finer) plant grid, which is the binding constraint.</summary>
    private static double ExplicitLimit(ScenarioDefinition s)
    {
        if (s.Geometry.Nx is < 2 or > 120 || s.Geometry.Ny is < 2 or > 60 || s.Geometry.LengthX <= 0 || s.Geometry.LengthY <= 0
            || s.Geometry.PlantRefinement is < 1 or > 3 || s.Material.Density <= 0 || s.Material.SpecificHeat <= 0 || s.Material.Conductivity <= 0
            || s.Material.Thickness <= 0)
        {
            return double.PositiveInfinity;
        }

        var factory = new ScenarioFactory(s);
        var plantGrid = new Grid2D(s.Geometry.Nx * s.Geometry.PlantRefinement, s.Geometry.Ny * s.Geometry.PlantRefinement,
            s.Geometry.LengthX, s.Geometry.LengthY);
        var solver = new HeatEquationSolver(factory.ThermalModel().WithGrid(plantGrid), TimeScheme.ExplicitEuler, s.Solver.TimeStep);
        return StabilityAnalyzer.Analyze(solver, 1).ExplicitCriticalTimeStep;
    }
}
