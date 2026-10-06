using System.Threading.Channels;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Health;
using AutoSphere.Application.Ota;
using AutoSphere.Domain.Telemetry;
using AutoSphere.Infrastructure.Persistence;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Infrastructure.BackgroundJobs;

/// <summary>
/// Buffers sampled telemetry and writes it in batches, so ingestion of live data never waits for the database.
/// </summary>
public sealed class TelemetrySampleWriter(IServiceScopeFactory scopeFactory, ILogger<TelemetrySampleWriter> logger) : BackgroundService, ITelemetrySampleSink
{
    private const int MaxBatch = 2000;
    private readonly Channel<TelemetryRecord> _queue = Channel.CreateBounded<TelemetryRecord>(
        new BoundedChannelOptions(50_000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public void Enqueue(TelemetryRecord record) => _queue.Writer.TryWrite(record);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<TelemetryRecord>(MaxBatch);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // flush what is left before shutting down
            }

            while (batch.Count < MaxBatch && _queue.Reader.TryRead(out var record))
            {
                batch.Add(record);
            }

            if (batch.Count == 0)
            {
                continue;
            }

            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoSphereDbContext>();
                db.TelemetryRecords.AddRange(batch);
                await db.SaveChangesAsync(CancellationToken.None);
            }
            catch (DbUpdateException ex)
            {
                // e.g. the vehicle was deleted meanwhile (foreign key); losing a sample batch is acceptable.
                logger.LogWarning(ex, "Discarded {Count} telemetry samples", batch.Count);
            }

            batch.Clear();
        }
    }
}

/// <summary>Deletes telemetry samples older than <see cref="TelemetryOptions.RetentionDays"/>.</summary>
public sealed class TelemetryRetentionService(
    IServiceScopeFactory scopeFactory,
    IOptions<TelemetryOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryRetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), timeProvider);
        do
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoSphereDbContext>();
                var cutoff = timeProvider.GetUtcNow().AddDays(-options.Value.RetentionDays);
                var deleted = await db.TelemetryRecords.Where(r => r.Timestamp < cutoff).ExecuteDeleteAsync(stoppingToken);
                if (deleted > 0)
                {
                    logger.LogInformation("Telemetry retention removed {Count} samples older than {Cutoff:O}", deleted, cutoff);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Telemetry retention run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>
/// Re-evaluates the health of every vehicle periodically. This catches conditions that are not tied to a
/// message, such as telemetry becoming stale or temperatures crossing thresholds between status messages.
/// </summary>
public sealed class VehicleHealthMonitorService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<VehicleHealthMonitorService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AutoSphereDbContext>();
                var keys = await db.Vehicles.AsNoTracking()
                    .Where(v => v.Connectivity == ConnectivityStatus.Online || v.Health != HealthStatus.Offline)
                    .Select(v => v.Id).ToListAsync(stoppingToken);
                foreach (var key in keys)
                {
                    await using var vehicleScope = scopeFactory.CreateAsyncScope();
                    await vehicleScope.ServiceProvider.GetRequiredService<VehicleHealthService>().EvaluateAsync(key, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Health monitoring run failed");
            }
        }
    }
}

/// <summary>Fails OTA deployments that stopped making progress.</summary>
public sealed class OtaDeploymentTimeoutService(IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<OtaDeploymentTimeoutService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OtaStatusIngestionService>().FailStuckDeploymentsAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "OTA timeout supervision failed");
            }
        }
    }
}
