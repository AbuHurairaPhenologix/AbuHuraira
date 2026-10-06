namespace AnomalyDetection.Api.Auth;

public static class Roles
{
    public const string Engineer = "Engineer";
    public const string Administrator = "Administrator";
}

public static class Policies
{
    /// <summary>Inspect and review anomalies (Engineer or Administrator).</summary>
    public const string Engineer = "EngineerAccess";

    /// <summary>Model lifecycle, retraining, pipeline control, audit (Administrator only).</summary>
    public const string Administrator = "AdministratorAccess";
}

/// <summary>
/// Authentication configuration (section <c>Auth</c>). <c>Development</c> mode issues tokens locally for configured
/// demo users; <c>Oidc</c> mode validates tokens from an external OpenID Connect authority (production).
/// Secrets are supplied through environment variables, never source code.
/// </summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string Mode { get; set; } = "Development";

    public string Issuer { get; set; } = "anomaly-detection-dev";

    public string Audience { get; set; } = "anomaly-detection-api";

    public string SigningKey { get; set; } = string.Empty;

    public int TokenLifetimeMinutes { get; set; } = 120;

    public string? DemoEngineerPassword { get; set; }

    public string? DemoAdminPassword { get; set; }

    /// <summary>OIDC authority (Oidc mode).</summary>
    public string? Authority { get; set; }

    public string RoleClaimType { get; set; } = "roles";

    public bool IsOidc => string.Equals(Mode, "Oidc", StringComparison.OrdinalIgnoreCase);
}

/// <summary>Service credential for event producers posting to <c>POST /api/v1/events</c>.</summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    public string ApiKey { get; set; } = string.Empty;
}
