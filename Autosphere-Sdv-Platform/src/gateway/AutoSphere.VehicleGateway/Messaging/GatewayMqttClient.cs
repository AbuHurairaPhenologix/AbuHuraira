using System.Buffers;
using System.Threading.Channels;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.SharedKernel.Vehicles;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Instrumentation;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace AutoSphere.VehicleGateway.Messaging;

/// <summary>A command received from the cloud.</summary>
public sealed record InboundMessage(VehicleTopicKind Kind, string Topic, byte[] Payload);

/// <summary>
/// The gateway's single MQTT session (MQTT 5). Responsibilities:
/// <list type="bullet">
/// <item>register a retained "offline" status as Last Will so the cloud learns about abrupt connection loss,</item>
/// <item>reconnect automatically with exponential back-off,</item>
/// <item>store-and-forward: outgoing messages are queued in a bounded outbox and flushed after reconnecting,</item>
/// <item>subscribe to the vehicle's command topics.</item>
/// </list>
/// </summary>
public sealed class GatewayMqttClient : BackgroundService
{
    private readonly MqttBrokerOptions _broker;
    private readonly GatewayOptions _gateway;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<GatewayMqttClient> _logger;
    private readonly IMqttClient _client;
    private readonly Channel<MqttApplicationMessage> _outbox;
    private readonly Channel<InboundMessage> _inbox = Channel.CreateUnbounded<InboundMessage>();
    private long _dropped;
    private DateTimeOffset _suspendedUntil = DateTimeOffset.MinValue;

    public GatewayMqttClient(IOptions<MqttBrokerOptions> broker, IOptions<GatewayOptions> gateway, GatewayMetrics metrics, ILogger<GatewayMqttClient> logger)
    {
        _broker = broker.Value;
        _gateway = gateway.Value;
        _metrics = metrics;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _outbox = Channel.CreateBounded<MqttApplicationMessage>(
            new BoundedChannelOptions(Math.Max(16, _gateway.OfflineBufferCapacity)) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
            _ => Interlocked.Increment(ref _dropped));
        _client.ApplicationMessageReceivedAsync += args =>
        {
            var topic = args.ApplicationMessage.Topic;
            if (MqttTopics.TryParse(topic, out var vehicleId, out var kind) && vehicleId == _gateway.VehicleId)
            {
                _inbox.Writer.TryWrite(new InboundMessage(kind, topic, args.ApplicationMessage.Payload.ToArray()));
            }

            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    /// <summary>Messages discarded because the offline buffer was full.</summary>
    public long DroppedMessages => Interlocked.Read(ref _dropped);

    /// <summary>Messages waiting to be sent.</summary>
    public int QueuedMessages => _outbox.Reader.Count;

    /// <summary>Commands received from the cloud, consumed by <see cref="CommandDispatcher"/>.</summary>
    public ChannelReader<InboundMessage> Inbound => _inbox.Reader;

    /// <summary>Queues a message for publication; it is sent as soon as the session is connected.</summary>
    public void Publish<T>(string topic, T message, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce, bool retain = false)
    {
        _outbox.Writer.TryWrite(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(AutoSphereJson.Serialize(message))
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build());
    }

    /// <summary>
    /// Fault injection: drops the cloud connection for <paramref name="duration"/>. With
    /// <paramref name="withWill"/> the broker publishes the Last Will (abrupt gateway loss); without it the
    /// session ends silently and the backend must detect the loss through stale telemetry.
    /// </summary>
    public async Task SuspendAsync(TimeSpan duration, bool withWill)
    {
        _suspendedUntil = DateTimeOffset.UtcNow + duration;
        if (_client.IsConnected)
        {
            var reason = withWill ? MqttClientDisconnectOptionsReason.DisconnectWithWillMessage : MqttClientDisconnectOptionsReason.NormalDisconnection;
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().WithReason(reason).Build());
        }

        _logger.LogWarning("MQTT session suspended for {Seconds} s (will message: {WithWill})", duration.TotalSeconds, withWill);
    }

    public void Resume() => _suspendedUntil = DateTimeOffset.MinValue;

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_client.IsConnected)
        {
            try
            {
                // Graceful shutdown: announce offline explicitly (the will is only sent on abnormal loss).
                await _client.PublishAsync(OfflineStatusMessage(), cancellationToken);
                await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Error while disconnecting from MQTT");
            }
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var sender = Task.Run(() => SendLoopAsync(stoppingToken), stoppingToken);
        var delay = TimeSpan.FromSeconds(_broker.ReconnectDelaySeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected && DateTimeOffset.UtcNow >= _suspendedUntil)
                {
                    await ConnectAsync(stoppingToken);
                    delay = TimeSpan.FromSeconds(_broker.ReconnectDelaySeconds);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT broker {Host}:{Port} unreachable ({Error}); retrying in {Delay} s ({Queued} messages buffered)",
                    _broker.Host, _broker.Port, ex.Message, delay.TotalSeconds, QueuedMessages);
                await Task.Delay(delay, stoppingToken);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, _broker.MaxReconnectDelaySeconds));
            }
        }

        await sender;
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var will = OfflineStatusMessage();
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_broker.Host, _broker.Port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId($"{_broker.ClientId}-gateway-{_gateway.VehicleId}")
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_broker.KeepAliveSeconds))
            .WithCleanStart()
            .WithWillTopic(will.Topic)
            .WithWillPayload(will.Payload.ToArray())
            .WithWillRetain()
            .WithWillQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce);
        if (!string.IsNullOrEmpty(_broker.Username))
        {
            builder.WithCredentials(_broker.Username, _broker.Password);
        }

        if (_broker.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls());
        }

        await _client.ConnectAsync(builder.Build(), cancellationToken);

        var vehicleId = _gateway.VehicleId;
        await _client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
            .WithTopicFilter(MqttTopics.DiagnosticRequest(vehicleId), MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(MqttTopics.OtaCommand(vehicleId), MqttQualityOfServiceLevel.AtLeastOnce)
            .WithTopicFilter(MqttTopics.FaultInjection(vehicleId), MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), cancellationToken);

        _logger.LogInformation("Gateway connected to MQTT broker {Host}:{Port} as vehicle {VehicleId} ({Queued} buffered messages to flush)",
            _broker.Host, _broker.Port, vehicleId, QueuedMessages);
    }

    private async Task SendLoopAsync(CancellationToken cancellationToken)
    {
        MqttApplicationMessage? pending = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                pending ??= await _outbox.Reader.ReadAsync(cancellationToken);
                if (!_client.IsConnected)
                {
                    await Task.Delay(200, cancellationToken);
                    continue;
                }

                await _client.PublishAsync(pending, cancellationToken);
                _metrics.MessagePublished();
                pending = null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // Keep the message and retry after reconnection.
                _logger.LogDebug(ex, "Publish failed; message kept for retry");
                await Task.Delay(200, cancellationToken).ContinueWith(_ => { }, TaskScheduler.Default);
            }
        }
    }

    private MqttApplicationMessage OfflineStatusMessage()
    {
        var status = new VehicleStatusMessage
        {
            VehicleId = _gateway.VehicleId,
            Timestamp = DateTimeOffset.UtcNow,
            Connectivity = ConnectivityStatus.Offline,
            GatewayId = _gateway.GatewayId,
        };
        return new MqttApplicationMessageBuilder()
            .WithTopic(MqttTopics.Status(_gateway.VehicleId))
            .WithPayload(AutoSphereJson.Serialize(status))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag()
            .Build();
    }
}
