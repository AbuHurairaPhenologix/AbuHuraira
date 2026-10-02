using AutoSphere.Domain.Common;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Versioning;

namespace AutoSphere.Domain.Ota;

/// <summary>The release of one software package to a set of vehicles.</summary>
public sealed class OtaCampaign
{
    private readonly List<OtaDeployment> _deployments = [];

    private OtaCampaign()
    {
    }

    public Guid Id { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public Guid PackageId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public string CreatedBy { get; private set; } = string.Empty;

    public IReadOnlyCollection<OtaDeployment> Deployments => _deployments;

    public bool IsCompleted => _deployments.Count > 0 && _deployments.All(d => d.Status.IsTerminal());

    public static OtaCampaign Create(string name, SoftwarePackage package, string createdBy, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (package.Signature.Length == 0)
        {
            throw new DomainException("Unsigned packages cannot be deployed.");
        }

        return new OtaCampaign
        {
            Id = Guid.NewGuid(),
            Name = string.IsNullOrWhiteSpace(name) ? $"{package.TargetEcuType} {package.Version}" : name.Trim(),
            PackageId = package.Id,
            CreatedAt = now,
            CreatedBy = createdBy,
        };
    }

    /// <summary>Adds a deployment for one vehicle ECU after checking the cloud-side preconditions.</summary>
    public OtaDeployment AddDeployment(Guid vehicleKey, string vehicleId, string ecuId, string currentVersion, SoftwarePackage package,
        bool simulateTransportCorruption, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(package);
        if (SoftwareVersion.TryParse(currentVersion, out var current) && current >= package.ParsedVersion)
        {
            throw new DomainException($"{vehicleId}/{ecuId} already runs {currentVersion}; package {package.Version} is not newer.");
        }

        var deployment = OtaDeployment.Create(Id, vehicleKey, vehicleId, ecuId, package, currentVersion, simulateTransportCorruption, now);
        _deployments.Add(deployment);
        return deployment;
    }
}

/// <summary>Execution of a campaign on one vehicle ECU.</summary>
public sealed class OtaDeployment
{
    private readonly List<OtaDeploymentEvent> _events = [];

    private OtaDeployment()
    {
    }

    public Guid Id { get; private set; }

    public Guid CampaignId { get; private set; }

    public Guid VehicleKey { get; private set; }

    public string VehicleId { get; private set; } = string.Empty;

    public string EcuId { get; private set; } = string.Empty;

    public Guid PackageId { get; private set; }

    public string FromVersion { get; private set; } = string.Empty;

    public string ToVersion { get; private set; } = string.Empty;

    /// <summary>Version running on the ECU at the end of the deployment.</summary>
    public string? InstalledVersion { get; private set; }

    public OtaUpdateStatus Status { get; private set; }

    public int ProgressPercent { get; private set; }

    public string? LastMessage { get; private set; }

    public string? FailureReason { get; private set; }

    /// <summary>Fault injection: the payload is corrupted in transit to demonstrate verification.</summary>
    public bool SimulateTransportCorruption { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public IReadOnlyCollection<OtaDeploymentEvent> Events => _events;

    public bool IsSuccessful => Status == OtaUpdateStatus.Succeeded;

    internal static OtaDeployment Create(Guid campaignId, Guid vehicleKey, string vehicleId, string ecuId, SoftwarePackage package,
        string currentVersion, bool simulateTransportCorruption, DateTimeOffset now)
    {
        var deployment = new OtaDeployment
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            VehicleKey = vehicleKey,
            VehicleId = vehicleId,
            EcuId = ecuId,
            PackageId = package.Id,
            FromVersion = currentVersion,
            ToVersion = package.Version,
            Status = OtaUpdateStatus.Created,
            SimulateTransportCorruption = simulateTransportCorruption,
            CreatedAt = now,
        };
        deployment._events.Add(OtaDeploymentEvent.Create(deployment.Id, OtaUpdateStatus.Created, 0, "Deployment created", now));
        return deployment;
    }

    /// <summary>Cloud-side transition (e.g. command sent → Pending, timeout → Failed). Throws on invalid transitions.</summary>
    public void TransitionTo(OtaUpdateStatus status, int progress, string message, DateTimeOffset at, string? failureReason = null)
    {
        if (!OtaStateMachine.CanTransition(Status, status))
        {
            throw new DomainException($"OTA deployment cannot move from {Status} to {status}.");
        }

        Apply(status, progress, message, at, failureReason, installedVersion: null);
    }

    /// <summary>
    /// Applies a status reported by the vehicle. Duplicate or out-of-order reports (possible with MQTT QoS 1)
    /// are ignored. Returns true if the deployment changed.
    /// </summary>
    public bool ApplyVehicleReport(OtaUpdateStatus status, int progress, string message, string? installedVersion, string? failureReason, DateTimeOffset at)
    {
        if (Status.IsTerminal() || (status != Status && !OtaStateMachine.IsReachable(Status, status)) || (status == Status && progress <= ProgressPercent))
        {
            return false;
        }

        Apply(status, progress, message, at, failureReason, installedVersion);
        return true;
    }

    private void Apply(OtaUpdateStatus status, int progress, string message, DateTimeOffset at, string? failureReason, string? installedVersion)
    {
        if (status != Status || progress != ProgressPercent)
        {
            _events.Add(OtaDeploymentEvent.Create(Id, status, progress, message, at));
        }

        Status = status;
        ProgressPercent = Math.Clamp(progress, 0, 100);
        LastMessage = message;
        if (status == OtaUpdateStatus.Downloading)
        {
            StartedAt ??= at;
        }

        FailureReason = failureReason ?? FailureReason;
        InstalledVersion = installedVersion ?? InstalledVersion;
        if (status.IsTerminal())
        {
            CompletedAt = at;
            InstalledVersion ??= status == OtaUpdateStatus.Succeeded ? ToVersion : FromVersion;
        }
    }
}

/// <summary>One step in the history of a deployment.</summary>
public sealed class OtaDeploymentEvent
{
    private OtaDeploymentEvent()
    {
    }

    public long Id { get; private set; }

    public Guid DeploymentId { get; private set; }

    public OtaUpdateStatus Status { get; private set; }

    public int ProgressPercent { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public DateTimeOffset Timestamp { get; private set; }

    internal static OtaDeploymentEvent Create(Guid deploymentId, OtaUpdateStatus status, int progress, string message, DateTimeOffset at) => new()
    {
        DeploymentId = deploymentId,
        Status = status,
        ProgressPercent = progress,
        Message = message,
        Timestamp = at,
    };
}
