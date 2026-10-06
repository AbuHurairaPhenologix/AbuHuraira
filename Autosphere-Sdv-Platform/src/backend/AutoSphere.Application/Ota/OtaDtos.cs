using AutoSphere.Domain.Ota;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Application.Ota;

public sealed record SoftwarePackageDto(
    Guid Id,
    string Name,
    EcuType TargetEcuType,
    string Version,
    string MinimumCompatibleVersion,
    string PayloadSha256,
    long PayloadSize,
    string SignatureBase64,
    string SigningKeyId,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    string? ReleaseNotes,
    string? FirmwareDescription);

public sealed record OtaDeploymentEventDto(OtaUpdateStatus Status, int ProgressPercent, string Message, DateTimeOffset Timestamp);

public sealed record OtaDeploymentDto(
    Guid Id,
    Guid CampaignId,
    string VehicleId,
    string EcuId,
    Guid PackageId,
    string FromVersion,
    string ToVersion,
    string? InstalledVersion,
    OtaUpdateStatus Status,
    int ProgressPercent,
    string? LastMessage,
    string? FailureReason,
    bool SimulateTransportCorruption,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    double? DurationSeconds,
    IReadOnlyList<OtaDeploymentEventDto> Events);

public sealed record OtaCampaignDto(
    Guid Id,
    string Name,
    Guid PackageId,
    string PackageVersion,
    EcuType TargetEcuType,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    bool IsCompleted,
    IReadOnlyList<OtaDeploymentDto> Deployments);

/// <summary>Request to create a package from an uploaded binary.</summary>
public sealed record CreatePackageRequest(string Name, EcuType TargetEcuType, string Version, string MinimumCompatibleVersion, string? ReleaseNotes, byte[] Payload);

/// <summary>Request to generate a simulated firmware image (demo/test helper).</summary>
public sealed record CreateSamplePackageRequest(
    EcuType TargetEcuType,
    string Version,
    string MinimumCompatibleVersion,
    FirmwareBootBehavior BootBehavior = FirmwareBootBehavior.Normal,
    string? ReleaseNotes = null,
    int SizeBytes = 16 * 1024);

public sealed record CreateCampaignRequest(string? Name, Guid PackageId, IReadOnlyList<string> VehicleIds, bool SimulateTransportCorruption = false);

public static class OtaMappings
{
    public static SoftwarePackageDto ToDto(this SoftwarePackage package) => new(package.Id, package.Name, package.TargetEcuType, package.Version,
        package.MinimumCompatibleVersion, package.PayloadSha256, package.PayloadSize, Convert.ToBase64String(package.Signature), package.SigningKeyId,
        package.CreatedAt, package.CreatedBy, package.ReleaseNotes, package.FirmwareDescription);

    public static OtaDeploymentDto ToDto(this OtaDeployment deployment) => new(deployment.Id, deployment.CampaignId, deployment.VehicleId,
        deployment.EcuId, deployment.PackageId, deployment.FromVersion, deployment.ToVersion, deployment.InstalledVersion, deployment.Status,
        deployment.ProgressPercent, deployment.LastMessage, deployment.FailureReason, deployment.SimulateTransportCorruption, deployment.CreatedAt,
        deployment.StartedAt, deployment.CompletedAt,
        deployment.CompletedAt is { } done ? Math.Round((done - (deployment.StartedAt ?? deployment.CreatedAt)).TotalSeconds, 2) : null,
        deployment.Events.OrderBy(e => e.Timestamp).ThenBy(e => e.Id).Select(e => new OtaDeploymentEventDto(e.Status, e.ProgressPercent, e.Message, e.Timestamp)).ToList());
}
