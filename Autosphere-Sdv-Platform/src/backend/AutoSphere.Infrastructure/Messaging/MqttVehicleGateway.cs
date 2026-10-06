using System.Buffers;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Messaging;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Formatter;
using MQTTnet.Protocol;

namespace AutoSphere.Infrastructure.Messaging;

/// <summary>
/// The backend's MQTT session. Inbound vehicle messages are queued and processed by two consumers:
/// a fast lane for telemetry and an ordered lane for state-changing messages (status, DTCs, alerts,
/// diagnostic responses, OTA progress), so that the MQTT receive loop is never blocked by database work.
/// </summary>
public sealed class MqttVehicleGateway : BackgroundService, IVehicleCommandPublisher
{
    private static readonly Meter Meter = new("AutoSphere.Backend.Mqtt", "1.0");
    private static readonly Counter<long> MessagesReceived = Meter.CreateCounter<long>("autosphere.backend.mqtt.messages_received", "{message}");

    private readonly MqttBrokerOptions _options;
    private readonly VehicleMessageDispatcher _dispatcher;
    private readonly ILogger<MqttVehicleGateway> _logger;
    private readonly IMqttClient _client;
    private readonly Channel<(VehicleTopicKind Kind, string VehicleId, byte[] Payload)> _telemetry =
        Channel.CreateBounded<(VehicleTopicKind, string, byte[])>(new BoundedChannelOptions(5000) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly Channel<(VehicleTopicKind Kind, string VehicleId, byte[] Payload)> _ordered =
        Channel.CreateUnbounded<(VehicleTopicKind, string, byte[])>();

    public MqttVehicleGateway(IOptions<MqttBrokerOptions> options, VehicleMessageDispatcher dispatcher, ILogger<MqttVehicleGateway> logger)
    {
        _options = options.Value;
        _dispatcher = dispatcher;
        _logger = logger;
        _client = new MqttClientFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += args =>
        {
            if (MqttTopics.TryParse(args.ApplicationMessage.Topic, out var vehicleId, out var kind))
            {
                MessagesReceived.Add(1);
                var item = (kind, vehicleId, args.ApplicationMessage.Payload.ToArray());
                (kind == VehicleTopicKind.Telemetry ? _telemetry : _ordered).Writer.TryWrite(item);
            }

            return Task.CompletedTask;
        };
    }

    public bool IsConnected => _client.IsConnected;

    public Task PublishDiagnosticRequestAsync(DiagnosticRequestMessage request, CancellationToken cancellationToken) =>
        PublishAsync(MqttTopics.DiagnosticRequest(request.VehicleId), request, cancellationToken);

    public Task PublishOtaCommandAsync(OtaUpdateCommand command, CancellationToken cancellationToken) =>
        PublishAsync(MqttTopics.OtaCommand(command.VehicleId), command, cancellationToken);

    public Task PublishFaultInjectionAsync(FaultInjectionCommand command, CancellationToken cancellationToken) =>
        PublishAsync(MqttTopics.FaultInjection(command.VehicleId), command, cancellationToken);

    public override void Dispose()
    {
        _client.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var consumers = new[]
        {
            Task.Run(() => ConsumeAsync(_telemetry.Reader, stoppingToken), stoppingToken),
            Task.Run(() => ConsumeAsync(_ordered.Reader, stoppingToken), stoppingToken),
        };

        var delay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!_client.IsConnected)
                {
                    await ConnectAsync(stoppingToken);
                    delay = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds);
                }

                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("MQTT broker {Host}:{Port} unreachable ({Error}); retrying in {Delay} s",
                    _options.Host, _options.Port, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, _options.MaxReconnectDelaySeconds));
            }
        }

        if (_client.IsConnected)
        {
            await _client.DisconnectAsync(new MqttClientDisconnectOptionsBuilder().Build(), CancellationToken.None);
        }

        await Task.WhenAll(consumers).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    private async Task ConnectAsync(CancellationToken cancellationToken)
    {
        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithProtocolVersion(MqttProtocolVersion.V500)
            .WithClientId($"{_options.ClientId}-backend-{Environment.MachineName}-{Environment.ProcessId}")
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(_options.KeepAliveSeconds))
            .WithCleanStart();
        if (!string.IsNullOrEmpty(_options.Username))
        {
            builder.WithCredentials(_options.Username, _options.Password);
        }

        if (_options.UseTls)
        {
            builder.WithTlsOptions(tls => tls.UseTls());
        }

        await _client.ConnectAsync(builder.Build(), cancellationToken);
        var subscriptions = new MqttClientSubscribeOptionsBuilder();
        foreach (var kind in new[]
                 {
                     VehicleTopicKind.Telemetry, VehicleTopicKind.Status, VehicleTopicKind.Dtcs, VehicleTopicKind.Alerts,
                     VehicleTopicKind.DiagnosticResponse, VehicleTopicKind.OtaStatus, VehicleTopicKind.FaultInjectionAck,
                 })
        {
            subscriptions.WithTopicFilter(MqttTopics.AllVehicles(kind),
                kind == VehicleTopicKind.Telemetry ? MqttQualityOfServiceLevel.AtMostOnce : MqttQualityOfServiceLevel.AtLeastOnce);
        }

        await _client.SubscribeAsync(subscriptions.Build(), cancellationToken);
        _logger.LogInformation("Backend connected to MQTT broker {Host}:{Port}", _options.Host, _options.Port);
    }

    private async Task ConsumeAsync(ChannelReader<(VehicleTopicKind Kind, string VehicleId, byte[] Payload)> reader, CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var (kind, vehicleId, payload) in reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _dispatcher.DispatchAsync(kind, vehicleId, payload, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One bad message must never stop the ingestion pipeline.
                    _logger.LogError(ex, "Processing {Kind} from {VehicleId} failed", kind, vehicleId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async Task PublishAsync<T>(string topic, T message, CancellationToken cancellationToken)
    {
        if (!_client.IsConnected)
        {
            throw new ServiceUnavailableException("The MQTT broker is not reachable; the command was not sent.");
        }

        await _client.PublishAsync(new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(AutoSphereJson.Serialize(message))
            .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
            .Build(), cancellationToken);
    }
}

public sealed class MqttHealthCheck(MqttVehicleGateway mqtt) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(mqtt.IsConnected ? HealthCheckResult.Healthy("Connected to the MQTT broker.") : HealthCheckResult.Unhealthy("Not connected to the MQTT broker."));
}
