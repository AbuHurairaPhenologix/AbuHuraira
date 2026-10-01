using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Infrastructure.Workers;

public sealed class ModelBootstrapOptions
{
    public const string SectionName = "Models:Bootstrap";

    /// <summary>Opt-in convenience for demos: register registry models and activate the recommended one if none is active.</summary>
    public bool Enabled { get; set; }

    /// <summary><c>recommended</c> (default) or an explicit registered version label.</summary>
    public string ActivateVersion { get; set; } = "recommended";
}

/// <summary>
/// Explicitly configured start-up bootstrap. Uses the same audited <see cref="ModelLifecycleService"/> path as an
/// administrator (actor <c>system-bootstrap</c>), including artifact and schema verification. Never overrides an
/// already active model.
/// </summary>
public sealed class ModelBootstrapWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<ModelBootstrapOptions> options,
    TimeProvider clock,
    ILogger<ModelBootstrapWorker> logger) : BackgroundService
{
    public const string Actor = "system-bootstrap";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        for (var attempt = 1; attempt <= 60 && !stoppingToken.IsCancellationRequested; attempt++)
        {
            try
            {
                if (await TryBootstrapAsync(stoppingToken))
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Model bootstrap attempt {Attempt} failed.", attempt);
            }

            await Task.Delay(TimeSpan.FromSeconds(10), clock, stoppingToken);
        }
    }

    private async Task<bool> TryBootstrapAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IApplicationDbContext>();
        var lifecycle = scope.ServiceProvider.GetRequiredService<ModelLifecycleService>();

        var registry = await lifecycle.ListRegistryAsync(ct);
        if (!registry.IsOk)
        {
            logger.LogInformation("Model bootstrap waiting for ML registry: {Error}", registry.Error);
            return false;
        }

        foreach (var entry in registry.Value!.Where(e => !e.RegisteredInBackend))
        {
            var registered = await lifecycle.RegisterAsync(entry.Metadata.ModelVersion, Actor, ct);
            logger.LogInformation("Bootstrap registration of {ModelVersion}: {Status} {Error}", entry.Metadata.ModelVersion, registered.Status, registered.Error);
        }

        if (await db.ModelVersions.AnyAsync(m => m.IsActive, ct))
        {
            return true;
        }

        var target = options.Value.ActivateVersion == "recommended"
            ? registry.Value!.FirstOrDefault(e => e.Metadata.Recommended && e.Metadata.ProductionEligible)?.Metadata.ModelVersion
            : options.Value.ActivateVersion;
        if (target is null)
        {
            logger.LogWarning("Model bootstrap: no recommended production-eligible model in the registry.");
            return true;
        }

        var model = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(m => m.Version == target, ct);
        if (model is null)
        {
            return false;
        }

        var activated = await lifecycle.ActivateAsync(model.ModelId, Actor, ct);
        logger.LogInformation("Bootstrap activation of {ModelVersion}: {Status} {Error}", target, activated.Status, activated.Error);
        return activated.IsOk;
    }
}
