using System.Net;
using System.Net.Sockets;
using AutoSphere.CanBus.InMemory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AutoSphere.CanBus.Udp;

/// <summary>
/// Bridges a local (in-process) CAN segment to remote peers over UDP, in the spirit of the
/// <i>cannelloni</i> CAN-over-Ethernet tunnel. It allows the ECU simulator and the gateway to run as
/// separate processes on any OS. Frames written by a local channel are delivered to the other local
/// channels and forwarded to every peer; datagrams received from peers are delivered to all local channels.
/// </summary>
/// <remarks>
/// UDP gives no delivery guarantee. That is acceptable here because CAN itself is a lossy medium from the
/// application's point of view and every consumer already handles message timeouts.
/// </remarks>
public sealed class UdpCanBus : ICanBus
{
    private readonly InMemoryCanBus _segment;
    private readonly UdpClient _udp;
    private readonly IPEndPoint[] _peers;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _sendLoop;
    private readonly Task _receiveLoop;
    private readonly ICanChannel _bridge;

    private UdpCanBus(InMemoryCanBus segment, ICanChannel bridge, UdpClient udp, IPEndPoint[] peers, ILogger logger)
    {
        _segment = segment;
        _bridge = bridge;
        _udp = udp;
        _peers = peers;
        _logger = logger;
        Description = $"udp:{udp.Client.LocalEndPoint}->{string.Join(',', peers.Select(p => p.ToString()))}";
        _sendLoop = Task.Run(() => ForwardToPeersAsync(_stopping.Token));
        _receiveLoop = Task.Run(() => ReceiveFromPeersAsync(_stopping.Token));
    }

    public string Description { get; }

    public static async Task<UdpCanBus> CreateAsync(int localPort, IEnumerable<IPEndPoint> peers, ILogger? logger = null, TimeProvider? timeProvider = null)
    {
        var segment = new InMemoryCanBus(timeProvider, description: "udp-segment");
        var bridge = await segment.OpenChannelAsync("udp-bridge");
        var udp = new UdpClient(new IPEndPoint(IPAddress.Any, localPort));
        return new UdpCanBus(segment, bridge, udp, peers.ToArray(), logger ?? NullLogger.Instance);
    }

    public ValueTask<ICanChannel> OpenChannelAsync(string name, IReadOnlyList<CanFilter>? filters = null, CancellationToken cancellationToken = default) =>
        _segment.OpenChannelAsync(name, filters, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _udp.Dispose();
        try
        {
            await Task.WhenAll(_sendLoop, _receiveLoop);
        }
        catch (OperationCanceledException)
        {
            // expected on shutdown
        }

        await _bridge.DisposeAsync();
        await _segment.DisposeAsync();
        _stopping.Dispose();
    }

    private async Task ForwardToPeersAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[CanFrameWireFormat.HeaderLength + CanFrame.MaxFdLength];
        try
        {
            await foreach (var frame in _bridge.ReadAllAsync(cancellationToken))
            {
                var length = CanFrameWireFormat.Encode(frame, buffer);
                foreach (var peer in _peers)
                {
                    try
                    {
                        await _udp.SendAsync(buffer.AsMemory(0, length), peer, cancellationToken);
                    }
                    catch (SocketException ex)
                    {
                        // Peer not (yet) listening: ICMP port unreachable surfaces on some platforms.
                        _logger.LogDebug(ex, "UDP CAN bridge could not send to {Peer}", peer);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task ReceiveFromPeersAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(cancellationToken);
                if (CanFrameWireFormat.TryDecode(result.Buffer, DateTimeOffset.UtcNow, out var frame))
                {
                    await _bridge.WriteAsync(frame!, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException ex)
            {
                // Windows reports ICMP "connection reset" on UDP sockets when a peer is down; keep listening.
                _logger.LogDebug(ex, "UDP CAN bridge receive error");
            }
        }
    }
}
