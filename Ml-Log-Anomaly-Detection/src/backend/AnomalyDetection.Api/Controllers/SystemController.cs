using System.ComponentModel.DataAnnotations;
using AnomalyDetection.Api.Auth;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Queries;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Infrastructure.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Api.Controllers;

public sealed record ComponentHealthDto(string Name, string Status, string? Description, double DurationMs, IReadOnlyDictionary<string, object>? Data);

public sealed record SystemStatusDto(
    string Status,
    DateTime CheckedAtUtc,
    IReadOnlyList<ComponentHealthDto> Components,
    IReadOnlyList<WorkerStatus> Workers,
    PipelineSettingsDto Pipeline,
    long EventQueueDepth,
    long EventQueueDropped,
    DashboardStatsDto Stats);

public sealed record PipelineSettingsDto(int WindowSizeMinutes, int AllowedLatenessSeconds, int ScoringBatchSize, bool BackgroundWorkersEnabled);

/// <summary>Health and operational status.</summary>
[Route("api/v1")]
public sealed class SystemController(
    HealthCheckService health,
    WorkerStatusRegistry workers,
    IEventQueue queue,
    AnomalyQueryService queries,
    IOptions<PipelineOptions> pipeline) : ApiControllerBase
{
    /// <summary>Anonymous health summary: Healthy / Degraded / Unhealthy per component (no sensitive details).</summary>
    [HttpGet("health")]
    [AllowAnonymous]
    public async Task<IActionResult> Health(CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(ct);
        var body = new
        {
            status = report.Status.ToString(),
            components = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        };
        return StatusCode(report.Status == HealthStatus.Unhealthy ? 503 : 200, body);
    }

    [HttpGet("system/status")]
    [Authorize(Policy = Policies.Engineer)]
    public async Task<SystemStatusDto> Status(CancellationToken ct)
    {
        var report = await health.CheckHealthAsync(ct);
        var components = new List<ComponentHealthDto> { new("api", "Healthy", "ASP.NET Core backend is serving requests", 0, null) };
        components.AddRange(report.Entries.Select(e => new ComponentHealthDto(
            e.Key,
            e.Value.Status.ToString(),
            e.Value.Description ?? e.Value.Exception?.Message,
            e.Value.Duration.TotalMilliseconds,
            e.Value.Data.Count == 0 ? null : e.Value.Data)));
        var o = pipeline.Value;
        return new SystemStatusDto(
            report.Status.ToString(),
            DateTime.UtcNow,
            components,
            workers.Snapshot(),
            new PipelineSettingsDto(o.WindowSizeMinutes, o.AllowedLatenessSeconds, o.ScoringBatchSize, o.EnableBackgroundWorkers),
            queue.ApproximateCount,
            queue.DroppedCount,
            await queries.StatsAsync(7, ct));
    }
}

/// <summary>Explicit administrative pipeline control and audit trail.</summary>
[Route("api/v1")]
[Authorize(Policy = Policies.Administrator)]
public sealed class AdministrationController(PipelineCoordinator pipeline, AuditService audit, IApplicationDbContext db, TimeProvider clock) : ApiControllerBase
{
    /// <summary>Runs window aggregation and scoring immediately (the background workers do this periodically).</summary>
    [HttpPost("pipeline/run")]
    public async Task<PipelineRunSummary> RunPipeline([FromQuery, Range(1, 200)] int maxScoringBatches = 50, CancellationToken ct = default)
    {
        var result = await pipeline.RunOnceAsync(maxScoringBatches, ct);
        await audit.RecordNowAsync(AuditActions.PipelineRun, Actor, "pipeline", null, AuditResults.Success, new
        {
            result.Aggregation.WindowsCreated,
            scored = result.Scoring.Sum(s => s.WindowsScored),
            anomalies = result.Scoring.Sum(s => s.AnomaliesCreated),
        }, ct);
        return result;
    }

    /// <summary>Re-queues windows whose scoring was rejected or deferred (e.g. after fixing the ML contract).</summary>
    [HttpPost("pipeline/requeue")]
    public async Task<IActionResult> Requeue(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var windows = await db.FeatureWindows.Where(w => w.ScoringStatus == ScoringStatus.Rejected || w.ScoringStatus == ScoringStatus.Deferred).ToListAsync(ct);
        foreach (var w in windows)
        {
            w.RequeueScoring(now);
        }

        audit.Record(AuditActions.ScoringRequeue, Actor, "pipeline", null, AuditResults.Success, new { windows = windows.Count });
        await db.SaveChangesAsync(ct);
        return Ok(new { requeued = windows.Count });
    }

    [HttpGet("audit")]
    public async Task<PagedResult<AuditEvent>> Audit([FromQuery] string? action, [FromQuery] int? page, [FromQuery] int? pageSize, CancellationToken ct)
    {
        var (p, s) = Paging.Normalize(page, pageSize, 50);
        var query = db.AuditEvents.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(action))
        {
            query = query.Where(a => a.Action == action);
        }

        var total = await query.LongCountAsync(ct);
        var items = await query.OrderByDescending(a => a.OccurredAtUtc).Skip((p - 1) * s).Take(s).ToListAsync(ct);
        return new PagedResult<AuditEvent>(items, p, s, total);
    }
}
