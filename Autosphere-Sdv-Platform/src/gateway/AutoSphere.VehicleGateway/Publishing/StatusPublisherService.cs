using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway.Can;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleGateway.RemoteDiagnostics;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Publishing;

/// <summary>
/// Publishes the retained vehicle status (gateway + ECU inventory) periodically and immediately whenever
/// an ECU changes its communication status.
/// </summary>
public sealed class StatusPublisherService : BackgroundService
{
    private readonly EcuNetworkMonitor _network;
    private readonly GatewayMqttClient _mqtt;
    private readonly CanConnectionState _can;
    private readonly GatewayOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly bool _simulationMode;
    private readonly DateTimeOffset _startedAt;
    private readonly StatusSignal _signal;

    public StatusPublisherService(
        EcuNetworkMonitor network,
        GatewayMqttClient mqtt,
        CanConnectionState can,
        IOptions<GatewayOptions> options,
        TimeProvider timeProvider,
        StatusSignal signal,
        IServiceProvider services)
    {
        _network = network;
        _mqtt = mqtt;
        _can = can;
        _options = options.Value;
        _timeProvider = timeProvider;
        _simulationMode = services.GetService<AutoSphere.EcuSimulation.VehicleSimulation>() is not null;
        _startedAt = timeProvider.GetUtcNow();
        _signal = signal;
        network.StatusChanged += (_, _) => signal.Signal();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(_options.StatusPublishIntervalMs);
        while (!stoppingToken.IsCancellationRequested)
        {
            Publish();
            await _signal.WaitAsync(interval, stoppingToken);
            await Task.Delay(50, stoppingToken); // coalesce bursts of status changes
        }
    }

    private void Publish()
    {
        var now = _timeProvider.GetUtcNow();
        var gatewayEcu = new EcuStatusDto(_options.GatewayId, EcuType.CentralGateway, "Central Gateway", EcuStatus.Online,
            UdsDiagnosticService.GatewayVersion, "CGW-HW1", now, _network.GatewayDtcs.Query(UdsDiagnosticService.AllStatusBits).Count(d => d.TestFailed), 0, 0);
        _mqtt.Publish(MqttTopics.Status(_options.VehicleId), new VehicleStatusMessage
        {
            VehicleId = _options.VehicleId,
            Timestamp = now,
            Connectivity = ConnectivityStatus.Online,
            GatewayId = _options.GatewayId,
            GatewaySoftwareVersion = UdsDiagnosticService.GatewayVersion,
            CanState = _can.IsConnected ? GatewayCanState.Connected : GatewayCanState.Disconnected,
            CanTransport = _can.Description,
            SimulationMode = _simulationMode,
            UptimeSeconds = Math.Round((now - _startedAt).TotalSeconds),
            Ecus = [gatewayEcu, .. _network.Nodes.Select(n => n.ToDto())],
        }, retain: true);
    }
}

/// <summary>Wakes the status publisher when something worth reporting changed.</summary>
public sealed class StatusSignal : IDisposable
{
    private readonly SemaphoreSlim _semaphore = new(0, 1);

    public void Signal()
    {
        try
        {
            if (_semaphore.CurrentCount == 0)
            {
                _semaphore.Release();
            }
        }
        catch (SemaphoreFullException)
        {
            // already signalled
        }
    }

    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken cancellationToken) => _semaphore.WaitAsync(timeout, cancellationToken);

    public void Dispose() => _semaphore.Dispose();
}
