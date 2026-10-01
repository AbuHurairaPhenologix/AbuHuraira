using System.Text.Json;
using AnomalyDetection.Api.Auth;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Ingestion;
using AnomalyDetection.Application.Queries;
using AnomalyDetection.Application.Search;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnomalyDetection.Api.Controllers;

public sealed class IngestEventsRequest
{
    /// <summary>Events in the canonical schema. Each item is validated individually.</summary>
    public List<JsonElement> Events { get; set; } = [];
}

/// <summary>FR-01 ingestion and FR-07 investigation search over operational events.</summary>
[Route("api/v1/events")]
public sealed class EventsController(
    IngestionService ingestion,
    EventInvestigationService investigation,
    WindowAndEventQueryService queries) : ApiControllerBase
{
    /// <summary>Ingest a batch of structured events (service credential: <c>X-Api-Key</c>).</summary>
    [HttpPost]
    [AllowAnonymous]
    [RequireIngestionKey]
    [RequestSizeLimit(10 * 1024 * 1024)]
    [ProducesResponseType<IngestionResult>(StatusCodes.Status202Accepted)]
    public async Task<IActionResult> Ingest([FromBody] IngestEventsRequest request, CancellationToken ct)
    {
        if (request.Events.Count == 0)
        {
            return Problem(statusCode: 400, title: "At least one event is required.");
        }

        if (request.Events.Count > IngestionService.MaxBatchSize)
        {
            return Problem(statusCode: 413, title: $"A batch may contain at most {IngestionService.MaxBatchSize} events.");
        }

        var raw = request.Events.Select(RawEventInput.FromJson).ToList();
        var result = await ingestion.IngestAsync(raw, "api", ct);
        return Accepted(result);
    }

    /// <summary>Constrained event search (service, environment, time range, correlation ID, event type).</summary>
    [HttpGet]
    [Authorize(Policy = Policies.Engineer)]
    public Task<EventSearchResult> Search(
        [FromQuery] string? service,
        [FromQuery] string? environment,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? correlationId,
        [FromQuery] string? eventType,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct) =>
        investigation.SearchAsync(new EventSearchCriteria(service, environment, from, to, correlationId, eventType, page ?? 1, pageSize ?? 50), ct);

    [HttpGet("{id:guid}")]
    [Authorize(Policy = Policies.Engineer)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var e = await queries.GetEventAsync(id, ct);
        return e is null ? NotFound() : Ok(e);
    }

    /// <summary>Quarantined (invalid) input records with their reasons. Payloads are sanitized.</summary>
    [HttpGet("quarantine")]
    [Authorize(Policy = Policies.Engineer)]
    public Task<PagedResult<QuarantinedEventDto>> Quarantine([FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct) =>
        queries.ListQuarantineAsync(page, pageSize, ct);
}
