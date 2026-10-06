using AnomalyDetection.Application.Ingestion;
using AnomalyDetection.Infrastructure.Queue;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AnomalyDetection.Infrastructure.Workers;

/// <summary>Drains events captured by the request middleware and ingests them in batches, off the request path.</summary>
public sealed class EventQueueWorker(
    ChannelEventQueue queue,
    IServiceScopeFactory scopeFactory,
    WorkerStatusRegistry registry,
    TimeProvider clock,
    ILogger<EventQueueWorker> logger) : BackgroundService
{
    private const int MaxBatch = 500;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<RawEventInput>(MaxBatch);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await queue.Reader.WaitToReadAsync(stoppingToken))
                {
                    break;
                }

                // Small linger so bursts are ingested as one batch.
                await Task.Delay(TimeSpan.FromMilliseconds(250), clock, stoppingToken);
                while (batch.Count < MaxBatch && queue.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IngestionService>().IngestAsync(batch, "middleware", stoppingToken);
                registry.Success("event-queue", clock.GetUtcNow().UtcDateTime, $"accepted={result.Accepted} quarantined={result.Quarantined}");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                registry.Failure("event-queue", clock.GetUtcNow().UtcDateTime, $"{ex.GetType().Name}: {ex.Message}");
                logger.LogError(ex, "Failed to ingest {Count} captured events; they will be retried.", batch.Count);

                // Re-queue the batch (best effort) after a pause so a database outage does not lose events.
                await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                foreach (var item in batch)
                {
                    queue.TryEnqueue(item);
                }
            }
            finally
            {
                batch.Clear();
            }
        }
    }
}
