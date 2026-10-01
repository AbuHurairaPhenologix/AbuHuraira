using System.Globalization;
using AnomalyDetection.Application.Abstractions;

namespace AnomalyDetection.Application.Scoring;

/// <summary>
/// Builds a compact, non-causal reason summary from the most unusual feature deviations (report §2.8).
/// It describes *what* differs from the model's training baseline, never *why* — ML is decision support.
/// </summary>
public static class ReasonSummaryBuilder
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["request_count"] = "request count",
        ["error_rate"] = "error rate",
        ["avg_duration_ms"] = "average response time",
        ["p95_duration_ms"] = "p95 response time",
        ["auth_failure_rate"] = "authentication-failure rate",
        ["dependency_failure_count"] = "dependency failure count",
        ["retry_count"] = "retry count",
        ["endpoint_entropy"] = "endpoint diversity (entropy)",
    };

    public static string Label(string feature) => Labels.TryGetValue(feature, out var label) ? label : feature;

    public static string Build(IReadOnlyList<FeatureReason> reasons, double minAbsZ = 2.0, int max = 3)
    {
        var notable = reasons
            .Where(r => Math.Abs(r.ZScore) >= minAbsZ)
            .OrderByDescending(r => Math.Abs(r.ZScore))
            .ThenBy(r => r.Feature, StringComparer.Ordinal)
            .Take(max)
            .ToList();

        if (notable.Count == 0)
        {
            return "No single feature deviates strongly from the model's baseline; the score reflects a combination of moderate deviations. Review the feature vector and related events.";
        }

        var parts = notable.Select(r =>
            string.Create(CultureInfo.InvariantCulture, $"{(r.ZScore >= 0 ? "elevated" : "reduced")} {Label(r.Feature)} ({r.Value:0.###} vs baseline {r.BaselineMean:0.###})"));
        var text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..] + " compared with the model's training baseline. Indicates where to investigate; not a root-cause determination.";
    }
}
