namespace AutoSphere.Application.Common;

/// <summary>Role names used for role-based access control.</summary>
public static class Roles
{
    public const string Administrator = "Administrator";
    public const string Engineer = "Engineer";
    public const string Viewer = "Viewer";

    public static IReadOnlyList<string> All { get; } = [Administrator, Engineer, Viewer];
}

/// <summary>Configuration section <c>Telemetry</c>.</summary>
public sealed class TelemetryOptions
{
    public const string SectionName = "Telemetry";

    /// <summary>At most one sample per signal and vehicle is persisted per interval.</summary>
    public int SamplingIntervalSeconds { get; set; } = 1;

    /// <summary>Samples older than this are deleted by the retention job.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>Maximum time range of a history query.</summary>
    public int MaxHistoryHours { get; set; } = 24;
}

/// <summary>Configuration section <c>Diagnostics</c>.</summary>
public sealed class DiagnosticsOptions
{
    public const string SectionName = "Diagnostics";

    /// <summary>How long an HTTP request waits for the vehicle's diagnostic response.</summary>
    public int ResponseTimeoutSeconds { get; set; } = 20;
}

/// <summary>Configuration section <c>Ota</c>.</summary>
public sealed class OtaOptions
{
    public const string SectionName = "Ota";

    /// <summary>Post-install observation period on the vehicle.</summary>
    public int HealthCheckSeconds { get; set; } = 8;

    /// <summary>A deployment without progress for this long is failed by the cloud.</summary>
    public int DeploymentTimeoutMinutes { get; set; } = 5;

    /// <summary>Upper limit for package payloads (simulated ECUs accept 1 MiB).</summary>
    public int MaxPackageSizeBytes { get; set; } = 1024 * 1024;
}

/// <summary>Configuration section <c>FaultInjection</c>.</summary>
public sealed class FaultInjectionOptions
{
    public const string SectionName = "FaultInjection";

    /// <summary>Must only be enabled for simulation/demo environments.</summary>
    public bool Enabled { get; set; }
}
