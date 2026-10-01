using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Reviews;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Application.Search;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.Application.Queries;

/// <summary>Read side for anomalies: list, detail with investigation context, related events, dashboard stats.</summary>
public sealed class AnomalyQueryService(IApplicationDbContext db, EventInvestigationService investigation, TimeProvider clock)
{
    public async Task<PagedResult<AnomalyListItemDto>> ListAsync(AnomalyFilter filter, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(filter.Page, filter.PageSize);
        var query = db.Anomalies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(filter.Service))
        {
            query = query.Where(a => a.ServiceName == filter.Service.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(filter.Environment))
        {
            query = query.Where(a => a.Environment == filter.Environment.Trim().ToLowerInvariant());
        }

        if (filter.ReviewState is { } state)
        {
            query = query.Where(a => a.ReviewState == state);
        }

        if (!string.IsNullOrWhiteSpace(filter.ModelVersion))
        {
            query = query.Where(a => a.ModelVersion == filter.ModelVersion);
        }

        if (filter.FromUtc is { } from)
        {
            var f = DateTime.SpecifyKind(from, DateTimeKind.Utc);
            query = query.Where(a => a.WindowStartUtc >= f);
        }

        if (filter.ToUtc is { } to)
        {
            var t = DateTime.SpecifyKind(to, DateTimeKind.Utc);
            query = query.Where(a => a.WindowStartUtc < t);
        }

        if (filter.MinScore is { } minScore)
        {
            query = query.Where(a => a.Score >= minScore);
        }

        query = filter.Sort switch
        {
            "score" => query.OrderByDescending(a => a.Score).ThenByDescending(a => a.WindowStartUtc),
            "window" => query.OrderByDescending(a => a.WindowStartUtc),
            "severity" => query.OrderByDescending(a => a.Score - a.Threshold).ThenByDescending(a => a.WindowStartUtc),
            _ => query.OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.WindowStartUtc),
        };

        var total = await query.LongCountAsync(cancellationToken);
        var items = await query.Skip((page - 1) * pageSize).Take(pageSize).Select(a => new AnomalyListItemDto(
            a.AnomalyId,
            a.WindowId,
            a.ServiceName,
            a.Environment,
            a.WindowStartUtc,
            a.WindowEndUtc,
            a.Score,
            a.Threshold,
            a.ModelId,
            a.ModelVersion,
            a.ReviewState.ToString(),
            a.ReasonSummary,
            a.CreatedAtUtc)).ToListAsync(cancellationToken);
        return new PagedResult<AnomalyListItemDto>(items, page, pageSize, total);
    }

    public async Task<AnomalyDetailDto?> GetAsync(Guid anomalyId, CancellationToken cancellationToken)
    {
        var anomaly = await db.Anomalies.AsNoTracking().FirstOrDefaultAsync(a => a.AnomalyId == anomalyId, cancellationToken);
        if (anomaly is null)
        {
            return null;
        }

        var window = await db.FeatureWindows.AsNoTracking().SingleAsync(w => w.WindowId == anomaly.WindowId, cancellationToken);
        var model = await db.ModelVersions.AsNoTracking().SingleAsync(m => m.ModelId == anomaly.ModelId, cancellationToken);
        var scoring = await db.ScoringRecords.AsNoTracking().SingleAsync(s => s.Id == anomaly.ScoringRecordId, cancellationToken);
        var otherScores = await db.ScoringRecords.AsNoTracking()
            .Where(s => s.WindowId == anomaly.WindowId && s.Id != anomaly.ScoringRecordId)
            .OrderByDescending(s => s.ScoredAtUtc)
            .Select(s => new ScoringRecordDto(s.Id, s.WindowId, s.ModelId, s.ModelVersion, s.Score, s.Threshold, s.IsAnomaly, s.ReasonSummary, s.ScoredAtUtc))
            .ToListAsync(cancellationToken);

        var reviews = await db.AnomalyReviews.AsNoTracking()
            .Where(r => r.AnomalyId == anomalyId)
            .OrderBy(r => r.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        var windowEvents = db.OperationalEvents.AsNoTracking().Where(e => e.FeatureWindowId == anomaly.WindowId);
        // Correlation IDs involved in errors first, then chronological; capped for the review screen.
        var correlationIds = await windowEvents
            .Where(e => e.CorrelationId != null)
            .GroupBy(e => e.CorrelationId!)
            .Select(g => new { Id = g.Key, HasError = g.Max(e => e.ErrorFlag ? 1 : 0), First = g.Min(e => e.EventTimestampUtc) })
            .OrderByDescending(x => x.HasError)
            .ThenBy(x => x.First)
            .ThenBy(x => x.Id)
            .Take(50)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var typeCounts = await windowEvents
            .GroupBy(e => e.EventType)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return new AnomalyDetailDto(
            ToListItem(anomaly),
            FeatureWindowDto.From(window),
            model.Algorithm,
            model.ValidationThreshold,
            model.IsActive,
            BuildDeviations(window.Features, scoring.ReasonsJson),
            correlationIds,
            typeCounts.ToDictionary(t => t.Key.ToToken(), t => t.Count),
            reviews.Select(ReviewDto.From).ToList(),
            otherScores);
    }

    /// <summary>FR-07: related raw events for the anomaly's service, environment and window (constrained filters only).</summary>
    public async Task<EventSearchResult?> RelatedEventsAsync(Guid anomalyId, string? eventType, string? correlationId, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var anomaly = await db.Anomalies.AsNoTracking().FirstOrDefaultAsync(a => a.AnomalyId == anomalyId, cancellationToken);
        if (anomaly is null)
        {
            return null;
        }

        var (p, s) = Paging.Normalize(page, pageSize, 50);
        return await investigation.SearchAsync(
            new EventSearchCriteria(anomaly.ServiceName, anomaly.Environment, anomaly.WindowStartUtc, anomaly.WindowEndUtc, correlationId, eventType, p, s),
            cancellationToken);
    }

    public async Task<DashboardStatsDto> StatsAsync(int days, CancellationToken cancellationToken)
    {
        days = Math.Clamp(days, 1, 90);
        var windows = db.FeatureWindows.AsNoTracking();
        var processed = await windows.LongCountAsync(cancellationToken);
        var statusCounts = await windows.GroupBy(w => w.ScoringStatus).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(cancellationToken);
        long Status(ScoringStatus s) => statusCounts.FirstOrDefault(x => x.Key == s)?.Count ?? 0;

        var anomalies = await db.Anomalies.LongCountAsync(cancellationToken);
        var reviewCounts = await db.Anomalies.AsNoTracking().GroupBy(a => a.ReviewState).Select(g => new { g.Key, Count = g.LongCount() }).ToListAsync(cancellationToken);
        var byReviewState = Enum.GetValues<ReviewState>().ToDictionary(s => s.ToString(), s => reviewCounts.FirstOrDefault(r => r.Key == s)?.Count ?? 0);

        var active = await db.ModelVersions.AsNoTracking().Where(m => m.IsActive)
            .Select(m => new ActiveModelDto(m.ModelId, m.Version, m.Algorithm, m.ValidationThreshold, m.ActivatedAtUtc))
            .FirstOrDefaultAsync(cancellationToken);

        // Trend is anchored on the latest window so historical (backfilled) data is also visible.
        var latest = await windows.MaxAsync(w => (DateTime?)w.WindowStartUtc, cancellationToken) ?? clock.GetUtcNow().UtcDateTime;
        var since = latest.Date.AddDays(-(days - 1));
        var windowTrend = await windows.Where(w => w.WindowStartUtc >= since)
            .GroupBy(w => w.WindowStartUtc.Date)
            .Select(g => new { Day = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var anomalyTrend = await db.Anomalies.AsNoTracking().Where(a => a.WindowStartUtc >= since)
            .GroupBy(a => a.WindowStartUtc.Date)
            .Select(g => new { Day = g.Key, Count = g.LongCount() })
            .ToListAsync(cancellationToken);
        var trend = Enumerable.Range(0, days)
            .Select(i => since.AddDays(i))
            .Select(d => new TrendPointDto(
                DateOnly.FromDateTime(d),
                windowTrend.FirstOrDefault(x => x.Day == d)?.Count ?? 0,
                anomalyTrend.FirstOrDefault(x => x.Day == d)?.Count ?? 0))
            .ToList();

        var serviceWindows = await windows.GroupBy(w => new { w.ServiceName, w.Environment })
            .Select(g => new { g.Key.ServiceName, g.Key.Environment, Count = g.LongCount() }).ToListAsync(cancellationToken);
        var serviceAnomalies = await db.Anomalies.AsNoTracking().GroupBy(a => new { a.ServiceName, a.Environment })
            .Select(g => new { g.Key.ServiceName, g.Key.Environment, Count = g.LongCount() }).ToListAsync(cancellationToken);
        var services = serviceWindows
            .Select(s => new ServiceSummaryDto(s.ServiceName, s.Environment, s.Count, serviceAnomalies.FirstOrDefault(a => a.ServiceName == s.ServiceName && a.Environment == s.Environment)?.Count ?? 0))
            .OrderByDescending(s => s.Anomalies).ThenBy(s => s.Service)
            .ToList();

        var scored = Status(ScoringStatus.Scored);
        return new DashboardStatsDto(
            processed,
            scored,
            Status(ScoringStatus.Pending),
            Status(ScoringStatus.Deferred),
            Status(ScoringStatus.Rejected),
            anomalies,
            scored == 0 ? 0 : (double)anomalies / scored,
            byReviewState[nameof(ReviewState.Unreviewed)],
            byReviewState,
            await db.OperationalEvents.LongCountAsync(cancellationToken),
            await db.QuarantinedEvents.LongCountAsync(cancellationToken),
            await db.OperationalEvents.LongCountAsync(e => e.IsLate, cancellationToken),
            await db.OperationalEvents.LongCountAsync(e => e.IndexedAtUtc == null, cancellationToken),
            active,
            trend,
            services);
    }

    private static AnomalyListItemDto ToListItem(AnomalyRecord a) => new(
        a.AnomalyId, a.WindowId, a.ServiceName, a.Environment, a.WindowStartUtc, a.WindowEndUtc, a.Score, a.Threshold,
        a.ModelId, a.ModelVersion, a.ReviewState.ToString(), a.ReasonSummary, a.CreatedAtUtc);

    private static List<FeatureDeviationDto> BuildDeviations(FeatureVector features, string reasonsJson)
    {
        var reasons = JsonSerializer.Deserialize<List<FeatureReason>>(reasonsJson) ?? [];
        var values = features.ToDictionary();
        return FeatureSchema.OrderedFeatureNames.Select(name =>
        {
            var r = reasons.FirstOrDefault(x => x.Feature == name);
            return new FeatureDeviationDto(name, ReasonSummaryBuilder.Label(name), values[name], r?.BaselineMean, r?.BaselineStd, r?.ZScore, r?.Direction);
        }).ToList();
    }
}
