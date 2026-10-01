namespace AnomalyDetection.Domain.Features;

/// <summary>
/// The versioned feature contract shared by the feature service and the ML scoring service (report §3.8, §4.5).
/// Changing the order or meaning of any feature requires a new schema version, because it invalidates model artifacts.
/// Mirrors <c>config/feature-schema.ops-v1.json</c> and <c>app/core/feature_schema.py</c>.
/// </summary>
public static class FeatureSchema
{
    public const string CurrentVersion = "ops-v1";

    public const string RequestCount = "request_count";
    public const string ErrorRate = "error_rate";
    public const string AvgDurationMs = "avg_duration_ms";
    public const string P95DurationMs = "p95_duration_ms";
    public const string AuthFailureRate = "auth_failure_rate";
    public const string DependencyFailureCount = "dependency_failure_count";
    public const string RetryCount = "retry_count";
    public const string EndpointEntropy = "endpoint_entropy";

    /// <summary>Explicit, ordered feature list. The index of each name is its column position in the model input.</summary>
    public static readonly IReadOnlyList<string> OrderedFeatureNames =
    [
        RequestCount,
        ErrorRate,
        AvgDurationMs,
        P95DurationMs,
        AuthFailureRate,
        DependencyFailureCount,
        RetryCount,
        EndpointEntropy,
    ];

    public static bool IsCompatible(string? schemaVersion) =>
        string.Equals(schemaVersion, CurrentVersion, StringComparison.Ordinal);
}
