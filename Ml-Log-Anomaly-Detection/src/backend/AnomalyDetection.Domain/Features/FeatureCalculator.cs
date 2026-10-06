using AnomalyDetection.Domain.Enums;

namespace AnomalyDetection.Domain.Features;

/// <summary>Minimal projection of a normalized event needed to compute window features.</summary>
public sealed record FeatureInputEvent(
    string EventId,
    EventType EventType,
    string EndpointGroup,
    int? StatusCode,
    double? DurationMs,
    bool ErrorFlag,
    AuthenticationResult AuthenticationResult,
    int RetryCount);

/// <summary>
/// Computes the eight <c>ops-v1</c> features for one service + environment + observation window.
/// Pure and deterministic: the same events (in any order) always produce the same vector.
/// Formulas are documented in docs/ml-pipeline.md and mirrored by <c>app/datasets/features.py</c>.
/// </summary>
public static class FeatureCalculator
{
    public static FeatureVector Calculate(IEnumerable<FeatureInputEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);

        // Defensive de-duplication (TC-09): an event identifier contributes at most once to an aggregate.
        var unique = events
            .GroupBy(e => e.EventId, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.EventId, StringComparer.Ordinal)
            .ToList();

        var requests = unique.Where(e => e.EventType == EventType.HttpRequest).ToList();
        var requestCount = requests.Count;

        var errorRate = requestCount == 0 ? 0d : (double)requests.Count(e => e.ErrorFlag) / requestCount;

        var durations = requests.Where(e => e.DurationMs.HasValue).Select(e => e.DurationMs!.Value).ToList();
        var avgDuration = durations.Count == 0 ? 0d : durations.Sum() / durations.Count;
        var p95Duration = FeatureStatistics.Percentile(durations, 95d);

        var authFailureRate = requestCount == 0
            ? 0d
            : (double)requests.Count(e => e.AuthenticationResult == AuthenticationResult.Failure) / requestCount;

        var dependencyFailures = unique.Count(e => e.EventType == EventType.DependencyCall && e.ErrorFlag);
        var retries = unique.Sum(e => Math.Max(0, e.RetryCount));
        var entropy = FeatureStatistics.ShannonEntropyBits(requests.Select(e => e.EndpointGroup));

        return new FeatureVector(
            RequestCount: requestCount,
            ErrorRate: errorRate,
            AvgDurationMs: avgDuration,
            P95DurationMs: p95Duration,
            AuthFailureRate: authFailureRate,
            DependencyFailureCount: dependencyFailures,
            RetryCount: retries,
            EndpointEntropy: entropy);
    }
}
