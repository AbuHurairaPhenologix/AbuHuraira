using System.Text.Json;

namespace AnomalyDetection.Application.Abstractions;

/// <summary>One window in a batch scoring request (report Appendix "API Contract").</summary>
public sealed record ScoreItem(
    Guid WindowId,
    string Service,
    string Environment,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    IReadOnlyDictionary<string, double> Features);

public sealed record FeatureReason(string Feature, double Value, double BaselineMean, double BaselineStd, double ZScore, string Direction);

public sealed record ScoreResult(
    Guid WindowId,
    double Score,
    double Threshold,
    bool IsAnomaly,
    string ModelVersion,
    string SchemaVersion,
    string ReasonSummary,
    IReadOnlyList<FeatureReason> Reasons);

public enum MlCallStatus
{
    Success,

    /// <summary>Timeout, connection failure, 5xx or open circuit: defer and retry later.</summary>
    Unavailable,

    /// <summary>The model version is registered but not active in the ML service (409).</summary>
    ModelNotActive,

    /// <summary>The model version is not registered in the ML registry (404).</summary>
    ModelNotFound,

    /// <summary>The ML service rejected the request contract (400/422).</summary>
    Rejected,

    /// <summary>The ML service rejected the service credential (401/403).</summary>
    Unauthorized,
}

public sealed record MlCallResult<T>(MlCallStatus Status, T? Value, string? Error)
{
    public bool IsSuccess => Status == MlCallStatus.Success;

    public static MlCallResult<T> Ok(T value) => new(MlCallStatus.Success, value, null);

    public static MlCallResult<T> Fail(MlCallStatus status, string error) => new(status, default, error);
}

/// <summary>Metadata of a registered artifact as reported by the ML registry.</summary>
public sealed record RegistryModelMetadata(
    Guid ModelId,
    string ModelVersion,
    string Algorithm,
    string FeatureSchemaVersion,
    IReadOnlyList<string> FeatureNames,
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
    DateTime CreatedAtUtc,
    bool ArtifactExists,
    bool ActiveInService,
    bool Recommended);

public sealed record TrainingJobRequest(
    string Algorithm,
    string Source,
    IReadOnlyList<IReadOnlyDictionary<string, double>>? Rows,
    DateTime? TrainingPeriodStartUtc,
    DateTime? TrainingPeriodEndUtc,
    string RequestedBy);

public sealed record TrainingJobStatus(string JobId, string Status, string? Algorithm, IReadOnlyList<string> ModelVersions, string? Error, DateTime CreatedAtUtc, DateTime? CompletedAtUtc);

/// <summary>Port to the internal Python ML service. All calls are resilient and never throw on transport failure.</summary>
public interface IMlScoringClient
{
    Task<MlCallResult<IReadOnlyList<ScoreResult>>> ScoreBatchAsync(string modelVersion, string schemaVersion, IReadOnlyList<ScoreItem> items, CancellationToken cancellationToken);

    Task<MlCallResult<IReadOnlyList<RegistryModelMetadata>>> ListRegistryAsync(CancellationToken cancellationToken);

    Task<MlCallResult<RegistryModelMetadata>> GetModelAsync(string modelVersion, CancellationToken cancellationToken);

    Task<MlCallResult<RegistryModelMetadata>> ActivateModelAsync(string modelVersion, CancellationToken cancellationToken);

    Task<MlCallResult<bool>> DeactivateModelAsync(string modelVersion, CancellationToken cancellationToken);

    Task<MlCallResult<TrainingJobStatus>> StartTrainingAsync(TrainingJobRequest request, CancellationToken cancellationToken);

    Task<MlCallResult<TrainingJobStatus>> GetTrainingJobAsync(string jobId, CancellationToken cancellationToken);
}
