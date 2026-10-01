namespace AnomalyDetection.Domain.Enums;

/// <summary>Canonical event categories (report §3.4 "explicit event categories").</summary>
public enum EventType
{
    HttpRequest,
    Authentication,
    DependencyCall,
    BackgroundJob,
}

/// <summary>Normalized authentication outcome recorded on an event.</summary>
public enum AuthenticationResult
{
    None,
    Success,
    Failure,
}

/// <summary>Engineering review outcome of an anomaly (report §4.9 / §4.10).</summary>
public enum ReviewState
{
    Unreviewed,
    ConfirmedIssue,
    BenignChange,
    FalsePositive,
    DuplicateAlert,
    InsufficientEvidence,
}

/// <summary>Lifecycle of an operational event inside the window pipeline.</summary>
public enum EventProcessingState
{
    /// <summary>Persisted, waiting for its observation window to close.</summary>
    Pending,

    /// <summary>Included in a finalized feature window.</summary>
    Aggregated,

    /// <summary>Arrived after its window was finalized; kept for investigation, excluded from features (TC-10).</summary>
    Late,
}

/// <summary>Scoring status persisted on each feature window (background job state).</summary>
public enum ScoringStatus
{
    Pending,
    Scored,

    /// <summary>ML service unavailable or no active model; will be retried (TC-04).</summary>
    Deferred,

    /// <summary>The ML service rejected the window as invalid; not retried automatically.</summary>
    Rejected,
}

public static class EnumTokens
{
    public static string ToToken(this EventType value) => value switch
    {
        EventType.HttpRequest => "http_request",
        EventType.Authentication => "authentication",
        EventType.DependencyCall => "dependency_call",
        EventType.BackgroundJob => "background_job",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static string ToToken(this AuthenticationResult value) => value switch
    {
        AuthenticationResult.None => "none",
        AuthenticationResult.Success => "success",
        AuthenticationResult.Failure => "failure",
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    public static EventType ParseEventType(string token) => token switch
    {
        "http_request" => EventType.HttpRequest,
        "authentication" => EventType.Authentication,
        "dependency_call" => EventType.DependencyCall,
        "background_job" => EventType.BackgroundJob,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown event type token."),
    };

    public static AuthenticationResult ParseAuthenticationResult(string token) => token switch
    {
        "none" => AuthenticationResult.None,
        "success" => AuthenticationResult.Success,
        "failure" => AuthenticationResult.Failure,
        _ => throw new ArgumentOutOfRangeException(nameof(token), token, "Unknown authentication result token."),
    };
}
