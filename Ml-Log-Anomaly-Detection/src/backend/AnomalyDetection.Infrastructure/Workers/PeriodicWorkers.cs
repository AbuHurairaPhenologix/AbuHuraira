using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Application.Search;
using AnomalyDetection.Application.Windowing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Infrastructure.Workers;

/// <summary>
/// Base class for scoped, periodic background work. Exceptions are logged and recorded, never propagated, so a
/// failing dependency (e.g. the ML service or OpenSearch) cannot crash the host or affect request handling.
/// </summary>
public abstract class PeriodicWorker(
    IServiceScopeFactory scopeFactory,
    WorkerStatusRegistry registry,
    TimeProvider clock,
    ILogger logger) : BackgroundService
{
    protected abstract string Name { get; }

    protected abstract TimeSpan Interval { get; }

    protected abstract Task<string> RunOnceAsync(IServiceProvider services, CancellationToken stoppingToken);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Small start-up delay so database migrations and dependencies settle first.
        await Task.Delay(TimeSpan.FromSeconds(3), clock, stoppingToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await RunOnceAsync(scope.ServiceProvider, stoppingToken);
                registry.Success(Name, clock.GetUtcNow().UtcDateTime, result);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                registry.Failure(Name, clock.GetUtcNow().UtcDateTime, $"{ex.GetType().Name}: {ex.Message}");
                logger.LogError(ex, "Background worker {Worker} failed; it will retry on the next interval.", Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}

public sealed class WindowAggregationWorker(IServiceScopeFactory f, WorkerStatusRegistry r, TimeProvider c, IOptions<PipelineOptions> o, ILogger<WindowAggregationWorker> l)
    : PeriodicWorker(f, r, c, l)
{
    protected override string Name => "window-aggregation";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.AggregationIntervalSeconds);

    protected override async Task<string> RunOnceAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var s = await services.GetRequiredService<WindowAggregationService>().RunOnceAsync(stoppingToken);
        return $"windows={s.WindowsCreated} events={s.EventsAggregated} late={s.LateEvents}";
    }
}

public sealed class ScoringWorker(IServiceScopeFactory f, WorkerStatusRegistry r, TimeProvider c, IOptions<PipelineOptions> o, ILogger<ScoringWorker> l)
    : PeriodicWorker(f, r, c, l)
{
    protected override string Name => "scoring";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.ScoringIntervalSeconds);

    protected override async Task<string> RunOnceAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var scoring = services.GetRequiredService<ScoringService>();
        var parts = new List<string>();

        // Drain several batches per tick while results keep coming back successfully.
        for (var i = 0; i < 20; i++)
        {
            var s = await scoring.RunOnceAsync(stoppingToken);
            parts.Add($"{s.Outcome}:{s.WindowsScored}/{s.AnomaliesCreated}");
            if (s.Outcome != ScoringRunOutcome.Scored)
            {
                break;
            }
        }

        return string.Join(' ', parts.TakeLast(3));
    }
}

public sealed class IndexingWorker(IServiceScopeFactory f, WorkerStatusRegistry r, TimeProvider c, IOptions<PipelineOptions> o, ILogger<IndexingWorker> l)
    : PeriodicWorker(f, r, c, l)
{
    protected override string Name => "opensearch-indexing";

    protected override TimeSpan Interval => TimeSpan.FromSeconds(o.Value.IndexingIntervalSeconds);

    protected override async Task<string> RunOnceAsync(IServiceProvider services, CancellationToken stoppingToken)
    {
        var indexing = services.GetRequiredService<IndexingService>();
        var total = 0;
        for (var i = 0; i < 20; i++)
        {
            var n = await indexing.RunOnceAsync(stoppingToken);
            total += n;
            if (n == 0)
            {
                break;
            }
        }

        return $"indexed={total}";
    }
}
