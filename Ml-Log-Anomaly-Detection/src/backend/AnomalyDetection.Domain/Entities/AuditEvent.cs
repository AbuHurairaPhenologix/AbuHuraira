namespace AnomalyDetection.Domain.Entities;

/// <summary>Security/governance audit trail: model lifecycle, reviews, and administrative authorization failures.</summary>
public sealed class AuditEvent
{
    private AuditEvent()
    {
    }

    public AuditEvent(string action, string actor, string targetType, string? targetId, string result, string metadataJson, DateTime occurredAtUtc)
    {
        Id = Guid.NewGuid();
        Action = action;
        Actor = actor;
        TargetType = targetType;
        TargetId = targetId;
        Result = result;
        MetadataJson = metadataJson;
        OccurredAtUtc = occurredAtUtc;
    }

    public Guid Id { get; private set; }

    /// <summary>e.g. <c>model.activate</c>, <c>anomaly.review</c>, <c>authorization.denied</c>.</summary>
    public string Action { get; private set; } = string.Empty;

    public string Actor { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    /// <summary><c>success</c>, <c>denied</c>, <c>failed</c> or <c>rejected</c>.</summary>
    public string Result { get; private set; } = string.Empty;

    /// <summary>Safe metadata only — never tokens, passwords or raw request bodies.</summary>
    public string MetadataJson { get; private set; } = "{}";

    public DateTime OccurredAtUtc { get; private set; }
}

public static class AuditActions
{
    public const string ModelRegister = "model.register";
    public const string ModelActivate = "model.activate";
    public const string ModelDeactivate = "model.deactivate";
    public const string ModelRetrain = "model.retrain";
    public const string AnomalyReview = "anomaly.review";
    public const string AuthorizationDenied = "authorization.denied";
    public const string PipelineRun = "pipeline.run";
    public const string ScoringRequeue = "scoring.requeue";
}

public static class AuditResults
{
    public const string Success = "success";
    public const string Denied = "denied";
    public const string Failed = "failed";
    public const string Rejected = "rejected";
}
