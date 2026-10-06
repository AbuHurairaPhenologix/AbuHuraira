using AnomalyDetection.Domain.Enums;

namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// A reviewable alert for a window whose score exceeded the model's threshold (report §4.4 DDL, extended with a reason
/// summary). Score, threshold and model are immutable: historical anomalies are never rescored with a newer model.
/// </summary>
public sealed class AnomalyRecord
{
    private readonly List<AnomalyReview> _reviews = [];

    private AnomalyRecord()
    {
    }

    public AnomalyRecord(ScoringRecord scoring, FeatureWindow window, DateTime createdAtUtc)
    {
        AnomalyId = Guid.NewGuid();
        WindowId = window.WindowId;
        ModelId = scoring.ModelId;
        ModelVersion = scoring.ModelVersion;
        ScoringRecordId = scoring.Id;
        Score = scoring.Score;
        Threshold = scoring.Threshold;
        IsAnomaly = scoring.IsAnomaly;
        ReasonSummary = scoring.ReasonSummary;
        ServiceName = window.ServiceName;
        Environment = window.Environment;
        WindowStartUtc = window.WindowStartUtc;
        WindowEndUtc = window.WindowEndUtc;
        ReviewState = ReviewState.Unreviewed;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid AnomalyId { get; private set; }

    public Guid WindowId { get; private set; }

    public FeatureWindow? Window { get; private set; }

    public Guid ModelId { get; private set; }

    public ModelVersion? Model { get; private set; }

    public string ModelVersion { get; private set; } = string.Empty;

    public Guid ScoringRecordId { get; private set; }

    public double Score { get; private set; }

    public double Threshold { get; private set; }

    public bool IsAnomaly { get; private set; }

    public ReviewState ReviewState { get; private set; }

    public string ReasonSummary { get; private set; } = string.Empty;

    public string ServiceName { get; private set; } = string.Empty;

    public string Environment { get; private set; } = string.Empty;

    public DateTime WindowStartUtc { get; private set; }

    public DateTime WindowEndUtc { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public DateTime? LastReviewedAtUtc { get; private set; }

    /// <summary>Optimistic-concurrency token (PostgreSQL <c>xmin</c>).</summary>
    public uint RowVersion { get; private set; }

    public IReadOnlyCollection<AnomalyReview> Reviews => _reviews;

    /// <summary>
    /// Records a review outcome. The previous state is preserved in the appended history entry — reviews are never
    /// overwritten or deleted.
    /// </summary>
    public AnomalyReview Review(ReviewState outcome, string? note, string reviewer, DateTime nowUtc)
    {
        if (string.IsNullOrWhiteSpace(reviewer))
        {
            throw new ArgumentException("Reviewer is required.", nameof(reviewer));
        }

        var review = new AnomalyReview(AnomalyId, ReviewState, outcome, note, reviewer, nowUtc);
        _reviews.Add(review);
        ReviewState = outcome;
        LastReviewedAtUtc = nowUtc;
        return review;
    }
}
