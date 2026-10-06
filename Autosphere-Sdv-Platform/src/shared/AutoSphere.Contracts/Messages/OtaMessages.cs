using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Contracts.Messages;

/// <summary>
/// Published by the backend to <c>autosphere/vehicles/{vehicleId}/ota/command</c>.
/// </summary>
/// <remarks>
/// Simplification: the (small, simulated) package payload is embedded as Base64. A production system would
/// send a short-lived download URL to a CDN instead. Verification on the vehicle is independent of the
/// transport: the manifest fields below are re-assembled into a <see cref="PackageManifest"/> and checked
/// against the signature with the vehicle's trusted public key.
/// </remarks>
public sealed record OtaUpdateCommand : CorrelatedVehicleMessage
{
    public required Guid DeploymentId { get; init; }

    public required string TargetEcuId { get; init; }

    public required Guid PackageId { get; init; }

    public required EcuType TargetEcuType { get; init; }

    public required string Version { get; init; }

    public required string MinimumCompatibleVersion { get; init; }

    public required string PayloadSha256 { get; init; }

    public required long PayloadSize { get; init; }

    public required DateTimeOffset PackageCreatedAt { get; init; }

    public required string SignatureBase64 { get; init; }

    public required string PayloadBase64 { get; init; }

    /// <summary>How long the gateway observes the ECU after the restart before confirming the update.</summary>
    public int HealthCheckSeconds { get; init; } = 8;
}

/// <summary>Published by the gateway to <c>autosphere/vehicles/{vehicleId}/ota/status</c> on every state change.</summary>
public sealed record OtaStatusMessage : CorrelatedVehicleMessage
{
    public required Guid DeploymentId { get; init; }

    public required string EcuId { get; init; }

    public required OtaUpdateStatus Status { get; init; }

    public int ProgressPercent { get; init; }

    public required string Message { get; init; }

    public string? InstalledVersion { get; init; }

    public string? PreviousVersion { get; init; }

    public string? FailureReason { get; init; }

    public IReadOnlyList<string>? VerificationChecksPassed { get; init; }
}
