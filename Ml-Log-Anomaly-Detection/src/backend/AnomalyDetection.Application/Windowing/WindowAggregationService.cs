using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;
using AnomalyDetection.Domain.Windowing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Application.Windowing;

public sealed record AggregationSummary(int WindowsCreated, int EventsAggregated, int LateEvents, DateTime ClosedBeforeUtc);

/// <summary>
/// FR-03/FR-04: finalizes observation windows per service + environment and computes the <c>ops-v1</c> vector.
/// A window is finalized once <c>window_end + allowed_lateness</c> has passed. Re-running is idempotent: a window is
/// created at most once (unique index), and events already aggregated are never counted again.
/// </summary>
public sealed class WindowAggregationService(
    IApplicationDbContext db,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<WindowAggregationService> logger)
{
    private const int MaxEventsPerService = 250_000;

    public async Task<AggregationSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;
        var closable = now - TimeSpan.FromSeconds(settings.AllowedLatenessSeconds);

        // Every event strictly before this boundary belongs to a window that ended at or before `closable`.
        var boundary = WindowAligner.Align(closable, settings.WindowSizeMinutes).StartUtc;

        var serviceIds = await db.OperationalEvents
            .Where(e => e.ProcessingState == EventProcessingState.Pending && e.EventTimestampUtc < boundary)
            .Select(e => e.ServiceDefinitionId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var windowsCreated = 0;
        var eventsAggregated = 0;
        var lateEvents = 0;

        foreach (var serviceId in serviceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var service = await db.Services.SingleAsync(s => s.Id == serviceId, cancellationToken);
            var pending = await db.OperationalEvents
                .Where(e => e.ServiceDefinitionId == serviceId
                            && e.ProcessingState == EventProcessingState.Pending
                            && e.EventTimestampUtc < boundary)
                .OrderBy(e => e.EventTimestampUtc)
                .Take(MaxEventsPerService)
                .ToListAsync(cancellationToken);

            if (pending.Count == 0)
            {
                continue;
            }

            var groups = pending
                .GroupBy(e => WindowAligner.Align(e.EventTimestampUtc, settings.WindowSizeMinutes))
                .OrderBy(g => g.Key.StartUtc)
                .ToList();

            // If the per-service cap truncated the query, the last group may be incomplete; leave it for the next run.
            if (pending.Count == MaxEventsPerService && groups.Count > 1)
            {
                groups.RemoveAt(groups.Count - 1);
            }

            var minStart = groups[0].Key.StartUtc;
            var maxEnd = groups[^1].Key.EndUtc;
            var existingWindows = await db.FeatureWindows
                .Where(w => w.ServiceDefinitionId == serviceId && w.WindowStartUtc < maxEnd && w.WindowEndUtc > minStart)
                .ToListAsync(cancellationToken);

            foreach (var group in groups)
            {
                var events = group.ToList();
                var existing = existingWindows.FirstOrDefault(w => w.WindowStartUtc <= group.Key.StartUtc && w.WindowEndUtc > group.Key.StartUtc);
                if (existing is not null)
                {
                    // Window already finalized (e.g. by an earlier run): apply the late policy.
                    foreach (var e in events)
                    {
                        e.MarkLate(existing.WindowId);
                    }

                    existing.RecordLateEvents(events.Count);
                    lateEvents += events.Count;
                    continue;
                }

                var features = FeatureCalculator.Calculate(events.Select(e => e.ToFeatureInput()));
                var window = new FeatureWindow(service, group.Key.StartUtc, group.Key.EndUtc, features, events.Count, now);
                db.FeatureWindows.Add(window);
                foreach (var e in events)
                {
                    e.MarkAggregated(window.WindowId);
                }

                windowsCreated++;
                eventsAggregated += events.Count;
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex) when (db.IsUniqueViolation(ex))
            {
                // Another worker finalized the same window concurrently; its result wins and ours is discarded.
                logger.LogWarning("Window for service {ServiceId} was finalized concurrently; skipping.", serviceId);
                db.ResetTracking();
            }
        }

        if (windowsCreated > 0 || lateEvents > 0)
        {
            logger.LogInformation(
                "Window aggregation created {WindowsCreated} windows from {EventsAggregated} events ({LateEvents} late) before {Boundary:o}",
                windowsCreated,
                eventsAggregated,
                lateEvents,
                boundary);
        }

        return new AggregationSummary(windowsCreated, eventsAggregated, lateEvents, boundary);
    }
}
