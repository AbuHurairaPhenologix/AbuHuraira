namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// The result of scoring one feature window with one model version — persisted for every window, including those
/// below the threshold (TC-01 evidence: "API response + persisted score"). The threshold actually used is stored.
/// </summary>
public sealed class ScoringRecord
{
    private ScoringRecord()
    {
    }

    public ScoringRecord(
        Guid windowId,
        Guid modelId,
        string modelVersion,
        string featureSchemaVersion,
        double score,
        double threshold,
        bool isAnomaly,
        string reasonSummary,
        string reasonsJson,
        DateTime scoredAtUtc)
    {
        Id = Guid.NewGuid();
        WindowId = windowId;
        ModelId = modelId;
        ModelVersion = modelVersion;
        FeatureSchemaVersion = featureSchemaVersion;
        Score = score;
        Threshold = threshold;
        IsAnomaly = isAnomaly;
        ReasonSummary = reasonSummary;
        ReasonsJson = reasonsJson;
        ScoredAtUtc = scoredAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid WindowId { get; private set; }

    public FeatureWindow? Window { get; private set; }

    public Guid ModelId { get; private set; }

    public ModelVersion? Model { get; private set; }

    public string ModelVersion { get; private set; } = string.Empty;

    public string FeatureSchemaVersion { get; private set; } = string.Empty;

    public double Score { get; private set; }

    public double Threshold { get; private set; }

    public bool IsAnomaly { get; private set; }

    public string ReasonSummary { get; private set; } = string.Empty;

    /// <summary>Per-feature deviation details returned by the ML service (JSON array).</summary>
    public string ReasonsJson { get; private set; } = "[]";

    public DateTime ScoredAtUtc { get; private set; }
}
