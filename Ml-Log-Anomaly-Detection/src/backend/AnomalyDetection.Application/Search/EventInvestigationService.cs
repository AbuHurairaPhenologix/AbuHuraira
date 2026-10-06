using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnomalyDetection.Application.Search;

/// <summary>
/// Investigation search (FR-07). Uses the OpenSearch index for exploratory search; if the index is unavailable the same
/// constrained filters are executed against PostgreSQL, and the response states which source answered.
/// </summary>
public sealed class EventInvestigationService(IApplicationDbContext db, IEventSearchService search, ILogger<EventInvestigationService> logger)
{
    public const string SourceOpenSearch = "opensearch";
    public const string SourcePostgresFallback = "postgresql-fallback";

    public async Task<EventSearchResult> SearchAsync(EventSearchCriteria criteria, CancellationToken cancellationToken)
    {
        var (page, pageSize) = Paging.Normalize(criteria.Page, criteria.PageSize, 50);
        var normalized = criteria with
        {
            Service = criteria.Service?.Trim().ToLowerInvariant(),
            Environment = criteria.Environment?.Trim().ToLowerInvariant(),
            EventType = NormalizeEventType(criteria.EventType),
            CorrelationId = string.IsNullOrWhiteSpace(criteria.CorrelationId) ? null : criteria.CorrelationId.Trim(),
            FromUtc = criteria.FromUtc is { } f ? DateTime.SpecifyKind(f, DateTimeKind.Utc) : null,
            ToUtc = criteria.ToUtc is { } t ? DateTime.SpecifyKind(t, DateTimeKind.Utc) : null,
            Page = page,
            PageSize = pageSize,
        };

        try
        {
            return await search.SearchAsync(normalized, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "OpenSearch query failed; falling back to PostgreSQL for event investigation.");
            return await SearchDatabaseAsync(normalized, cancellationToken);
        }
    }

    public async Task<EventSearchResult> SearchDatabaseAsync(EventSearchCriteria c, CancellationToken cancellationToken)
    {
        var query = db.OperationalEvents.AsNoTracking().AsQueryable();
        if (c.Service is not null)
        {
            query = query.Where(e => e.ServiceName == c.Service);
        }

        if (c.Environment is not null)
        {
            query = query.Where(e => e.Environment == c.Environment);
        }

        if (c.FromUtc is { } from)
        {
            query = query.Where(e => e.EventTimestampUtc >= from);
        }

        if (c.ToUtc is { } to)
        {
            query = query.Where(e => e.EventTimestampUtc < to);
        }

        if (c.CorrelationId is not null)
        {
            query = query.Where(e => e.CorrelationId == c.CorrelationId);
        }

        if (c.EventType is not null)
        {
            var type = EnumTokens.ParseEventType(c.EventType);
            query = query.Where(e => e.EventType == type);
        }

        var total = await query.LongCountAsync(cancellationToken);
        var rows = await query.OrderBy(e => e.EventTimestampUtc).ThenBy(e => e.EventId)
            .Skip((c.Page - 1) * c.PageSize).Take(c.PageSize)
            .ToListAsync(cancellationToken);
        return new EventSearchResult(rows.Select(ToDocument).ToList(), total, c.Page, c.PageSize, SourcePostgresFallback);
    }

    public static EventDocument ToDocument(OperationalEvent e) => new(
        e.Id,
        e.EventId,
        e.EventTimestampUtc,
        e.ServiceName,
        e.Environment,
        e.EventType.ToToken(),
        e.EndpointGroup,
        e.StatusCode,
        e.DurationMs,
        e.ErrorFlag,
        e.AuthenticationResult.ToToken(),
        e.DependencyName,
        e.RetryCount,
        e.CorrelationId,
        e.IsLate,
        e.FeatureWindowId);

    /// <summary>Only canonical event-type tokens are accepted as filters; anything else is ignored.</summary>
    private static string? NormalizeEventType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var token = value.Trim().ToLowerInvariant();
        return token is "http_request" or "authentication" or "dependency_call" or "background_job" ? token : null;
    }
}
