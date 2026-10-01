namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// A registered, immutable model artifact (report §3.12, §4.6). Identifiers never change; only activation state does.
/// Historical anomalies keep a foreign key to the model version that produced them.
/// </summary>
public sealed class ModelVersion
{
    private ModelVersion()
    {
    }

    public ModelVersion(
        Guid modelId,
        string version,
        string algorithm,
        string featureSchemaVersion,
        DateTime? trainingPeriodStartUtc,
        DateTime? trainingPeriodEndUtc,
        int randomSeed,
        string libraryVersionsJson,
        string parametersJson,
        double validationThreshold,
        string thresholdObjective,
        string artifactPath,
        string artifactSha256,
        bool productionEligible,
        string? validationMetricsJson,
        DateTime trainedAtUtc,
        DateTime registeredAtUtc,
        string registeredBy)
    {
        ModelId = modelId;
        Version = version;
        Algorithm = algorithm;
        FeatureSchemaVersion = featureSchemaVersion;
        TrainingPeriodStartUtc = trainingPeriodStartUtc;
        TrainingPeriodEndUtc = trainingPeriodEndUtc;
        RandomSeed = randomSeed;
        LibraryVersionsJson = libraryVersionsJson;
        ParametersJson = parametersJson;
        ValidationThreshold = validationThreshold;
        ThresholdObjective = thresholdObjective;
        ArtifactPath = artifactPath;
        ArtifactSha256 = artifactSha256;
        ProductionEligible = productionEligible;
        ValidationMetricsJson = validationMetricsJson;
        TrainedAtUtc = trainedAtUtc;
        CreatedAtUtc = registeredAtUtc;
        RegisteredBy = registeredBy;
        IsActive = false;
    }

    public Guid ModelId { get; private set; }

    /// <summary>Human-readable, unique version label, e.g. <c>ocsvm-ops-v1-20261001t120000</c>.</summary>
    public string Version { get; private set; } = string.Empty;

    public string Algorithm { get; private set; } = string.Empty;

    public string FeatureSchemaVersion { get; private set; } = string.Empty;

    public DateTime? TrainingPeriodStartUtc { get; private set; }

    public DateTime? TrainingPeriodEndUtc { get; private set; }

    public int RandomSeed { get; private set; }

    public string LibraryVersionsJson { get; private set; } = "{}";

    public string ParametersJson { get; private set; } = "{}";

    public double ValidationThreshold { get; private set; }

    public string ThresholdObjective { get; private set; } = string.Empty;

    /// <summary>Registry-relative artifact location (never a caller-supplied path).</summary>
    public string ArtifactPath { get; private set; } = string.Empty;

    public string ArtifactSha256 { get; private set; } = string.Empty;

    /// <summary>False for the supervised Random Forest reference, which must not be activated for production scoring.</summary>
    public bool ProductionEligible { get; private set; }

    public string? ValidationMetricsJson { get; private set; }

    public DateTime TrainedAtUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public string RegisteredBy { get; private set; } = string.Empty;

    public bool IsActive { get; private set; }

    public DateTime? ActivatedAtUtc { get; private set; }

    public string? ActivatedBy { get; private set; }

    public DateTime? DeactivatedAtUtc { get; private set; }

    public string? DeactivatedBy { get; private set; }

    public void Activate(string actor, DateTime nowUtc)
    {
        IsActive = true;
        ActivatedAtUtc = nowUtc;
        ActivatedBy = actor;
    }

    public void Deactivate(string actor, DateTime nowUtc)
    {
        IsActive = false;
        DeactivatedAtUtc = nowUtc;
        DeactivatedBy = actor;
    }
}
