using AutoSphere.CanBus;
using AutoSphere.EcuSimulation.Ecus;
using AutoSphere.EcuSimulation.Plant;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleSignals.Codec;
using AutoSphere.VehicleSignals.Database;
using Microsoft.Extensions.Logging;

namespace AutoSphere.EcuSimulation;

/// <summary>
/// A complete simulated vehicle network: the plant model plus every configured ECU on one CAN bus.
/// </summary>
public sealed class VehicleSimulation : IAsyncDisposable
{
    private static readonly TimeSpan PlantStep = TimeSpan.FromMilliseconds(20);

    private readonly ICanBus _bus;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<VehicleSimulation> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _plantLoop;
    private int _disposed;

    public VehicleSimulation(
        SimulationOptions options,
        ICanBus bus,
        ILoggerFactory loggerFactory,
        TimeProvider? timeProvider = null,
        CanDatabase? database = null,
        DriveCycle? driveCycle = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (string.IsNullOrWhiteSpace(options.SecurityAccessSecret))
        {
            throw new InvalidOperationException("Simulation:SecurityAccessSecret must be configured (see .env.example).");
        }

        Options = options;
        _bus = bus;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = loggerFactory.CreateLogger<VehicleSimulation>();
        Plant = new VehiclePlantModel(options.Plant, driveCycle);
        var context = new SimulationContext(
            Plant,
            new CanSignalCodec(database ?? CanDatabaseLoader.Default),
            options.Vin,
            options.SecurityAccessSecret,
            TimeSpan.FromMilliseconds(options.BootTimeMs),
            _timeProvider,
            loggerFactory);

        Ecus = options.EffectiveEcus.Where(e => e.Enabled).Select(e => CreateEcu(e, context)).ToList();
        FaultInjector = new SimulationFaultInjector(this, loggerFactory.CreateLogger<SimulationFaultInjector>(), _timeProvider);
    }

    public SimulationOptions Options { get; }

    public VehiclePlantModel Plant { get; }

    public IReadOnlyList<EcuSimulator> Ecus { get; }

    public SimulationFaultInjector FaultInjector { get; }

    public EcuSimulator? FindEcu(string ecuId) => Ecus.FirstOrDefault(e => string.Equals(e.EcuId, ecuId, StringComparison.OrdinalIgnoreCase));

    public EcuSimulator? FindEcu(EcuType type) => Ecus.FirstOrDefault(e => e.Type == type);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var ecu in Ecus)
        {
            await ecu.StartAsync(_bus, cancellationToken);
        }

        _plantLoop = Task.Run(() => RunPlantAsync(_stopping.Token), CancellationToken.None);
        _logger.LogInformation("Vehicle simulation {VehicleId} started on {CanBus} with {EcuCount} ECUs",
            Options.VehicleId, _bus.Description, Ecus.Count);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return; // stopped by the host and disposed by the container
        }

        await _stopping.CancelAsync();
        if (_plantLoop is not null)
        {
            try
            {
                await _plantLoop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        foreach (var ecu in Ecus)
        {
            await ecu.DisposeAsync();
        }

        FaultInjector.Dispose();
        _stopping.Dispose();
    }

    private async Task RunPlantAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PlantStep, _timeProvider);
        var last = _timeProvider.GetTimestamp();
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var now = _timeProvider.GetTimestamp();
                Plant.Step(_timeProvider.GetElapsedTime(last, now));
                last = now;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static EcuSimulator CreateEcu(EcuSimulatorOptions options, SimulationContext context) => options.Type switch
    {
        EcuType.VehicleControlUnit => new VehicleControlEcuSimulator(options, context),
        EcuType.MotorControlUnit => new MotorEcuSimulator(options, context),
        EcuType.BatteryManagementSystem => new BatteryEcuSimulator(options, context),
        EcuType.BodyControlModule => new BodyControlEcuSimulator(options, context),
        _ => throw new NotSupportedException($"ECU type {options.Type} cannot be simulated."),
    };
}
