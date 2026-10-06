using System.Text.Json;
using AnomalyDetection.Application.Reviews;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;

namespace AnomalyDetection.Application.Queries;

public sealed record FeatureWindowDto(
    Guid WindowId,
    string Service,
    string Environment,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    int WindowSizeMinutes,
    string FeatureSchemaVersion,
    IReadOnlyDictionary<string, double> Features,
    int EventCount,
    int LateEventCount,
    DateTime CreatedAtUtc,
    string ScoringStatus,
    int ScoringAttempts,
    DateTime? NextScoringAttemptUtc,
    string? LastScoringError)
{
    public static FeatureWindowDto From(FeatureWindow w) => new(
        w.WindowId,
        w.ServiceName,
        w.Environment,
        w.WindowStartUtc,
        w.WindowEndUtc,
        w.WindowSizeMinutes,
        w.FeatureSchemaVersion,
        w.Features.ToDictionary(),
        w.EventCount,
        w.LateEventCount,
        w.CreatedAtUtc,
        w.ScoringStatus.ToString(),
        w.ScoringAttempts,
        w.NextScoringAttemptUtc,
        w.LastScoringError);
}

public sealed record ScoringRecordDto(Guid Id, Guid WindowId, Guid ModelId, string ModelVersion, double Score, double Threshold, bool IsAnomaly, string ReasonSummary, DateTime ScoredAtUtc);

public sealed record AnomalyListItemDto(
    Guid AnomalyId,
    Guid WindowId,
    string Service,
    string Environment,
    DateTime WindowStartUtc,
    DateTime WindowEndUtc,
    double Score,
    double Threshold,
    Guid ModelId,
    string ModelVersion,
    string ReviewState,
    string ReasonSummary,
    DateTime CreatedAtUtc);

public sealed record FeatureDeviationDto(string Feature, string Label, double Value, double? BaselineMean, double? BaselineStd, double? ZScore, string? Direction);

public sealed record AnomalyDetailDto(
    AnomalyListItemDto Anomaly,
    FeatureWindowDto Window,
    string ModelAlgorithm,
    double ModelValidationThreshold,
    bool ModelIsActive,
    IReadOnlyList<FeatureDeviationDto> FeatureDeviations,
    IReadOnlyList<string> CorrelationIds,
    IReadOnlyDictionary<string, int> EventTypeCounts,
    IReadOnlyList<ReviewDto> Reviews,
    IReadOnlyList<ScoringRecordDto> OtherScores);

public sealed record AnomalyFilter(
    string? Service,
    string? Environment,
    ReviewState? ReviewState,
    string? ModelVersion,
    DateTime? FromUtc,
    DateTime? ToUtc,
    double? MinScore,
    string? Sort,
    int? Page,
    int? PageSize);

public sealed record TrendPointDto(DateOnly Day, long Windows, long Anomalies);

public sealed record DashboardStatsDto(
    long ProcessedWindows,
    long ScoredWindows,
    long PendingWindows,
    long DeferredWindows,
    long RejectedWindows,
    long Anomalies,
    double AnomalyRate,
    long Unreviewed,
    IReadOnlyDictionary<string, long> ByReviewState,
    long Events,
    long QuarantinedEvents,
    long LateEvents,
    long UnindexedEvents,
    ActiveModelDto? ActiveModel,
    IReadOnlyList<TrendPointDto> Trend,
    IReadOnlyList<ServiceSummaryDto> Services);

public sealed record ActiveModelDto(Guid ModelId, string ModelVersion, string Algorithm, double ValidationThreshold, DateTime? ActivatedAtUtc);

public sealed record ServiceSummaryDto(string Service, string Environment, long Windows, long Anomalies);

public sealed record QuarantinedEventDto(Guid Id, string Source, string? EventId, string? ServiceName, IReadOnlyList<string> ReasonCodes, JsonElement SanitizedPayload, DateTime ReceivedAtUtc);
