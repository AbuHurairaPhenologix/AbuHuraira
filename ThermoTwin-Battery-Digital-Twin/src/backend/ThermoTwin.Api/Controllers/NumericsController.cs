using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using ThermoTwin.Application.Analysis;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Numerics.Grid;
using ThermoTwin.Numerics.Pde;

namespace ThermoTwin.Api.Controllers;

public sealed record GridStabilityDto(string Grid, int Nx, int Ny, double Dx, double Dy, IReadOnlyList<StabilityReport> Schemes);

public sealed record StabilityAnalysisDto(double Diffusivity, double DiffusionTimeScale, double TimeStep, IReadOnlyList<GridStabilityDto> Grids, bool Valid, IReadOnlyList<string> Issues);

/// <summary>On-demand numerical analysis of a configuration.</summary>
[ApiController]
[Route("api/numerics")]
public sealed class NumericsController : ControllerBase
{
    private readonly IValidator<ScenarioDefinition> _validator;

    public NumericsController(IValidator<ScenarioDefinition> validator) => _validator = validator;

    /// <summary>
    /// Stability analysis of every time scheme for the scenario's model and plant grids
    /// (Fourier number, Gershgorin and power-iteration spectral radius, amplification factor).
    /// </summary>
    [HttpPost("stability")]
    public async Task<ActionResult<StabilityAnalysisDto>> Stability(ScenarioDefinition scenario, CancellationToken cancellationToken)
    {
        var validation = await _validator.ValidateAsync(scenario, cancellationToken);
        var issues = validation.Errors.Select(e => e.ErrorMessage).ToList();
        var factory = new ScenarioFactory(scenario);
        var g = scenario.Geometry;
        if (g.Nx < 2 || g.Ny < 2 || g.LengthX <= 0 || g.LengthY <= 0 || scenario.Solver.TimeStep <= 0 || g.Nx * g.Ny > 20_000)
        {
            return Ok(new StabilityAnalysisDto(0, 0, scenario.Solver.TimeStep, [], false, issues));
        }

        var model = factory.ThermalModel();
        var grids = new List<GridStabilityDto>();
        foreach (var (label, grid) in new[]
                 {
                     ("Model grid (digital twin)", model.Grid),
                     ("Plant grid (synthetic truth)", new Grid2D(g.Nx * Math.Max(1, g.PlantRefinement), g.Ny * Math.Max(1, g.PlantRefinement), g.LengthX, g.LengthY)),
                 })
        {
            var reports = Enum.GetValues<TimeScheme>()
                .Select(s => StabilityAnalyzer.Analyze(new HeatEquationSolver(model.WithGrid(grid), s, scenario.Solver.TimeStep), 1))
                .ToArray();
            grids.Add(new GridStabilityDto(label, grid.Nx, grid.Ny, grid.Dx, grid.Dy, reports));
        }

        var alpha = model.Material.Diffusivity;
        return Ok(new StabilityAnalysisDto(alpha, g.LengthY * g.LengthY / alpha, scenario.Solver.TimeStep, grids, validation.IsValid, issues));
    }

    /// <summary>
    /// Structured P1 triangulation of the cell (nodes, counter-clockwise triangles, boundary segments) — the mesh used
    /// by the finite-element solver, for visualisation.
    /// </summary>
    [HttpGet("fem/mesh")]
    [ProducesResponseType<FemMeshDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<FemMeshDto> FemMesh([FromQuery] FemMeshRequest request, [FromServices] OnDemandAnalysis analysis) =>
        Ok(analysis.FemMesh(request));

    /// <summary>
    /// Discrete adjoint gradient of the PDE-constrained cooling objective versus central finite differences for a random
    /// control vector on the demo cell (one forward + one backward sweep vs 2K forward solves).
    /// </summary>
    [HttpPost("adjoint/gradient-check")]
    [ProducesResponseType<GradientCheckResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public ActionResult<GradientCheckResponse> GradientCheck(GradientCheckRequest request, [FromServices] OnDemandAnalysis analysis) =>
        Ok(analysis.GradientCheck(request));
}
