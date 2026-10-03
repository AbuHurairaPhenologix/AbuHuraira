using FluentValidation;
using Microsoft.AspNetCore.Mvc;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Scenarios;
using ThermoTwin.Domain.Entities;
using ThermoTwin.SimulationWorker;

namespace ThermoTwin.Api.Controllers;

/// <param name="ScenarioKey">Key of a built-in scenario (used when <paramref name="Scenario"/> is omitted).</param>
/// <param name="Scenario">Full custom scenario definition.</param>
/// <param name="Name">Optional display name of the run.</param>
public sealed record StartSimulationRequest(string? ScenarioKey, ScenarioDefinition? Scenario, string? Name);

/// <summary>Simulation runs (history and creation of the live session).</summary>
[ApiController]
[Route("api/simulations")]
public sealed class SimulationsController : ControllerBase
{
    private readonly SimulationCoordinator _coordinator;
    private readonly ISimulationRunRepository _runs;
    private readonly IValidator<ScenarioDefinition> _validator;

    public SimulationsController(SimulationCoordinator coordinator, ISimulationRunRepository runs, IValidator<ScenarioDefinition> validator)
    {
        _coordinator = coordinator;
        _runs = runs;
        _validator = validator;
    }

    /// <summary>Validates a scenario and starts it as the live digital twin (replacing any active run).</summary>
    [HttpPost]
    [ProducesResponseType<SimulationRunDto>(StatusCodes.Status201Created)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SimulationRunDto>> Start(StartSimulationRequest request, CancellationToken cancellationToken)
    {
        var scenario = request.Scenario
                       ?? (request.ScenarioKey is null ? ScenarioCatalog.RapidChargeHiddenHotspot : ScenarioCatalog.Find(request.ScenarioKey))
                       ?? throw new KeyNotFoundException($"Unknown scenario '{request.ScenarioKey}'.");

        await _validator.ValidateAndThrowAsync(scenario, cancellationToken);
        var run = await _coordinator.StartAsync(scenario, request.Name, cancellationToken);
        return CreatedAtAction(nameof(Get), new { id = run.Id }, SimulationRunDto.From(run));
    }

    /// <summary>Validates a scenario without running it.</summary>
    [HttpPost("validate")]
    public async Task<IActionResult> Validate(ScenarioDefinition scenario, CancellationToken cancellationToken)
    {
        await _validator.ValidateAndThrowAsync(scenario, cancellationToken);
        return NoContent();
    }

    /// <summary>Lists recent simulation runs.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<SimulationRunDto>>> List([FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        var runs = await _runs.ListAsync(Math.Clamp(take, 1, 200), cancellationToken);
        return Ok(runs.Select(SimulationRunDto.From).ToArray());
    }

    /// <summary>Gets one run summary.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<SimulationRunDto>> Get(Guid id, CancellationToken cancellationToken) =>
        await _runs.GetAsync(id, cancellationToken) is { } run ? Ok(SimulationRunDto.From(run)) : NotFound();

    /// <summary>Persisted, down-sampled time series of a run.</summary>
    [HttpGet("{id:guid}/snapshots")]
    public async Task<ActionResult<IReadOnlyList<SimulationSnapshot>>> Snapshots(Guid id, CancellationToken cancellationToken) =>
        Ok(await _runs.GetSnapshotsAsync(id, cancellationToken));
}
