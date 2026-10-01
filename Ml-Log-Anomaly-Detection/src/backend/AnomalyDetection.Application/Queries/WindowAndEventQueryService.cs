using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Search;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.Application.Queries;

public sealed record WindowDetailDto(FeatureWindowDto Window, IReadOnlyList<ScoringRecordDto> Scores, Guid? AnomalyId);

/// <summary>Read side for feature windows, single events and quarantine.</summary>
public sealed class WindowAndEventQueryService(IApplicationDbContext db)
{
    public async Task<PagedResult<FeatureWindowDto>> ListWindowsAsync(
        string? service, string? environment, DateTime? fromUtc, DateTime? toUtc, ScoringStatus? status, int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var (p, s) = Paging.Normalize(page, pageSize);
        var query = db.FeatureWindows.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(service))
        {
            query = query.Where(w => w.ServiceName == service.Trim().ToLowerInvariant());
        }

        if (!string.IsNullOrWhiteSpace(environment))
        {
            query = query.Where(w => w.Environment == environment.Trim().ToLowerInvariant());
        }

        if (fromUtc is { } f)
        {
            var from = DateTime.SpecifyKind(f, DateTimeKind.Utc);
            query = query.Where(w => w.WindowStartUtc >= from);
        }

        if (toUtc is { } t)
        {
            var to = DateTime.SpecifyKind(t, DateTimeKind.Utc);
            query = query.Where(w => w.WindowStartUtc < to);
        }

        if (status is { } st)
        {
            query = query.Where(w => w.ScoringStatus == st);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderByDescending(w => w.WindowStartUtc).ThenBy(w => w.ServiceName)
            .Skip((p - 1) * s).Take(s).ToListAsync(cancellationToken);
        return new PagedResult<FeatureWindowDto>(rows.Select(FeatureWindowDto.From).ToList(), p, s, total);
    }

    public async Task<WindowDetailDto?> GetWindowAsync(Guid windowId, CancellationToken cancellationToken)
    {
        var window = await db.FeatureWindows.AsNoTracking().FirstOrDefaultAsync(w => w.WindowId == windowId, cancellationToken);
        if (window is null)
        {
            return null;
        }

        var scores = await db.ScoringRecords.AsNoTracking().Where(r => r.WindowId == windowId)
            .OrderByDescending(r => r.ScoredAtUtc)
            .Select(r => new ScoringRecordDto(r.Id, r.WindowId, r.ModelId, r.ModelVersion, r.Score, r.Threshold, r.IsAnomaly, r.ReasonSummary, r.ScoredAtUtc))
            .ToListAsync(cancellationToken);
        var anomalyId = await db.Anomalies.AsNoTracking().Where(a => a.WindowId == windowId)
            .OrderByDescending(a => a.CreatedAtUtc).Select(a => (Guid?)a.AnomalyId).FirstOrDefaultAsync(cancellationToken);
        return new WindowDetailDto(FeatureWindowDto.From(window), scores, anomalyId);
    }

    public async Task<EventDocument?> GetEventAsync(Guid id, CancellationToken cancellationToken)
    {
        var e = await db.OperationalEvents.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return e is null ? null : EventInvestigationService.ToDocument(e);
    }

    public async Task<PagedResult<QuarantinedEventDto>> ListQuarantineAsync(int? page, int? pageSize, CancellationToken cancellationToken)
    {
        var (p, s) = Paging.Normalize(page, pageSize);
        var total = await db.QuarantinedEvents.LongCountAsync(cancellationToken);
        var rows = await db.QuarantinedEvents.AsNoTracking().OrderByDescending(q => q.ReceivedAtUtc)
            .Skip((p - 1) * s).Take(s).ToListAsync(cancellationToken);
        var items = rows.Select(q => new QuarantinedEventDto(
            q.Id,
            q.Source,
            q.EventId,
            q.ServiceName,
            q.ReasonCodes.Split(',', StringSplitOptions.RemoveEmptyEntries),
            JsonDocument.Parse(q.SanitizedPayloadJson).RootElement.Clone(),
            q.ReceivedAtUtc)).ToList();
        return new PagedResult<QuarantinedEventDto>(items, p, s, total);
    }
}
