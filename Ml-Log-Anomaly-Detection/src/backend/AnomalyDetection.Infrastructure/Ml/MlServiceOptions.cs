namespace AnomalyDetection.Infrastructure.Ml;

/// <summary>Configuration for the internal Python ML scoring service (section <c>MlService</c>).</summary>
public sealed class MlServiceOptions
{
    public const string SectionName = "MlService";

    public string BaseUrl { get; set; } = "http://localhost:8000";

    /// <summary>Per-attempt timeout for scoring and model calls.</summary>
    public int AttemptTimeoutSeconds { get; set; } = 5;

    /// <summary>Overall timeout including retries.</summary>
    public int TotalTimeoutSeconds { get; set; } = 20;

    public int MaxRetryAttempts { get; set; } = 2;

    public int CircuitBreakDurationSeconds { get; set; } = 30;

    /// <summary>Timeout for administrative calls such as starting a training job (no retries).</summary>
    public int AdminTimeoutSeconds { get; set; } = 30;

    /// <summary>HS256 signing key shared with the ML service for short-lived service tokens (≥ 32 bytes).</summary>
    public string ServiceTokenSigningKey { get; set; } = string.Empty;

    public string ServiceTokenIssuer { get; set; } = "anomaly-backend";

    public string ServiceTokenAudience { get; set; } = "ml-scoring";

    public int ServiceTokenLifetimeSeconds { get; set; } = 300;
}
