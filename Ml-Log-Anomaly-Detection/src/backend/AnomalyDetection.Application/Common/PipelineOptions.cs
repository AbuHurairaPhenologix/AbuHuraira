using AnomalyDetection.Domain.Windowing;

namespace AnomalyDetection.Application.Common;

/// <summary>Strongly typed pipeline configuration (section <c>Pipeline</c>).</summary>
public sealed class PipelineOptions
{
    public const string SectionName = "Pipeline";

    /// <summary>Observation window size: 1, 5 (default) or 15 minutes (report §3.7).</summary>
    public int WindowSizeMinutes { get; set; } = WindowAligner.DefaultWindowSizeMinutes;

    /// <summary>Grace period after a window ends before it is finalized; later events follow the late policy.</summary>
    public int AllowedLatenessSeconds { get; set; } = 120;

    /// <summary>Events further than this in the future are quarantined (clock-skew guard).</summary>
    public int MaxFutureSkewSeconds { get; set; } = 300;

    public int ScoringBatchSize { get; set; } = 100;

    public int ScoringRetryBaseDelaySeconds { get; set; } = 15;

    public int ScoringRetryMaxDelaySeconds { get; set; } = 900;

    public int AggregationIntervalSeconds { get; set; } = 15;

    public int ScoringIntervalSeconds { get; set; } = 15;

    public int IndexingIntervalSeconds { get; set; } = 5;

    public int IndexingBatchSize { get; set; } = 500;

    public bool EnableBackgroundWorkers { get; set; } = true;

    public int EventQueueCapacity { get; set; } = 10_000;

    /// <summary>Service name used for events captured from this backend's own demo workload.</summary>
    public string DemoServiceName { get; set; } = "demo-shop-api";

    public string DemoEnvironment { get; set; } = "development";

    public void Validate()
    {
        if (!WindowAligner.IsSupported(WindowSizeMinutes))
        {
            throw new InvalidOperationException($"Pipeline:WindowSizeMinutes must be one of 1, 5, 15 (was {WindowSizeMinutes}).");
        }

        if (AllowedLatenessSeconds < 0 || ScoringBatchSize is < 1 or > 500)
        {
            throw new InvalidOperationException("Pipeline options are out of range.");
        }
    }
}
