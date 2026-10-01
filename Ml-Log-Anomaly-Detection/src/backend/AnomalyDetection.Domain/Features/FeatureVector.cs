namespace AnomalyDetection.Domain.Features;

/// <summary>The eight <c>ops-v1</c> window-level features (report Table 3.3).</summary>
public sealed record FeatureVector(
    int RequestCount,
    double ErrorRate,
    double AvgDurationMs,
    double P95DurationMs,
    double AuthFailureRate,
    int DependencyFailureCount,
    int RetryCount,
    double EndpointEntropy)
{
    /// <summary>Values in <see cref="FeatureSchema.OrderedFeatureNames"/> order.</summary>
    public IReadOnlyList<double> ToOrderedArray() =>
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

    /// <summary>Name → value map using the canonical feature names (used for the scoring contract).</summary>
    public IReadOnlyDictionary<string, double> ToDictionary()
    {
        var values = ToOrderedArray();
        var result = new Dictionary<string, double>(FeatureSchema.OrderedFeatureNames.Count, StringComparer.Ordinal);
        for (var i = 0; i < values.Count; i++)
        {
            result[FeatureSchema.OrderedFeatureNames[i]] = values[i];
        }

        return result;
    }
}
