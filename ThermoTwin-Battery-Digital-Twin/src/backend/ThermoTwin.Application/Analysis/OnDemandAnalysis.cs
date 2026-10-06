using System.Diagnostics;
using FluentValidation;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Numerics.Fem;
using ThermoTwin.Numerics.LinearAlgebra;
using ThermoTwin.Numerics.Optimization;

namespace ThermoTwin.Application.Analysis;

/// <summary>Request for a P1 triangulation of the cell.</summary>
public sealed record FemMeshRequest(int Nx = 10, int Ny = 5, double LengthX = 0.2, double LengthY = 0.1);

/// <summary>Request for an on-demand discrete-adjoint gradient check on the demo cell.</summary>
/// <param name="Segments">K — number of piecewise-constant controls.</param>
/// <param name="SegmentDuration">Length of each segment [s]; a multiple of the 10 s prediction step.</param>
/// <param name="Mu">Penalty parameter μ of the state constraint.</param>
/// <param name="Epsilon">Central finite-difference step ε.</param>
/// <param name="Seed">Seed of the random control vector u ∈ [0.05, 0.55]^K.</param>
public sealed record GradientCheckRequest(int Segments = 6, double SegmentDuration = 100, double Mu = 1e4, double Epsilon = 1e-6, int Seed = 1);

public sealed record GradientCheckResponse(
    int Segments,
    int StateDimension,
    int TimeSteps,
    double[] Controls,
    double[] AdjointGradient,
    double[] FiniteDifferenceGradient,
    double RelativeError,
    double Objective,
    double PeakTemperature,
    double AdjointMs,
    double FiniteDifferenceMs,
    int AdjointSolves,
    int FiniteDifferenceSolves);

public sealed class FemMeshRequestValidator : AbstractValidator<FemMeshRequest>
{
    public FemMeshRequestValidator()
    {
        RuleFor(r => r.Nx).InclusiveBetween(1, 80);
        RuleFor(r => r.Ny).InclusiveBetween(1, 40);
        RuleFor(r => r.LengthX).InclusiveBetween(0.01, 2);
        RuleFor(r => r.LengthY).InclusiveBetween(0.01, 2);
    }
}

public sealed class GradientCheckRequestValidator : AbstractValidator<GradientCheckRequest>
{
    public GradientCheckRequestValidator()
    {
        RuleFor(r => r.Segments).InclusiveBetween(1, 36);
        RuleFor(r => r.SegmentDuration).InclusiveBetween(10, 600)
            .Must(d => Math.Abs(d / 10 - Math.Round(d / 10)) < 1e-9).WithMessage("SegmentDuration must be a multiple of the 10 s prediction time step.");
        RuleFor(r => r.Segments * r.SegmentDuration).LessThanOrEqualTo(3600).WithName("Horizon").WithMessage("Segments × SegmentDuration must not exceed 3600 s.");
        RuleFor(r => r.Mu).GreaterThan(0).LessThanOrEqualTo(1e8);
        RuleFor(r => r.Epsilon).InclusiveBetween(1e-9, 1e-1);
    }
}

/// <summary>Small numerical analyses computed synchronously for the API (the heavy studies run as experiments).</summary>
public sealed class OnDemandAnalysis
{
    private readonly IValidator<FemMeshRequest> _meshValidator;
    private readonly IValidator<GradientCheckRequest> _gradientValidator;

    public OnDemandAnalysis(IValidator<FemMeshRequest> meshValidator, IValidator<GradientCheckRequest> gradientValidator)
    {
        _meshValidator = meshValidator;
        _gradientValidator = gradientValidator;
    }

    public FemMeshDto FemMesh(FemMeshRequest request)
    {
        _meshValidator.ValidateAndThrow(request);
        var mesh = new FemMesh2D(request.Nx, request.Ny, request.LengthX, request.LengthY);
        return new FemMeshDto(mesh.Nx, mesh.Ny, mesh.LengthX, mesh.LengthY, mesh.NodeCount, mesh.ElementCount, mesh.BoundarySegments.Count,
            mesh.Nodes.Select(n => new[] { Math.Round(n.X, 6), Math.Round(n.Y, 6) }).ToArray(),
            mesh.Elements.Select(e => e.Nodes.ToArray()).ToArray());
    }

    public GradientCheckResponse GradientCheck(GradientCheckRequest request, ScenarioDefinition? scenario = null)
    {
        _gradientValidator.ValidateAndThrow(request);
        var factory = new ScenarioFactory(scenario ?? ScenarioCatalog.RapidChargeHiddenHotspot);
        var solver = factory.PredictionSolver();
        var dynamics = new FullOrderThermalDynamics(solver);
        var grid = solver.Model.Grid;
        var options = factory.OptimizationOptions(request.Segments, request.SegmentDuration);
        var objective = new PdeConstrainedObjective(dynamics, factory.LoadProfile(), factory.Cooling(),
            grid.CreateField(factory.Scenario.Environment.InitialTemperature), factory.HeatSource().SpatialField(grid), 0,
            new ControlProblemSettings(options.SafeTemperature, request.Segments, request.SegmentDuration, options.SmoothnessWeight));

        var rng = new Random(request.Seed);
        var u = Enumerable.Range(0, request.Segments).Select(_ => 0.05 + 0.5 * rng.NextDouble()).ToArray();
        var sw = Stopwatch.StartNew();
        var evaluation = objective.EvaluateWithGradient(u, request.Mu);
        var adjointMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var fd = objective.FiniteDifferenceGradient(u, request.Mu, request.Epsilon, central: true);
        var fdMs = sw.Elapsed.TotalMilliseconds;
        var adjoint = evaluation.Gradient!;

        return new GradientCheckResponse(request.Segments, dynamics.StateDimension, objective.Steps, u, adjoint, fd,
            Vector.Norm2(Vector.Subtract(adjoint, fd)) / Math.Max(Vector.Norm2(fd), 1e-300), evaluation.Value, evaluation.PeakTemperature,
            Math.Round(adjointMs, 2), Math.Round(fdMs, 2), 2, 2 * request.Segments);
    }
}
