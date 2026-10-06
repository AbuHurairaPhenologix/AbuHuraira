namespace AutoSphere.SharedKernel.Ota;

/// <summary>
/// Lifecycle of a single OTA deployment to one ECU.
/// </summary>
/// <remarks>
/// <code>
/// Created → Pending → Downloading → Verifying → Installing → Restarting → HealthChecking → Succeeded
///                         │             │            │            │              │
///                         └─────────────┴────────────┴→ Failed    └──────────────┴→ RollingBack → RolledBack
///                                                                                              └→ RollbackFailed
/// </code>
/// A failure before the ECU restarts never touches the active software bank, therefore it ends in
/// <see cref="Failed"/> without rollback. Once the ECU has booted the new image, any failure triggers
/// a rollback to the previous bank.
/// </remarks>
public enum OtaUpdateStatus
{
    Created = 0,
    Pending = 1,
    Downloading = 2,
    Verifying = 3,
    Installing = 4,
    Restarting = 5,
    HealthChecking = 6,
    Succeeded = 7,
    Failed = 8,
    RollingBack = 9,
    RolledBack = 10,
    RollbackFailed = 11,
    Cancelled = 12,
}

public static class OtaUpdateStatusExtensions
{
    public static bool IsTerminal(this OtaUpdateStatus status) => status is
        OtaUpdateStatus.Succeeded or OtaUpdateStatus.Failed or OtaUpdateStatus.RolledBack
        or OtaUpdateStatus.RollbackFailed or OtaUpdateStatus.Cancelled;
}

/// <summary>Behaviour encoded in a simulated firmware image, interpreted by the simulated ECU after boot.</summary>
public enum FirmwareBootBehavior
{
    /// <summary>The ECU boots and operates normally.</summary>
    Normal = 0,

    /// <summary>The ECU keeps resetting shortly after boot (watchdog reset loop).</summary>
    CrashLoop = 1,

    /// <summary>The ECU boots but its power-on self-test fails and it stores DTC P0606.</summary>
    SelfTestFailure = 2,
}
