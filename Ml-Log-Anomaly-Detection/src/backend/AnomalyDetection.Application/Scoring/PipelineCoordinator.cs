using AnomalyDetection.Application.Search;
using AnomalyDetection.Application.Windowing;
using Microsoft.Extensions.Logging;

namespace AnomalyDetection.Application.Scoring;

public sealed record PipelineRunSummary(int EventsIndexed, string? IndexingError, AggregationSummary Aggregation, IReadOnlyList<ScoringSummary> Scoring);

/// <summary>
/// Runs one indexing pass, one aggregation pass and scoring passes until no due windows remain (bounded).
/// Used by the explicit administrative "run pipeline now" action and by automated tests; the background workers
/// run the same services on their own schedules.
/// </summary>
public sealed class PipelineCoordinator(
    IndexingService indexing,
    WindowAggregationService aggregation,
    ScoringService scoring,
    ILogger<PipelineCoordinator> logger)
{
    public async Task<PipelineRunSummary> RunOnceAsync(int maxScoringBatches, CancellationToken cancellationToken)
    {
        var indexed = 0;
        string? indexingError = null;
        try
        {
            for (var i = 0; i < 50; i++)
            {
                var n = await indexing.RunOnceAsync(cancellationToken);
                indexed += n;
                if (n == 0)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Search indexing is best-effort; it must never block aggregation and scoring.
            indexingError = ex.Message;
            logger.LogWarning(ex, "Indexing pass failed during pipeline run; continuing with aggregation and scoring.");
        }

        var aggregated = await aggregation.RunOnceAsync(cancellationToken);
        var batches = new List<ScoringSummary>();
        for (var i = 0; i < Math.Max(1, maxScoringBatches); i++)
        {
            var result = await scoring.RunOnceAsync(cancellationToken);
            batches.Add(result);
            if (result.Outcome != ScoringRunOutcome.Scored)
            {
                break;
            }
        }

        return new PipelineRunSummary(indexed, indexingError, aggregated, batches);
    }
}
