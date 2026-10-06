using AutoSphere.SharedKernel.Vehicles;

namespace AutoSphere.Domain.Vehicles;

/// <summary>An electronic control unit of a vehicle as known to the cloud.</summary>
public sealed class Ecu
{
    private Ecu()
    {
        // EF Core
    }

    public Guid Id { get; private set; }

    public Guid VehicleKey { get; private set; }

    /// <summary>ECU identifier, unique within the vehicle, e.g. <c>BMS-001</c>.</summary>
    public string EcuId { get; private set; } = string.Empty;

    public EcuType Type { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string SoftwareVersion { get; private set; } = "0.0.0";

    public string? HardwareVersion { get; private set; }

    public EcuStatus Status { get; private set; } = EcuStatus.Unknown;

    public DateTimeOffset? LastHeartbeatAt { get; private set; }

    public int ActiveDtcCount { get; private set; }

    public long TimeoutCount { get; private set; }

    public long E2EErrorCount { get; private set; }

    internal static Ecu Create(Guid vehicleKey, string ecuId, EcuType type, string name, string? softwareVersion) => new()
    {
        Id = Guid.NewGuid(),
        VehicleKey = vehicleKey,
        EcuId = ecuId,
        Type = type,
        Name = name,
        SoftwareVersion = string.IsNullOrWhiteSpace(softwareVersion) ? "0.0.0" : softwareVersion,
    };

    /// <summary>Applies the gateway's observation of this ECU. Returns true if the status changed.</summary>
    public bool ApplyObservation(EcuStatus status, string? softwareVersion, string? hardwareVersion, DateTimeOffset? lastSeen,
        int activeDtcCount, long timeoutCount, long e2eErrorCount)
    {
        var changed = Status != status;
        Status = status;
        if (!string.IsNullOrWhiteSpace(softwareVersion))
        {
            SoftwareVersion = softwareVersion;
        }

        HardwareVersion = hardwareVersion ?? HardwareVersion;
        if (lastSeen is not null && (LastHeartbeatAt is null || lastSeen > LastHeartbeatAt))
        {
            LastHeartbeatAt = lastSeen;
        }

        ActiveDtcCount = activeDtcCount;
        TimeoutCount = timeoutCount;
        E2EErrorCount = e2eErrorCount;
        return changed;
    }

    public void SetSoftwareVersion(string version) => SoftwareVersion = version;

    /// <summary>The gateway went offline: the cloud no longer knows the ECU state.</summary>
    public void MarkUnreachable() => Status = EcuStatus.Offline;
}
