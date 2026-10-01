using System.Text.Json;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Application.Scoring;

public enum ScoringRunOutcome
{
    NothingToScore,
    NoActiveModel,
    Scored,
    Deferred,
    Rejected,
}

public sealed record ScoringSummary(ScoringRunOutcome Outcome, int WindowsScored, int AnomaliesCreated, int WindowsDeferred, int WindowsRejected, string? ModelVersion, string? Message);

/// <summary>
/// FR-05/FR-06: batch-scores finalized windows with the active registered model and persists score, threshold, model
/// version and context. Runs only in the background — never in an end-user request. If the ML service is
/// unavailable, windows are deferred with back-off and retried; nothing else in the application is affected (TC-04).
/// </summary>
public sealed class ScoringService(
    IApplicationDbContext db,
    IMlScoringClient ml,
    TimeProvider clock,
    IOptions<PipelineOptions> options,
    ILogger<ScoringService> logger)
{
    public async Task<ScoringSummary> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow().UtcDateTime;

        var model = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(m => m.IsActive, cancellationToken);
        var due = db.FeatureWindows.Where(w =>
            (w.ScoringStatus == ScoringStatus.Pending || w.ScoringStatus == ScoringStatus.Deferred)
            && (w.NextScoringAttemptUtc == null || w.NextScoringAttemptUtc <= now));

        if (model is null)
        {
            var waiting = await due.CountAsync(cancellationToken);
            if (waiting > 0)
            {
                logger.LogWarning("{Count} feature windows are waiting for scoring but no model version is active.", waiting);
            }

            return new ScoringSummary(ScoringRunOutcome.NoActiveModel, 0, 0, 0, 0, null, "No active model version; scoring is deferred.");
        }

        var windows = await due
            .Where(w => w.FeatureSchemaVersion == model.FeatureSchemaVersion)
            .OrderBy(w => w.WindowStartUtc)
            .Take(settings.ScoringBatchSize)
            .ToListAsync(cancellationToken);

        if (windows.Count == 0)
        {
            return new ScoringSummary(ScoringRunOutcome.NothingToScore, 0, 0, 0, 0, model.Version, null);
        }

        var items = windows.Select(w => new ScoreItem(
            w.WindowId,
            w.ServiceName,
            w.Environment,
            w.WindowStartUtc,
            w.WindowEndUtc,
            w.Features.ToDictionary())).ToList();

        var response = await ml.ScoreBatchAsync(model.Version, model.FeatureSchemaVersion, items, cancellationToken);
        var baseDelay = TimeSpan.FromSeconds(settings.ScoringRetryBaseDelaySeconds);
        var maxDelay = TimeSpan.FromSeconds(settings.ScoringRetryMaxDelaySeconds);

        switch (response.Status)
        {
            case MlCallStatus.Success:
                return await PersistResultsAsync(model, windows, response.Value!, now, cancellationToken);

            case MlCallStatus.ModelNotActive:
                // The ML service restarted or lost its activation state. PostgreSQL is the source of truth for the
                // active model, so re-activate it there (the ML service re-verifies the artifact) and retry later.
                logger.LogWarning("ML service reports model {ModelVersion} inactive; reconciling activation state.", model.Version);
                var reconcile = await ml.ActivateModelAsync(model.Version, cancellationToken);
                if (reconcile.IsSuccess && string.Equals(reconcile.Value!.ArtifactSha256, model.ArtifactSha256, StringComparison.OrdinalIgnoreCase))
                {
                    var retry = await ml.ScoreBatchAsync(model.Version, model.FeatureSchemaVersion, items, cancellationToken);
                    if (retry.IsSuccess)
                    {
                        return await PersistResultsAsync(model, windows, retry.Value!, now, cancellationToken);
                    }
                }

                foreach (var w in windows)
                {
                    w.DeferScoring($"model_not_active_in_ml_service; reconcile={reconcile.Status}", now, TimeSpan.Zero, maxDelay, countAttempt: false);
                }

                await db.SaveChangesAsync(cancellationToken);
                return new ScoringSummary(ScoringRunOutcome.Deferred, 0, 0, windows.Count, 0, model.Version, "Model activation reconciled with ML service.");

            case MlCallStatus.Rejected:
                logger.LogError("ML service rejected scoring batch for model {ModelVersion}: {Error}", model.Version, response.Error);
                foreach (var w in windows)
                {
                    w.RejectScoring($"rejected_by_ml_service: {response.Error}");
                }

                await db.SaveChangesAsync(cancellationToken);
                return new ScoringSummary(ScoringRunOutcome.Rejected, 0, 0, 0, windows.Count, model.Version, response.Error);

            default:
                // Unavailable / timeout / circuit open / model not found / unauthorized: defer and retry with back-off.
                logger.LogWarning(
                    "Scoring deferred for {Count} windows: ML call status {Status} ({Error})",
                    windows.Count,
                    response.Status,
                    response.Error);
                foreach (var w in windows)
                {
                    w.DeferScoring($"{response.Status}: {response.Error}", now, baseDelay, maxDelay);
                }

                await db.SaveChangesAsync(cancellationToken);
                return new ScoringSummary(ScoringRunOutcome.Deferred, 0, 0, windows.Count, 0, model.Version, response.Error);
        }
    }

    private async Task<ScoringSummary> PersistResultsAsync(
        ModelVersion model,
        IReadOnlyList<FeatureWindow> windows,
        IReadOnlyList<ScoreResult> results,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var byWindow = results.GroupBy(r => r.WindowId).ToDictionary(g => g.Key, g => g.First());
        var windowIds = windows.Select(w => w.WindowId).ToList();
        var alreadyScored = (await db.ScoringRecords.AsNoTracking()
                .Where(s => s.ModelId == model.ModelId && windowIds.Contains(s.WindowId))
                .Select(s => s.WindowId)
                .ToListAsync(cancellationToken))
            .ToHashSet();

        var scored = 0;
        var anomalies = 0;
        var rejected = 0;
        foreach (var window in windows)
        {
            if (alreadyScored.Contains(window.WindowId))
            {
                // Idempotent re-processing: never create a second score for the same window and model.
                window.MarkScored(now);
                continue;
            }

            if (!byWindow.TryGetValue(window.WindowId, out var result)
                || result.ModelVersion != model.Version
                || !FeatureSchema.IsCompatible(result.SchemaVersion)
                || double.IsNaN(result.Score) || double.IsInfinity(result.Score))
            {
                window.RejectScoring("ml_response_missing_or_inconsistent");
                rejected++;
                continue;
            }

            if (Math.Abs(result.Threshold - model.ValidationThreshold) > 1e-9)
            {
                logger.LogWarning(
                    "Threshold returned by ML ({Returned}) differs from registered threshold ({Registered}) for {ModelVersion}; persisting the threshold actually used.",
                    result.Threshold,
                    model.ValidationThreshold,
                    model.Version);
            }

            var reasonSummary = string.IsNullOrWhiteSpace(result.ReasonSummary)
                ? ReasonSummaryBuilder.Build(result.Reasons)
                : result.ReasonSummary;

            var record = new ScoringRecord(
                window.WindowId,
                model.ModelId,
                model.Version,
                result.SchemaVersion,
                result.Score,
                result.Threshold,
                result.IsAnomaly,
                reasonSummary,
                JsonSerializer.Serialize(result.Reasons),
                now);
            db.ScoringRecords.Add(record);

            if (result.IsAnomaly)
            {
                db.Anomalies.Add(new AnomalyRecord(record, window, now));
                anomalies++;
            }

            window.MarkScored(now);
            scored++;
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Scored {Scored} windows with {ModelVersion}: {Anomalies} anomalies, {Rejected} rejected",
            scored,
            model.Version,
            anomalies,
            rejected);
        return new ScoringSummary(ScoringRunOutcome.Scored, scored, anomalies, 0, rejected, model.Version, null);
    }
}
