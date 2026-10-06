using AutoSphere.Contracts.Messages;
using AutoSphere.Diagnostics.FaultMemory;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Network;

/// <summary>Reception supervision of one cyclic message.</summary>
public sealed class MessageSupervision(CanMessageDefinition definition, TimeSpan timeout)
{
    public CanMessageDefinition Definition { get; } = definition;

    public TimeSpan Timeout { get; } = timeout;

    public DateTimeOffset? LastReceived { get; set; }

    public int? LastCounter { get; set; }

    public bool TimedOut { get; set; }
}

/// <summary>Runtime view of one ECU as seen by the gateway.</summary>
public sealed class EcuNode
{
    private int _isUpdating;

    public EcuNode(GatewayEcuOptions options, IEnumerable<MessageSupervision> messages)
    {
        EcuId = options.EcuId;
        Type = options.Type;
        Name = options.Name;
        Messages = messages.ToList();
    }

    public string EcuId { get; }

    public EcuType Type { get; }

    public string Name { get; }

    public IReadOnlyList<MessageSupervision> Messages { get; }

    public EcuStatus Status { get; internal set; } = EcuStatus.Unknown;

    public string? SoftwareVersion { get; set; }

    public string? HardwareVersion { get; set; }

    public DateTimeOffset? LastSeen { get; internal set; }

    public long TimeoutCount { get; internal set; }

    public long E2EErrorCount { get; internal set; }

    public long LateCount { get; internal set; }

    internal DateTimeOffset? LastLateAt { get; set; }

    internal DateTimeOffset? LastE2EErrorAt { get; set; }

    /// <summary>Number of DTCs with testFailed set, from the last diagnostic poll.</summary>
    public int ActiveDtcCount { get; set; }

    public DtcSeverity? WorstActiveDtcSeverity { get; set; }

    /// <summary>Set while an OTA update runs: communication loss is expected and not reported as a fault.</summary>
    public bool IsUpdating
    {
        get => Volatile.Read(ref _isUpdating) == 1;
        set => Volatile.Write(ref _isUpdating, value ? 1 : 0);
    }

    public bool IsReachable => Status is not (EcuStatus.Offline or EcuStatus.Unknown);

    public EcuStatusDto ToDto() => new(EcuId, Type, Name, Status, SoftwareVersion, HardwareVersion, LastSeen, ActiveDtcCount, TimeoutCount, E2EErrorCount);
}

/// <summary>
/// Supervises cyclic CAN messages per ECU (timeouts, late frames, E2E CRC and alive-counter errors),
/// derives each ECU's communication status and records network DTCs in the gateway's own fault memory.
/// </summary>
public sealed class EcuNetworkMonitor
{
    private static readonly TimeSpan StartupGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(5);
    private const double LateFactor = 2.5;

    private readonly Dictionary<EcuType, EcuNode> _byType;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();

    public EcuNetworkMonitor(IOptions<GatewayOptions> options, CanDatabase database, TimeProvider timeProvider)
    {
        var gateway = options.Value;
        _timeProvider = timeProvider;
        _startedAt = timeProvider.GetUtcNow();
        Nodes = gateway.EffectiveEcus.Select(ecu => new EcuNode(ecu, database.MessagesSentBy(ecu.Type).Select(m =>
            new MessageSupervision(m, TimeSpan.FromMilliseconds(Math.Max(m.CycleTimeMs * gateway.MessageTimeoutMultiplier, gateway.MinimumMessageTimeoutMs))))))
            .ToList();
        _byType = Nodes.ToDictionary(n => n.Type);
    }

    public IReadOnlyList<EcuNode> Nodes { get; }

    /// <summary>The gateway's own fault memory (network DTCs such as U0100).</summary>
    public DtcMemory GatewayDtcs { get; } = new();

    public event EventHandler<EcuNode>? StatusChanged;

    public EcuNode? Find(string ecuId) => Nodes.FirstOrDefault(n => string.Equals(n.EcuId, ecuId, StringComparison.OrdinalIgnoreCase));

    public EcuNode? Find(EcuType type) => _byType.GetValueOrDefault(type);

    /// <summary>Records reception of a decoded frame. Returns <c>false</c> if the frame failed the E2E check.</summary>
    public bool OnFrameReceived(DecodedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!_byType.TryGetValue(message.Definition.Sender, out var node))
        {
            return true;
        }

        var now = message.Frame.Timestamp;
        lock (_gate)
        {
            var supervision = node.Messages.First(m => m.Definition.Id == message.Definition.Id);
            if (message.E2EStatus == E2EStatus.CrcError)
            {
                node.E2EErrorCount++;
                node.LastE2EErrorAt = now;
                return false;
            }

            if (message.AliveCounter is { } counter && supervision.LastCounter is { } previous && !supervision.TimedOut)
            {
                var delta = E2EProtection.CounterDelta(previous, counter);
                if (delta != 1)
                {
                    // Repeated (0) or skipped (>1) counter values indicate stale or lost frames.
                    node.E2EErrorCount++;
                    node.LastE2EErrorAt = now;
                }
            }

            if (supervision.LastReceived is { } last && !supervision.TimedOut
                && now - last > TimeSpan.FromMilliseconds(supervision.Definition.CycleTimeMs * LateFactor))
            {
                node.LateCount++;
                node.LastLateAt = now;
            }

            supervision.LastReceived = now;
            supervision.LastCounter = message.AliveCounter;
            supervision.TimedOut = false;
            node.LastSeen = now;
            return true;
        }
    }

    /// <summary>Re-evaluates timeouts, ECU statuses and network DTCs. Called periodically.</summary>
    public void Evaluate()
    {
        var now = _timeProvider.GetUtcNow();
        var changed = new List<EcuNode>();
        var anyLate = false;
        var anyE2E = false;

        lock (_gate)
        {
            foreach (var node in Nodes)
            {
                var timedOut = 0;
                foreach (var message in node.Messages)
                {
                    var reference = message.LastReceived ?? _startedAt;
                    var expired = now - reference > message.Timeout + (message.LastReceived is null ? StartupGrace : TimeSpan.Zero);
                    if (expired && !message.TimedOut)
                    {
                        message.TimedOut = true;
                        if (!node.IsUpdating)
                        {
                            node.TimeoutCount++;
                        }
                    }

                    if (message.TimedOut)
                    {
                        timedOut++;
                    }
                }

                var recentlyLate = node.LastLateAt is { } late && now - late < RecentWindow;
                var recentE2E = node.LastE2EErrorAt is { } e2e && now - e2e < RecentWindow;
                anyLate |= recentlyLate && !node.IsUpdating;
                anyE2E |= recentE2E && !node.IsUpdating;

                var status = node.IsUpdating ? EcuStatus.Updating
                    : timedOut == node.Messages.Count ? EcuStatus.Offline
                    : node.WorstActiveDtcSeverity == DtcSeverity.Critical ? EcuStatus.Critical
                    : timedOut > 0 || recentlyLate || recentE2E || node.WorstActiveDtcSeverity == DtcSeverity.Warning ? EcuStatus.Warning
                    : EcuStatus.Online;

                // While the ECU is being reprogrammed, a silent bus is expected and must not set a DTC.
                GatewayDtcs.Report(LostCommunicationDtc(node.Type), timedOut > 0 && !node.IsUpdating, static () => []);

                if (status != node.Status)
                {
                    node.Status = status;
                    changed.Add(node);
                }
            }

            GatewayDtcs.Report(KnownDtcs.CanMessageTimingFault.Code, anyLate, static () => []);
            GatewayDtcs.Report(KnownDtcs.E2EProtectionFault.Code, anyE2E, static () => []);
        }

        foreach (var node in changed)
        {
            StatusChanged?.Invoke(this, node);
        }
    }

    public static DtcCode LostCommunicationDtc(EcuType type) => type switch
    {
        EcuType.VehicleControlUnit => KnownDtcs.LostCommunicationVehicleControl.Code,
        EcuType.MotorControlUnit => KnownDtcs.LostCommunicationMotorController.Code,
        EcuType.BatteryManagementSystem => KnownDtcs.LostCommunicationBatteryManagement.Code,
        _ => KnownDtcs.LostCommunicationBodyControl.Code,
    };
}
