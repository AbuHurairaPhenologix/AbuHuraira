using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;

namespace AnomalyDetection.Domain.Entities;

/// <summary>
/// A finalized observation window for one service + environment with its <c>ops-v1</c> feature vector
/// (report §3.8: "the feature service stores the computed vector together with its source window").
/// Also carries persisted scoring-job state so scoring can be deferred and retried (TC-04).
/// </summary>
public sealed class FeatureWindow
{
    private FeatureWindow()
    {
    }

    public FeatureWindow(
        ServiceDefinition service,
        DateTime windowStartUtc,
        DateTime windowEndUtc,
        FeatureVector features,
        int eventCount,
        DateTime createdAtUtc)
    {
        WindowId = Guid.NewGuid();
        ServiceDefinitionId = service.Id;
        ServiceName = service.Name;
        Environment = service.Environment;
        WindowStartUtc = windowStartUtc;
        WindowEndUtc = windowEndUtc;
        WindowSizeMinutes = (int)Math.Round((windowEndUtc - windowStartUtc).TotalMinutes);
        FeatureSchemaVersion = FeatureSchema.CurrentVersion;
        RequestCount = features.RequestCount;
        ErrorRate = features.ErrorRate;
        AvgDurationMs = features.AvgDurationMs;
        P95DurationMs = features.P95DurationMs;
        AuthFailureRate = features.AuthFailureRate;
        DependencyFailureCount = features.DependencyFailureCount;
        RetryCount = features.RetryCount;
        EndpointEntropy = features.EndpointEntropy;
        EventCount = eventCount;
        CreatedAtUtc = createdAtUtc;
        ScoringStatus = ScoringStatus.Pending;
        NextScoringAttemptUtc = createdAtUtc;
    }

    public Guid WindowId { get; private set; }

    public Guid ServiceDefinitionId { get; private set; }

    public ServiceDefinition? Service { get; private set; }

    public string ServiceName { get; private set; } = string.Empty;

    public string Environment { get; private set; } = string.Empty;

    public DateTime WindowStartUtc { get; private set; }

    public DateTime WindowEndUtc { get; private set; }

    public int WindowSizeMinutes { get; private set; }

    public string FeatureSchemaVersion { get; private set; } = FeatureSchema.CurrentVersion;

    public int RequestCount { get; private set; }

    public double ErrorRate { get; private set; }

    public double AvgDurationMs { get; private set; }

    public double P95DurationMs { get; private set; }

    public double AuthFailureRate { get; private set; }

    public int DependencyFailureCount { get; private set; }

    public int RetryCount { get; private set; }

    public double EndpointEntropy { get; private set; }

    public int EventCount { get; private set; }

    public int LateEventCount { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public ScoringStatus ScoringStatus { get; private set; }

    public int ScoringAttempts { get; private set; }

    public DateTime? NextScoringAttemptUtc { get; private set; }

    public string? LastScoringError { get; private set; }

    public DateTime? LastScoredAtUtc { get; private set; }

    public FeatureVector Features => new(
        RequestCount,
        ErrorRate,
        AvgDurationMs,
        P95DurationMs,
        AuthFailureRate,
        DependencyFailureCount,
        RetryCount,
        EndpointEntropy);

    public void RecordLateEvents(int count) => LateEventCount += count;

    public void MarkScored(DateTime scoredAtUtc)
    {
        ScoringStatus = ScoringStatus.Scored;
        ScoringAttempts++;
        LastScoredAtUtc = scoredAtUtc;
        NextScoringAttemptUtc = null;
        LastScoringError = null;
    }

    /// <summary>Defers scoring with capped exponential back-off; the window is retried later.</summary>
    public void DeferScoring(string reason, DateTime nowUtc, TimeSpan baseDelay, TimeSpan maxDelay, bool countAttempt = true)
    {
        if (countAttempt)
        {
            ScoringAttempts++;
        }

        ScoringStatus = ScoringStatus.Deferred;
        var exponent = Math.Min(Math.Max(ScoringAttempts - 1, 0), 16);
        var delay = TimeSpan.FromTicks(Math.Min(baseDelay.Ticks * (1L << exponent), maxDelay.Ticks));
        NextScoringAttemptUtc = nowUtc + delay;
        LastScoringError = Truncate(reason, 1000);
    }

    public void RejectScoring(string reason)
    {
        ScoringAttempts++;
        ScoringStatus = ScoringStatus.Rejected;
        NextScoringAttemptUtc = null;
        LastScoringError = Truncate(reason, 1000);
    }

    /// <summary>Explicit administrative re-queue (e.g. after fixing a rejected contract).</summary>
    public void RequeueScoring(DateTime nowUtc)
    {
        ScoringStatus = ScoringStatus.Pending;
        NextScoringAttemptUtc = nowUtc;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
