using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.Application.Reviews;

public sealed record ReviewDto(Guid Id, Guid AnomalyId, string PreviousState, string Outcome, string? Note, string Reviewer, DateTime CreatedAtUtc)
{
    public static ReviewDto From(AnomalyReview r) => new(r.Id, r.AnomalyId, r.PreviousState.ToString(), r.Outcome.ToString(), r.Note, r.Reviewer, r.CreatedAtUtc);
}

/// <summary>FR-08: records review outcomes and notes as append-only history; audited.</summary>
public sealed class ReviewService(IApplicationDbContext db, AuditService audit, TimeProvider clock)
{
    public async Task<OperationResult<ReviewDto>> ReviewAsync(Guid anomalyId, string outcome, string? note, string reviewer, CancellationToken cancellationToken)
    {
        if (!Enum.TryParse<ReviewState>(outcome, ignoreCase: true, out var state) || !Enum.IsDefined(state))
        {
            return OperationResult<ReviewDto>.Invalid($"Outcome must be one of: {string.Join(", ", Enum.GetNames<ReviewState>())}.");
        }

        if (note is { Length: > AnomalyReview.MaxNoteLength })
        {
            return OperationResult<ReviewDto>.Invalid($"Note must be at most {AnomalyReview.MaxNoteLength} characters.");
        }

        var anomaly = await db.Anomalies.FirstOrDefaultAsync(a => a.AnomalyId == anomalyId, cancellationToken);
        if (anomaly is null)
        {
            return OperationResult<ReviewDto>.NotFound("Anomaly not found.");
        }

        var previous = anomaly.ReviewState;
        var review = anomaly.Review(state, note, reviewer, clock.GetUtcNow().UtcDateTime);
        db.AnomalyReviews.Add(review);
        audit.Record(AuditActions.AnomalyReview, reviewer, "anomaly", anomalyId.ToString(), AuditResults.Success, new { previous = previous.ToString(), outcome = state.ToString(), hasNote = review.Note is not null });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ResetTracking();
            return OperationResult<ReviewDto>.Conflict("The anomaly was reviewed concurrently by another engineer. Reload and try again.");
        }

        return OperationResult<ReviewDto>.Ok(ReviewDto.From(review));
    }

    public async Task<IReadOnlyList<ReviewDto>?> HistoryAsync(Guid anomalyId, CancellationToken cancellationToken)
    {
        if (!await db.Anomalies.AnyAsync(a => a.AnomalyId == anomalyId, cancellationToken))
        {
            return null;
        }

        return await db.AnomalyReviews.AsNoTracking()
            .Where(r => r.AnomalyId == anomalyId)
            .OrderBy(r => r.CreatedAtUtc)
            .Select(r => new ReviewDto(r.Id, r.AnomalyId, r.PreviousState.ToString(), r.Outcome.ToString(), r.Note, r.Reviewer, r.CreatedAtUtc))
            .ToListAsync(cancellationToken);
    }
}
