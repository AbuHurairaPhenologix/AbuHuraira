using AnomalyDetection.Domain.Enums;

namespace AnomalyDetection.Domain.Entities;

/// <summary>Append-only review history entry for an anomaly (report §4.10 "auditable feedback loop").</summary>
public sealed class AnomalyReview
{
    public const int MaxNoteLength = 4000;

    private AnomalyReview()
    {
    }

    internal AnomalyReview(Guid anomalyId, ReviewState previousState, ReviewState outcome, string? note, string reviewer, DateTime createdAtUtc)
    {
        if (note is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"Review note must be at most {MaxNoteLength} characters.", nameof(note));
        }

        Id = Guid.NewGuid();
        AnomalyId = anomalyId;
        PreviousState = previousState;
        Outcome = outcome;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        Reviewer = reviewer;
        CreatedAtUtc = createdAtUtc;
    }

    public Guid Id { get; private set; }

    public Guid AnomalyId { get; private set; }

    public ReviewState PreviousState { get; private set; }

    public ReviewState Outcome { get; private set; }

    public string? Note { get; private set; }

    public string Reviewer { get; private set; } = string.Empty;

    public DateTime CreatedAtUtc { get; private set; }
}
