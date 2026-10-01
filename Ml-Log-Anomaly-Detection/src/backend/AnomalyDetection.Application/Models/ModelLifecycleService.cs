using System.Text.Json;
using System.Text.RegularExpressions;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AnomalyDetection.Application.Models;

public sealed record ModelDto(
    Guid ModelId,
    string ModelVersion,
    string Algorithm,
    string FeatureSchemaVersion,
    DateTime? TrainingPeriodStartUtc,
    DateTime? TrainingPeriodEndUtc,
    int RandomSeed,
    JsonElement LibraryVersions,
    JsonElement Parameters,
    double ValidationThreshold,
    string ThresholdObjective,
    string ArtifactPath,
    string ArtifactSha256,
    bool ProductionEligible,
    JsonElement? ValidationMetrics,
    DateTime TrainedAtUtc,
    DateTime CreatedAtUtc,
    string RegisteredBy,
    bool IsActive,
    DateTime? ActivatedAtUtc,
    string? ActivatedBy,
    DateTime? DeactivatedAtUtc,
    string? DeactivatedBy,
    long ScoredWindows,
    long Anomalies)
{
    public static ModelDto From(ModelVersion m, long scored = 0, long anomalies = 0) => new(
        m.ModelId,
        m.Version,
        m.Algorithm,
        m.FeatureSchemaVersion,
        m.TrainingPeriodStartUtc,
        m.TrainingPeriodEndUtc,
        m.RandomSeed,
        Parse(m.LibraryVersionsJson),
        Parse(m.ParametersJson),
        m.ValidationThreshold,
        m.ThresholdObjective,
        m.ArtifactPath,
        m.ArtifactSha256,
        m.ProductionEligible,
        m.ValidationMetricsJson is null ? null : Parse(m.ValidationMetricsJson),
        m.TrainedAtUtc,
        m.CreatedAtUtc,
        m.RegisteredBy,
        m.IsActive,
        m.ActivatedAtUtc,
        m.ActivatedBy,
        m.DeactivatedAtUtc,
        m.DeactivatedBy,
        scored,
        anomalies);

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}

public sealed record RegistryEntryDto(RegistryModelMetadata Metadata, bool RegisteredInBackend, Guid? BackendModelId);

public sealed record RetrainRequest(string Algorithm, string Source, DateTime? TrainingPeriodStartUtc, DateTime? TrainingPeriodEndUtc);

/// <summary>
/// FR-09: administrative model lifecycle. Activation verifies the artifact exists and matches its registered hash,
/// validates schema compatibility and metadata, and is audited. Retraining is explicit and never automatic.
/// Authorization (Administrator role) is enforced at the API boundary; every call here is audited.
/// </summary>
public sealed partial class ModelLifecycleService(
    IApplicationDbContext db,
    IMlScoringClient ml,
    AuditService audit,
    TimeProvider clock,
    ILogger<ModelLifecycleService> logger)
{
    public static readonly IReadOnlyList<string> TrainableAlgorithms = ["isolation_forest", "lof", "ocsvm", "random_forest", "all"];

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]{1,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelVersionRegex();

    public static bool IsValidVersionLabel(string? version) => version is not null && ModelVersionRegex().IsMatch(version);

    public async Task<IReadOnlyList<ModelDto>> ListAsync(CancellationToken cancellationToken)
    {
        var models = await db.ModelVersions.AsNoTracking().OrderByDescending(m => m.CreatedAtUtc).ToListAsync(cancellationToken);
        var scored = await db.ScoringRecords.AsNoTracking().GroupBy(s => s.ModelId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var anomalies = await db.Anomalies.AsNoTracking().GroupBy(a => a.ModelId).Select(g => new { g.Key, Count = g.LongCount() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return models.Select(m => ModelDto.From(m, scored.GetValueOrDefault(m.ModelId), anomalies.GetValueOrDefault(m.ModelId))).ToList();
    }

    public async Task<ModelDto?> GetAsync(Guid modelId, CancellationToken cancellationToken)
    {
        var model = await db.ModelVersions.AsNoTracking().FirstOrDefaultAsync(m => m.ModelId == modelId, cancellationToken);
        if (model is null)
        {
            return null;
        }

        var scored = await db.ScoringRecords.LongCountAsync(s => s.ModelId == modelId, cancellationToken);
        var anomalies = await db.Anomalies.LongCountAsync(a => a.ModelId == modelId, cancellationToken);
        return ModelDto.From(model, scored, anomalies);
    }

    public async Task<OperationResult<IReadOnlyList<RegistryEntryDto>>> ListRegistryAsync(CancellationToken cancellationToken)
    {
        var registry = await ml.ListRegistryAsync(cancellationToken);
        if (!registry.IsSuccess)
        {
            return OperationResult<IReadOnlyList<RegistryEntryDto>>.Unavailable($"ML registry unavailable: {registry.Error}");
        }

        var known = await db.ModelVersions.AsNoTracking().Select(m => new { m.Version, m.ModelId }).ToDictionaryAsync(m => m.Version, m => m.ModelId, cancellationToken);
        IReadOnlyList<RegistryEntryDto> entries = registry.Value!
            .Select(r => new RegistryEntryDto(r, known.ContainsKey(r.ModelVersion), known.TryGetValue(r.ModelVersion, out var id) ? id : null))
            .ToList();
        return OperationResult<IReadOnlyList<RegistryEntryDto>>.Ok(entries);
    }

    /// <summary>Registers a model version that exists in the controlled ML registry. Never accepts a file path.</summary>
    public async Task<OperationResult<ModelDto>> RegisterAsync(string modelVersion, string actor, CancellationToken cancellationToken)
    {
        if (!IsValidVersionLabel(modelVersion))
        {
            await audit.RecordNowAsync(AuditActions.ModelRegister, actor, "model", modelVersion, AuditResults.Rejected, new { reason = "invalid_version_label" }, cancellationToken);
            return OperationResult<ModelDto>.Invalid("Model version label is invalid. Only registered version labels are accepted (no paths).");
        }

        if (await db.ModelVersions.AnyAsync(m => m.Version == modelVersion, cancellationToken))
        {
            return OperationResult<ModelDto>.Conflict($"Model version '{modelVersion}' is already registered.");
        }

        var metadata = await ml.GetModelAsync(modelVersion, cancellationToken);
        if (metadata.Status == MlCallStatus.ModelNotFound)
        {
            await audit.RecordNowAsync(AuditActions.ModelRegister, actor, "model", modelVersion, AuditResults.Rejected, new { reason = "not_in_ml_registry" }, cancellationToken);
            return OperationResult<ModelDto>.NotFound($"Model version '{modelVersion}' is not present in the ML registry.");
        }

        if (!metadata.IsSuccess)
        {
            return OperationResult<ModelDto>.Unavailable($"ML registry unavailable: {metadata.Error}");
        }

        var m = metadata.Value!;
        var problems = ValidateMetadata(m, requireProductionEligible: false);
        if (problems.Count > 0)
        {
            await audit.RecordNowAsync(AuditActions.ModelRegister, actor, "model", modelVersion, AuditResults.Rejected, new { problems }, cancellationToken);
            return OperationResult<ModelDto>.Invalid("Model metadata failed validation: " + string.Join("; ", problems));
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var entity = new ModelVersion(
            m.ModelId,
            m.ModelVersion,
            m.Algorithm,
            m.FeatureSchemaVersion,
            m.TrainingPeriodStartUtc,
            m.TrainingPeriodEndUtc,
            m.RandomSeed,
            m.LibraryVersions.GetRawText(),
            m.Parameters.GetRawText(),
            m.ValidationThreshold,
            m.ThresholdObjective,
            m.ArtifactPath,
            m.ArtifactSha256,
            m.ProductionEligible,
            m.ValidationMetrics?.GetRawText(),
            m.CreatedAtUtc,
            now,
            actor);
        db.ModelVersions.Add(entity);
        audit.Record(AuditActions.ModelRegister, actor, "model", m.ModelId.ToString(), AuditResults.Success, new { modelVersion = m.ModelVersion, m.Algorithm, m.ValidationThreshold });
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Model {ModelVersion} registered by {Actor}", m.ModelVersion, actor);
        return OperationResult<ModelDto>.Ok(ModelDto.From(entity));
    }

    public async Task<OperationResult<ModelDto>> ActivateAsync(Guid modelId, string actor, CancellationToken cancellationToken)
    {
        var model = await db.ModelVersions.FirstOrDefaultAsync(m => m.ModelId == modelId, cancellationToken);
        if (model is null)
        {
            return OperationResult<ModelDto>.NotFound("Model not found.");
        }

        if (model.IsActive)
        {
            return OperationResult<ModelDto>.Ok(ModelDto.From(model));
        }

        // 1) Local validation: schema compatibility, eligibility and metadata completeness.
        var problems = new List<string>();
        if (!FeatureSchema.IsCompatible(model.FeatureSchemaVersion))
        {
            problems.Add($"feature schema '{model.FeatureSchemaVersion}' is incompatible with '{FeatureSchema.CurrentVersion}'");
        }

        if (!model.ProductionEligible)
        {
            problems.Add("model is a benchmark reference (not production eligible)");
        }

        if (double.IsNaN(model.ValidationThreshold) || double.IsInfinity(model.ValidationThreshold))
        {
            problems.Add("validation threshold is missing");
        }

        if (string.IsNullOrWhiteSpace(model.ArtifactSha256))
        {
            problems.Add("artifact hash is missing");
        }

        if (problems.Count > 0)
        {
            await audit.RecordNowAsync(AuditActions.ModelActivate, actor, "model", modelId.ToString(), AuditResults.Rejected, new { model.Version, problems }, cancellationToken);
            return OperationResult<ModelDto>.Invalid("Activation rejected: " + string.Join("; ", problems));
        }

        // 2) Remote verification: the ML service checks the artifact exists, matches its hash, loads and self-tests.
        var verified = await ml.ActivateModelAsync(model.Version, cancellationToken);
        if (!verified.IsSuccess)
        {
            await audit.RecordNowAsync(AuditActions.ModelActivate, actor, "model", modelId.ToString(), AuditResults.Failed, new { model.Version, status = verified.Status.ToString(), error = verified.Error }, cancellationToken);
            return verified.Status is MlCallStatus.Unavailable or MlCallStatus.Unauthorized
                ? OperationResult<ModelDto>.Unavailable($"ML service could not verify the artifact: {verified.Error}")
                : OperationResult<ModelDto>.Invalid($"Artifact verification failed: {verified.Error}");
        }

        var remote = verified.Value!;
        if (!string.Equals(remote.ArtifactSha256, model.ArtifactSha256, StringComparison.OrdinalIgnoreCase)
            || Math.Abs(remote.ValidationThreshold - model.ValidationThreshold) > 1e-9
            || !remote.ArtifactExists)
        {
            await ml.DeactivateModelAsync(model.Version, cancellationToken);
            await audit.RecordNowAsync(AuditActions.ModelActivate, actor, "model", modelId.ToString(), AuditResults.Rejected, new { model.Version, reason = "artifact_or_threshold_mismatch" }, cancellationToken);
            return OperationResult<ModelDto>.Invalid("Activation rejected: the artifact hash or threshold no longer matches the registered metadata.");
        }

        // 3) Exactly one active model per feature schema.
        var now = clock.GetUtcNow().UtcDateTime;
        var previouslyActive = await db.ModelVersions.Where(m => m.IsActive && m.FeatureSchemaVersion == model.FeatureSchemaVersion).ToListAsync(cancellationToken);
        foreach (var previous in previouslyActive)
        {
            previous.Deactivate(actor, now);
            audit.Record(AuditActions.ModelDeactivate, actor, "model", previous.ModelId.ToString(), AuditResults.Success, new { previous.Version, reason = "superseded", supersededBy = model.Version });
        }

        model.Activate(actor, now);
        audit.Record(AuditActions.ModelActivate, actor, "model", modelId.ToString(), AuditResults.Success, new { model.Version, model.Algorithm, model.ValidationThreshold });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var previous in previouslyActive)
        {
            await ml.DeactivateModelAsync(previous.Version, cancellationToken);
        }

        // Windows that waited for a model are retried immediately.
        await RequeueWaitingWindowsAsync(now, cancellationToken);
        logger.LogInformation("Model {ModelVersion} activated by {Actor}", model.Version, actor);
        return OperationResult<ModelDto>.Ok(ModelDto.From(model));
    }

    public async Task<OperationResult<ModelDto>> DeactivateAsync(Guid modelId, string actor, CancellationToken cancellationToken)
    {
        var model = await db.ModelVersions.FirstOrDefaultAsync(m => m.ModelId == modelId, cancellationToken);
        if (model is null)
        {
            return OperationResult<ModelDto>.NotFound("Model not found.");
        }

        if (!model.IsActive)
        {
            return OperationResult<ModelDto>.Ok(ModelDto.From(model));
        }

        model.Deactivate(actor, clock.GetUtcNow().UtcDateTime);
        audit.Record(AuditActions.ModelDeactivate, actor, "model", modelId.ToString(), AuditResults.Success, new { model.Version });
        await db.SaveChangesAsync(cancellationToken);

        var remote = await ml.DeactivateModelAsync(model.Version, cancellationToken);
        if (!remote.IsSuccess)
        {
            // The database is authoritative: the backend never sends windows to an inactive model.
            logger.LogWarning("Model {ModelVersion} deactivated in database but ML service deactivation failed: {Error}", model.Version, remote.Error);
        }

        return OperationResult<ModelDto>.Ok(ModelDto.From(model));
    }

    /// <summary>Explicit retraining request. New versions are registered inactive in the ML registry.</summary>
    public async Task<OperationResult<TrainingJobStatus>> RetrainAsync(RetrainRequest request, string actor, CancellationToken cancellationToken)
    {
        if (!TrainableAlgorithms.Contains(request.Algorithm))
        {
            return OperationResult<TrainingJobStatus>.Invalid($"Algorithm must be one of: {string.Join(", ", TrainableAlgorithms)}.");
        }

        IReadOnlyList<IReadOnlyDictionary<string, double>>? rows = null;
        if (request.Source == "feature-windows")
        {
            if (request.Algorithm is "random_forest" or "all")
            {
                return OperationResult<TrainingJobStatus>.Invalid("Feature-window retraining is unsupervised; choose isolation_forest, lof or ocsvm.");
            }

            if (request.TrainingPeriodStartUtc is null || request.TrainingPeriodEndUtc is null || request.TrainingPeriodEndUtc <= request.TrainingPeriodStartUtc)
            {
                return OperationResult<TrainingJobStatus>.Invalid("A valid training period is required for feature-window retraining.");
            }

            var start = DateTime.SpecifyKind(request.TrainingPeriodStartUtc.Value, DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(request.TrainingPeriodEndUtc.Value, DateTimeKind.Utc);

            // Exclude known incident periods (report §3.9): windows flagged as anomalies unless an engineer
            // reviewed them as benign / false positive.
            var excluded = db.Anomalies
                .Where(a => a.ReviewState != ReviewState.FalsePositive && a.ReviewState != ReviewState.BenignChange)
                .Select(a => a.WindowId);
            var windows = await db.FeatureWindows.AsNoTracking()
                .Where(w => w.WindowStartUtc >= start && w.WindowEndUtc <= end
                            && w.FeatureSchemaVersion == FeatureSchema.CurrentVersion
                            && !excluded.Contains(w.WindowId))
                .OrderBy(w => w.WindowStartUtc)
                .ToListAsync(cancellationToken);

            if (windows.Count < 200)
            {
                return OperationResult<TrainingJobStatus>.Invalid($"At least 200 normal feature windows are required in the training period (found {windows.Count}).");
            }

            rows = windows.Select(w => w.Features.ToDictionary()).ToList();
        }
        else if (request.Source != "benchmark")
        {
            return OperationResult<TrainingJobStatus>.Invalid("Source must be 'benchmark' or 'feature-windows'.");
        }

        var job = await ml.StartTrainingAsync(
            new TrainingJobRequest(request.Algorithm, request.Source, rows, request.TrainingPeriodStartUtc, request.TrainingPeriodEndUtc, actor),
            cancellationToken);

        await audit.RecordNowAsync(
            AuditActions.ModelRetrain,
            actor,
            "training-job",
            job.Value?.JobId,
            job.IsSuccess ? AuditResults.Success : AuditResults.Failed,
            new { request.Algorithm, request.Source, rowCount = rows?.Count, error = job.Error },
            cancellationToken);

        return job.IsSuccess
            ? OperationResult<TrainingJobStatus>.Ok(job.Value!)
            : job.Status == MlCallStatus.Rejected
                ? OperationResult<TrainingJobStatus>.Invalid(job.Error ?? "Rejected by ML service.")
                : OperationResult<TrainingJobStatus>.Unavailable(job.Error ?? "ML service unavailable.");
    }

    public async Task<OperationResult<TrainingJobStatus>> GetTrainingJobAsync(string jobId, CancellationToken cancellationToken)
    {
        var job = await ml.GetTrainingJobAsync(jobId, cancellationToken);
        return job.Status switch
        {
            MlCallStatus.Success => OperationResult<TrainingJobStatus>.Ok(job.Value!),
            MlCallStatus.ModelNotFound => OperationResult<TrainingJobStatus>.NotFound("Training job not found."),
            _ => OperationResult<TrainingJobStatus>.Unavailable(job.Error ?? "ML service unavailable."),
        };
    }

    private async Task RequeueWaitingWindowsAsync(DateTime now, CancellationToken cancellationToken)
    {
        var waiting = await db.FeatureWindows
            .Where(w => w.ScoringStatus == ScoringStatus.Deferred)
            .Take(10_000)
            .ToListAsync(cancellationToken);
        foreach (var w in waiting)
        {
            w.RequeueScoring(now);
        }

        if (waiting.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<string> ValidateMetadata(RegistryModelMetadata m, bool requireProductionEligible)
    {
        var problems = new List<string>();
        if (m.ModelId == Guid.Empty)
        {
            problems.Add("model id missing");
        }

        if (string.IsNullOrWhiteSpace(m.Algorithm))
        {
            problems.Add("algorithm missing");
        }

        if (string.IsNullOrWhiteSpace(m.FeatureSchemaVersion))
        {
            problems.Add("feature schema missing");
        }

        if (FeatureSchema.IsCompatible(m.FeatureSchemaVersion) && !m.FeatureNames.SequenceEqual(FeatureSchema.OrderedFeatureNames))
        {
            problems.Add("feature order does not match ops-v1");
        }

        if (double.IsNaN(m.ValidationThreshold) || double.IsInfinity(m.ValidationThreshold))
        {
            problems.Add("validation threshold missing");
        }

        if (string.IsNullOrWhiteSpace(m.ArtifactSha256) || string.IsNullOrWhiteSpace(m.ArtifactPath))
        {
            problems.Add("artifact metadata missing");
        }

        if (!m.ArtifactExists)
        {
            problems.Add("artifact file not found in registry storage");
        }

        if (requireProductionEligible && !m.ProductionEligible)
        {
            problems.Add("not production eligible");
        }

        return problems;
    }
}
