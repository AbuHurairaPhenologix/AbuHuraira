using System.Diagnostics;
using AutoSphere.CanBus;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Instrumentation;
using AutoSphere.VehicleGateway.Network;
using AutoSphere.VehicleGateway.Signals;
using AutoSphere.VehicleSignals.Codec;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.Can;

/// <summary>Connection state of the gateway's CAN interface.</summary>
public sealed class CanConnectionState
{
    private volatile bool _connected;

    public bool IsConnected
    {
        get => _connected;
        set => _connected = value;
    }

    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Receives every non-diagnostic frame from the vehicle bus, decodes it with the CAN database,
/// updates the signal store and feeds the network monitor. Reconnects with exponential back-off when
/// the CAN channel fails (e.g. SocketCAN interface going down).
/// </summary>
public sealed class CanReceiverService(
    ICanBus bus,
    ICanSignalDecoder decoder,
    VehicleSignalStore store,
    EcuNetworkMonitor monitor,
    GatewayMetrics metrics,
    CanConnectionState connection,
    IOptions<GatewayOptions> options,
    ILogger<CanReceiverService> logger) : BackgroundService
{
    // Diagnostic traffic (0x7DF, 0x7E0-0x7EF) is handled by the ISO-TP channels, not here.
    private static bool IsDiagnosticId(uint id) => id is 0x7DF or (>= 0x7E0 and <= 0x7EF);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        connection.Description = bus.Description;
        var delay = TimeSpan.FromMilliseconds(500);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var channel = await bus.OpenChannelAsync("gateway-rx", cancellationToken: stoppingToken);
                connection.IsConnected = true;
                delay = TimeSpan.FromMilliseconds(500);
                logger.LogInformation("Gateway {GatewayId} listening on {CanBus}", options.Value.GatewayId, bus.Description);
                await ReceiveAsync(channel, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex) when (ex is CanBusException or System.Threading.Channels.ChannelClosedException or ObjectDisposedException)
            {
                connection.IsConnected = false;
                logger.LogWarning(ex, "CAN channel failed; reconnecting in {Delay} ms", delay.TotalMilliseconds);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 10_000));
            }
        }

        connection.IsConnected = false;
    }

    private async Task ReceiveAsync(ICanChannel channel, CancellationToken stoppingToken)
    {
        var ecuIds = monitor.Nodes.ToDictionary(n => n.Type, n => n.EcuId);
        await foreach (var frame in channel.ReadAllAsync(stoppingToken))
        {
            if (IsDiagnosticId(frame.Id))
            {
                continue;
            }

            var started = Stopwatch.GetTimestamp();
            var decoded = decoder.TryDecode(frame, out var message);
            var accepted = decoded && monitor.OnFrameReceived(message!);
            if (accepted)
            {
                var supervision = monitor.Find(message!.Definition.Sender)?.Messages.FirstOrDefault(m => m.Definition.Id == frame.Id);
                store.Update(message, ecuIds.GetValueOrDefault(message.Definition.Sender, message.Definition.Sender.ToString()),
                    supervision?.Timeout ?? TimeSpan.FromSeconds(1));
            }

            metrics.FrameReceived(accepted, Stopwatch.GetElapsedTime(started).TotalMicroseconds);
        }

        throw new CanBusException("CAN channel closed.");
    }
}

/// <summary>Periodically evaluates message timeouts and ECU communication status.</summary>
public sealed class NetworkSupervisionService(EcuNetworkMonitor monitor, TimeProvider timeProvider) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100), timeProvider);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            monitor.Evaluate();
        }
    }
}
