using System.ComponentModel.DataAnnotations;
using AnomalyDetection.Api.Auth;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Queries;
using AnomalyDetection.Application.Reviews;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnomalyDetection.Api.Controllers;

public sealed class SubmitReviewRequest
{
    /// <summary>ConfirmedIssue, BenignChange, FalsePositive, DuplicateAlert, InsufficientEvidence or Unreviewed (reopen).</summary>
    [Required]
    public string Outcome { get; set; } = string.Empty;

    [MaxLength(AnomalyReview.MaxNoteLength)]
    public string? Note { get; set; }
}

/// <summary>Anomaly records, investigation context (FR-06/FR-07) and engineering review (FR-08).</summary>
[Route("api/v1/anomalies")]
[Authorize(Policy = Policies.Engineer)]
public sealed class AnomaliesController(AnomalyQueryService queries, ReviewService reviews) : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<AnomalyListItemDto>> List(
        [FromQuery] string? service,
        [FromQuery] string? environment,
        [FromQuery] ReviewState? reviewState,
        [FromQuery] string? modelVersion,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] double? minScore,
        [FromQuery] string? sort,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct) =>
        queries.ListAsync(new AnomalyFilter(service, environment, reviewState, modelVersion, from, to, minScore, sort, page, pageSize), ct);

    [HttpGet("stats")]
    public Task<DashboardStatsDto> Stats([FromQuery] int days = 7, CancellationToken ct = default) => queries.StatsAsync(days, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var detail = await queries.GetAsync(id, ct);
        return detail is null ? NotFound() : Ok(detail);
    }

    /// <summary>Related raw events for the anomaly's service, environment and window (FR-07).</summary>
    [HttpGet("{id:guid}/events")]
    public async Task<IActionResult> Events(Guid id, [FromQuery] string? eventType, [FromQuery] string? correlationId, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var result = await queries.RelatedEventsAsync(id, eventType, correlationId, page, pageSize, ct);
        return result is null ? NotFound() : Ok(result);
    }

    [HttpGet("{id:guid}/reviews")]
    public async Task<IActionResult> Reviews(Guid id, CancellationToken ct)
    {
        var history = await reviews.HistoryAsync(id, ct);
        return history is null ? NotFound() : Ok(history);
    }

    /// <summary>Record a review outcome and optional note. History is append-only.</summary>
    [HttpPost("{id:guid}/reviews")]
    public async Task<IActionResult> Review(Guid id, [FromBody] SubmitReviewRequest request, CancellationToken ct)
    {
        var result = await reviews.ReviewAsync(id, request.Outcome, request.Note, Actor, ct);
        return FromResult(result, review => CreatedAtAction(nameof(Reviews), new { id }, review));
    }
}
