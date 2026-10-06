using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Application.Search;

/// <summary>Copies persisted events into the search index asynchronously; an index outage only delays search.</summary>
public sealed class IndexingService(
    IApplicationDbContext db,
    IEventSearchService search,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<IndexingService> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var batch = await db.OperationalEvents
            .Where(e => e.IndexedAtUtc == null)
            .OrderBy(e => e.ReceivedAtUtc)
            .Take(options.Value.IndexingBatchSize)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return 0;
        }

        var indexed = await search.IndexAsync(batch, cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var e in batch.Where(e => indexed.Contains(e.Id)))
        {
            e.MarkIndexed(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        if (indexed.Count < batch.Count)
        {
            logger.LogWarning("Indexed {Indexed} of {Total} events; the remainder will be retried.", indexed.Count, batch.Count);
        }

        return indexed.Count;
    }
}
