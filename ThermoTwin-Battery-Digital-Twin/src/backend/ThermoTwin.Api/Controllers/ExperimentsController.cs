using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Experiments;
using ThermoTwin.Domain.Entities;
using ThermoTwin.Domain.Enums;
using ThermoTwin.SimulationWorker;

namespace ThermoTwin.Api.Controllers;

public sealed record ExperimentDetailDto(Guid Id, ExperimentKind Kind, string Title, string Summary, DateTimeOffset CreatedAt, double DurationMs, JsonElement Result);

public sealed record ExperimentOverviewDto(IReadOnlyList<ExperimentSummaryDto> Experiments, IReadOnlyList<ExperimentJobStatus> Queue);

/// <summary>Reproducible numerical experiments (convergence, regularisation, sensitivity, forecasting, cooling).</summary>
[ApiController]
[Route("api/experiments")]
public sealed class ExperimentsController : ControllerBase
{
    private readonly IExperimentRepository _repository;
    private readonly ExperimentQueue _queue;

    public ExperimentsController(IExperimentRepository repository, ExperimentQueue queue)
    {
        _repository = repository;
        _queue = queue;
    }

    /// <summary>All stored experiment results (newest first) and the state of the background queue.</summary>
    [HttpGet]
    public async Task<ActionResult<ExperimentOverviewDto>> List(CancellationToken cancellationToken)
    {
        var records = await _repository.ListAsync(cancellationToken);
        return Ok(new ExperimentOverviewDto(records.Select(Summary).ToArray(), _queue.Status));
    }

    /// <summary>Most recent result of an experiment kind, including its full JSON payload.</summary>
    [HttpGet("latest/{kind}")]
    public async Task<ActionResult<ExperimentDetailDto>> Latest(ExperimentKind kind, CancellationToken cancellationToken) =>
        await _repository.GetLatestAsync(kind, cancellationToken) is { } record ? Ok(Detail(record)) : NotFound();

    /// <summary>One stored experiment result by id.</summary>
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<ExperimentDetailDto>> Get(Guid id, CancellationToken cancellationToken) =>
        await _repository.GetAsync(id, cancellationToken) is { } record ? Ok(Detail(record)) : NotFound();

    /// <summary>Queues an experiment on the background worker. Poll <c>GET /api/experiments</c> or listen for <c>experimentCompleted</c>.</summary>
    [HttpPost("{kind}/run")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult Run(ExperimentKind kind) =>
        _queue.Enqueue(kind)
            ? Accepted(new { kind, state = ExperimentJobState.Queued })
            : Conflict(new ProblemDetails { Title = "Experiment already queued or running", Status = 409 });

    private static ExperimentSummaryDto Summary(ExperimentRecord r) =>
        new(r.Id, r.Kind, ExperimentRunner.Describe(r.Kind), r.Summary, r.CreatedAt, r.DurationMs);

    private static ExperimentDetailDto Detail(ExperimentRecord r)
    {
        using var document = JsonDocument.Parse(r.ResultJson);
        return new ExperimentDetailDto(r.Id, r.Kind, ExperimentRunner.Describe(r.Kind), r.Summary, r.CreatedAt, r.DurationMs,
            document.RootElement.Clone());
    }
}
