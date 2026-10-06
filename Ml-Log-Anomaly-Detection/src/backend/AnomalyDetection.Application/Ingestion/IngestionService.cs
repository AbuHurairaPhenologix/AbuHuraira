using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Application.Ingestion;

public enum IngestionOutcome
{
    Accepted,
    AcceptedLate,
    Duplicate,
    Quarantined,
}

public sealed record IngestionItemResult(int Index, string? EventId, IngestionOutcome Outcome, IReadOnlyList<string> Reasons);

public sealed record IngestionResult(int Received, int Accepted, int AcceptedLate, int Duplicates, int Quarantined, IReadOnlyList<IngestionItemResult> Items);

/// <summary>
/// FR-01/FR-02: validates, sanitizes, normalizes, de-duplicates and persists events. Invalid records are quarantined
/// (never silently dropped). Late events are stored but not merged into finalized windows (TC-10).
/// </summary>
public sealed class IngestionService(
    IApplicationDbContext db,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<IngestionService> logger)
{
    public const int MaxBatchSize = 5_000;

    public async Task<IngestionResult> IngestAsync(IReadOnlyList<RawEventInput> inputs, string source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count > MaxBatchSize)
        {
            throw new ArgumentException($"A batch may contain at most {MaxBatchSize} events.", nameof(inputs));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var maxSkew = TimeSpan.FromSeconds(options.Value.MaxFutureSkewSeconds);
        var items = new List<IngestionItemResult>(inputs.Count);
        var accepted = new List<(int Index, NormalizedEvent Event)>();
        var seenInBatch = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < inputs.Count; i++)
        {
            var result = EventNormalizer.Normalize(inputs[i], now, maxSkew);
            if (!result.IsValid)
            {
                db.QuarantinedEvents.Add(new QuarantinedEvent(
                    source,
                    Truncate(result.Sanitized.EventId, 128),
                    Truncate(result.Sanitized.ServiceName, 100),
                    result.ReasonCodes,
                    JsonSerializer.Serialize(result.Sanitized.ToDictionary()),
                    now));
                items.Add(new IngestionItemResult(i, result.Sanitized.EventId, IngestionOutcome.Quarantined, result.ReasonCodes));
                continue;
            }

            var normalized = result.Event!;
            if (!seenInBatch.Add(normalized.EventId))
            {
                items.Add(new IngestionItemResult(i, normalized.EventId, IngestionOutcome.Duplicate, ["duplicate_in_batch"]));
                continue;
            }

            accepted.Add((i, normalized));
        }

        // Duplicate detection against stored events (TC-09). The unique index on event_id is the final guard.
        var candidateIds = accepted.Select(a => a.Event.EventId).ToList();
        var existing = candidateIds.Count == 0
            ? []
            : (await db.OperationalEvents.AsNoTracking()
                .Where(e => candidateIds.Contains(e.EventId))
                .Select(e => e.EventId)
                .ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);

        var services = new Dictionary<(string, string), ServiceDefinition>();
        var toInsert = new List<(int Index, NormalizedEvent Event, ServiceDefinition Service)>();
        foreach (var (index, normalized) in accepted)
        {
            if (existing.Contains(normalized.EventId))
            {
                items.Add(new IngestionItemResult(index, normalized.EventId, IngestionOutcome.Duplicate, ["duplicate_event_id"]));
                continue;
            }

            var service = await GetOrCreateServiceAsync(normalized.ServiceName, normalized.Environment, services, now, cancellationToken);
            toInsert.Add((index, normalized, service));
        }

        // Preload finalized windows overlapping this batch (one query instead of one per event).
        var finalizedWindows = new List<FeatureWindow>();
        if (toInsert.Count > 0)
        {
            var serviceIds = toInsert.Select(t => t.Service.Id).Distinct().ToList();
            var minTs = toInsert.Min(t => t.Event.EventTimestampUtc);
            var maxTs = toInsert.Max(t => t.Event.EventTimestampUtc);
            finalizedWindows = await db.FeatureWindows
                .Where(w => serviceIds.Contains(w.ServiceDefinitionId) && w.WindowStartUtc <= maxTs && w.WindowEndUtc > minTs)
                .ToListAsync(cancellationToken);
        }

        foreach (var (index, normalized, service) in toInsert)
        {
            var entity = new OperationalEvent(
                normalized.EventId,
                service,
                normalized.EventTimestampUtc,
                normalized.EventType,
                normalized.EndpointGroup,
                normalized.StatusCode,
                normalized.DurationMs,
                normalized.ErrorFlag,
                normalized.AuthenticationResult,
                normalized.DependencyName,
                normalized.RetryCount,
                normalized.CorrelationId,
                normalized.AttributesJson,
                now);

            // Late-arrival policy (TC-10): if the event's window has already been finalized, keep the event for
            // investigation but do not alter the finalized feature vector or any score derived from it.
            var finalizedWindow = finalizedWindows.FirstOrDefault(w =>
                w.ServiceDefinitionId == service.Id
                && w.WindowStartUtc <= normalized.EventTimestampUtc
                && w.WindowEndUtc > normalized.EventTimestampUtc);

            if (finalizedWindow is not null)
            {
                entity.MarkLate(finalizedWindow.WindowId);
                finalizedWindow.RecordLateEvents(1);
                items.Add(new IngestionItemResult(index, normalized.EventId, IngestionOutcome.AcceptedLate, ["late_arrival_window_finalized"]));
            }
            else
            {
                items.Add(new IngestionItemResult(index, normalized.EventId, IngestionOutcome.Accepted, []));
            }

            db.OperationalEvents.Add(entity);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex) && inputs.Count > 1)
        {
            // A concurrent producer inserted one of these events between our check and the insert.
            // Retry event-by-event so only the true duplicates are skipped.
            logger.LogWarning("Concurrent duplicate detected during batch ingestion; retrying events individually.");
            db.ResetTracking();
            var combined = new List<IngestionItemResult>();
            for (var i = 0; i < inputs.Count; i++)
            {
                var single = await IngestAsync([inputs[i]], source, cancellationToken);
                combined.AddRange(single.Items.Select(it => it with { Index = i }));
            }

            return Summarize(combined, inputs.Count);
        }
        catch (DbUpdateException ex) when (db.IsUniqueViolation(ex))
        {
            db.ResetTracking();
            return Summarize([new IngestionItemResult(0, inputs[0].EventId, IngestionOutcome.Duplicate, ["duplicate_event_id"])], 1);
        }

        var summary = Summarize(items.OrderBy(i => i.Index).ToList(), inputs.Count);
        if (summary.Quarantined > 0)
        {
            logger.LogWarning(
                "Ingestion quarantined {Quarantined} of {Received} events from {Source}",
                summary.Quarantined,
                summary.Received,
                source);
        }

        return summary;
    }

    private async Task<ServiceDefinition> GetOrCreateServiceAsync(
        string name,
        string environment,
        Dictionary<(string, string), ServiceDefinition> cache,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (cache.TryGetValue((name, environment), out var cached))
        {
            return cached;
        }

        var service = await db.Services.FirstOrDefaultAsync(s => s.Name == name && s.Environment == environment, cancellationToken);
        if (service is null)
        {
            service = new ServiceDefinition(name, environment, now);
            db.Services.Add(service);
        }

        cache[(name, environment)] = service;
        return service;
    }

    private static IngestionResult Summarize(IReadOnlyList<IngestionItemResult> items, int received) => new(
        received,
        items.Count(i => i.Outcome == IngestionOutcome.Accepted),
        items.Count(i => i.Outcome == IngestionOutcome.AcceptedLate),
        items.Count(i => i.Outcome == IngestionOutcome.Duplicate),
        items.Count(i => i.Outcome == IngestionOutcome.Quarantined),
        items);

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
