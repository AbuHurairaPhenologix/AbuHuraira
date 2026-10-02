using AutoSphere.CanBus;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Diagnostics.Addressing;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.FaultMemory;
using AutoSphere.EcuSimulation.Plant;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.SharedKernel.Versioning;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using Microsoft.Extensions.Logging;

namespace AutoSphere.EcuSimulation.Ecus;

public enum EcuPowerState
{
    Off,
    Booting,
    Running,

    /// <summary>The application is not running (crash/watchdog); the bootloader may still answer diagnostics.</summary>
    ApplicationCrashed,
}

/// <summary>Faults currently injected into one ECU.</summary>
public sealed class EcuFaultState
{
    private volatile bool _crashed;
    private volatile bool _messageLoss;
    private volatile int _messageDelayMs;
    private volatile bool _invalidSensorValue;

    /// <summary>Total failure: no CAN traffic at all, no diagnostic responses.</summary>
    public bool Crashed { get => _crashed; set => _crashed = value; }

    /// <summary>Cyclic messages are not transmitted.</summary>
    public bool MessageLoss { get => _messageLoss; set => _messageLoss = value; }

    /// <summary>Additional transmission delay for cyclic messages.</summary>
    public int MessageDelayMs { get => _messageDelayMs; set => _messageDelayMs = value; }

    /// <summary>The primary sensor of the ECU reports an implausible value.</summary>
    public bool InvalidSensorValue { get => _invalidSensorValue; set => _invalidSensorValue = value; }

    public bool Any => Crashed || MessageLoss || MessageDelayMs > 0 || InvalidSensorValue;
}

/// <summary>Shared runtime services of the simulation.</summary>
public sealed record SimulationContext(
    VehiclePlantModel Plant,
    CanSignalCodec Codec,
    string Vin,
    string SecurityAccessSecret,
    TimeSpan BootTime,
    TimeProvider TimeProvider,
    ILoggerFactory LoggerFactory);

/// <summary>
/// Base class of every simulated ECU. An ECU transmits the cyclic messages assigned to it in the CAN
/// database, monitors its own fault conditions into a <see cref="DtcMemory"/>, and runs a UDS-inspired
/// diagnostic server with a dual-bank (A/B) bootloader for software updates.
/// </summary>
public abstract partial class EcuSimulator : IAsyncDisposable
{
    private static readonly TimeSpan MonitorPeriod = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CrashLoopUptime = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan CrashLoopDowntime = TimeSpan.FromSeconds(1.5);

    private readonly List<Task> _tasks = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly object _lifecycleGate = new();
    private ICanChannel? _canChannel;
    private IsoTpChannel? _diagnosticChannel;
    private DateTimeOffset _runningSince;
    private int _bootGeneration;
    private int _disposed;

    protected EcuSimulator(EcuSimulatorOptions options, SimulationContext context)
    {
        Options = options;
        Context = context;
        Logger = context.LoggerFactory.CreateLogger(GetType());
        Banks[0] = new SoftwareBank(SoftwareVersion.Parse(options.SoftwareVersion), FirmwareBootBehavior.Normal, true);
        Banks[1] = SoftwareBank.Empty;
        Messages = context.Codec.Database.MessagesSentBy(options.Type).ToList();
    }

    public string EcuId => Options.EcuId;

    public string Name => Options.Name;

    public EcuType Type => Options.Type;

    public EcuSimulatorOptions Options { get; }

    public EcuFaultState Faults { get; } = new();

    public DtcMemory Dtcs { get; } = new();

    public EcuPowerState PowerState { get; private set; } = EcuPowerState.Off;

    /// <summary>Number of resets since start (useful in tests and the UI).</summary>
    public int ResetCount { get; private set; }

    public SoftwareVersion ActiveVersion => Banks[ActiveBank].Version;

    public FirmwareBootBehavior ActiveBootBehavior => Banks[ActiveBank].BootBehavior;

    public bool IsApplicationRunning => PowerState == EcuPowerState.Running && !Faults.Crashed;

    public bool IsDiagnosticReachable => PowerState is EcuPowerState.Running or EcuPowerState.ApplicationCrashed && !Faults.Crashed;

    protected SimulationContext Context { get; }

    protected ILogger Logger { get; }

    protected IReadOnlyList<CanMessageDefinition> Messages { get; }

    protected PlantState Plant => Context.Plant.Current;

    /// <summary>Physical values for all signals this ECU transmits.</summary>
    protected abstract IReadOnlyDictionary<string, double> ProduceSignals(PlantState state);

    /// <summary>Evaluates the ECU's own fault monitors and reports them to <see cref="Dtcs"/>.</summary>
    protected abstract void MonitorFaults(PlantState state);

    /// <summary>Current value of a live-data DID, or <c>null</c> if the ECU does not provide it.</summary>
    protected abstract double? ReadLiveData(DataIdentifierDefinition definition, PlantState state);

    /// <summary>DIDs captured in a DTC snapshot.</summary>
    protected abstract IReadOnlyList<DataIdentifierDefinition> SnapshotIdentifiers { get; }

    public async Task StartAsync(ICanBus bus, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bus);
        _canChannel = await bus.OpenChannelAsync(EcuId, CanFilter.ReceiveNothing, cancellationToken); // transmit-only channel
        var address = DiagnosticAddresses.For(Type);
        _diagnosticChannel = await IsoTpChannel.OpenAsync(bus, $"{EcuId}-diag", address.ResponseId, address.RequestId,
            timeProvider: Context.TimeProvider, cancellationToken: cancellationToken);

        var token = _stopping.Token;
        foreach (var message in Messages)
        {
            _tasks.Add(Task.Run(() => TransmitLoopAsync(message, token), CancellationToken.None));
        }

        _tasks.Add(Task.Run(() => MonitorLoopAsync(token), CancellationToken.None));
        _tasks.Add(Task.Run(() => DiagnosticServerLoopAsync(token), CancellationToken.None));
        _ = BootAsync(token);
        Logger.LogInformation("ECU {EcuId} ({EcuType}) started with software {SoftwareVersion}", EcuId, Type, ActiveVersion);
    }

    /// <summary>Simulates an ECU reset: the ECU goes silent, boots the active bank and resumes.</summary>
    public Task ResetAsync() => BootAsync(_stopping.Token);

    /// <summary>Called when an injected crash is cleared: the ECU recovers by rebooting.</summary>
    public Task RecoverAsync()
    {
        Faults.Crashed = false;
        return BootAsync(_stopping.Token);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync();
        try
        {
            await Task.WhenAll(_tasks);
        }
        catch (OperationCanceledException)
        {
        }

        if (_diagnosticChannel is not null)
        {
            await _diagnosticChannel.DisposeAsync();
        }

        if (_canChannel is not null)
        {
            await _canChannel.DisposeAsync();
        }

        _stopping.Dispose();
        GC.SuppressFinalize(this);
    }

    protected byte[] EncodeLiveData(DataIdentifierDefinition definition) =>
        DataIdentifierCatalog.Encode(definition, ReadLiveData(definition, Plant) ?? 0);

    protected IReadOnlyList<(DataIdentifierDefinition Definition, byte[] Data)> CaptureSnapshot()
    {
        var state = Plant;
        return SnapshotIdentifiers.Select(d => (d, DataIdentifierCatalog.Encode(d, ReadLiveData(d, state) ?? 0))).ToList();
    }

    private async Task BootAsync(CancellationToken cancellationToken)
    {
        int generation;
        lock (_lifecycleGate)
        {
            generation = ++_bootGeneration;
            PowerState = EcuPowerState.Booting;
            ResetCount++;
            OnReset();
        }

        try
        {
            await Task.Delay(Context.BootTime, Context.TimeProvider, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_lifecycleGate)
        {
            if (generation != _bootGeneration)
            {
                return; // a newer reset superseded this boot
            }

            PowerState = EcuPowerState.Running;
            _runningSince = Context.TimeProvider.GetUtcNow();
        }

        Logger.LogInformation("ECU {EcuId} booted software {SoftwareVersion} from bank {Bank} (behaviour {BootBehavior})",
            EcuId, ActiveVersion, ActiveBank == 0 ? 'A' : 'B', ActiveBootBehavior);
    }

    private async Task TransmitLoopAsync(CanMessageDefinition message, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(message.CycleTimeMs), Context.TimeProvider);
        var counter = 0;
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                if (!IsApplicationRunning || Faults.MessageLoss || _canChannel is null)
                {
                    continue;
                }

                var delay = Faults.MessageDelayMs;
                if (delay > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(delay), Context.TimeProvider, cancellationToken);
                }

                var payload = Context.Codec.Encode(message, ProduceSignals(Plant), counter);
                counter = (counter + 1) % E2EProtection.CounterModulo;
                await _canChannel.WriteAsync(new CanFrame(message.Id, payload, Context.TimeProvider.GetUtcNow(), source: EcuId), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is CanBusException or ObjectDisposedException)
        {
            Logger.LogWarning(ex, "ECU {EcuId} stopped transmitting {Message}", EcuId, message.Name);
        }
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(MonitorPeriod, Context.TimeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                ExpireDiagnosticSession();
                if (Faults.Crashed)
                {
                    continue;
                }

                SimulateCrashLoop();
                if (!IsApplicationRunning)
                {
                    continue;
                }

                var state = Plant;
                MonitorFaults(state);

                // Power-on self-test result of the running image (simulated firmware behaviour).
                Dtcs.Report(SharedKernel.Diagnostics.KnownDtcs.ControlModuleInternalFault.Code,
                    ActiveBootBehavior == FirmwareBootBehavior.SelfTestFailure, CaptureSnapshot);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>A crash-looping image runs briefly, then the (simulated) watchdog resets the application.</summary>
    private void SimulateCrashLoop()
    {
        if (ActiveBootBehavior != FirmwareBootBehavior.CrashLoop)
        {
            return;
        }

        var now = Context.TimeProvider.GetUtcNow();
        lock (_lifecycleGate)
        {
            if (PowerState == EcuPowerState.Running && now - _runningSince > CrashLoopUptime)
            {
                PowerState = EcuPowerState.ApplicationCrashed;
                _runningSince = now;
                Logger.LogWarning("ECU {EcuId} application crashed (watchdog reset loop of software {SoftwareVersion})", EcuId, ActiveVersion);
            }
            else if (PowerState == EcuPowerState.ApplicationCrashed && now - _runningSince > CrashLoopDowntime)
            {
                PowerState = EcuPowerState.Running;
                _runningSince = now;
            }
        }
    }
}
