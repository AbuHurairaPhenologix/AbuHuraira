using System.Buffers;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.EcuSimulation;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;

namespace AutoSphere.VehicleSimulator;

/// <summary>
/// Receives fault-injection commands for the simulated ECUs when the simulator runs as its own process.
/// (When the simulation is hosted inside the gateway, the gateway forwards these commands directly.)
/// </summary>
public sealed class SimulationControlListener(
    VehicleSimulation simulation,
    IOptions<MqttBrokerOptions> brokerOptions,
    ILogger<SimulationControlListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = brokerOptions.Value;
        var vehicleId = simulation.Options.VehicleId;
        using var client = new MqttClientFactory().CreateMqttClient();
        client.ApplicationMessageReceivedAsync += async args =>
        {
            FaultInjectionCommand command;
            try
            {
                command = AutoSphereJson.Deserialize<FaultInjectionCommand>(args.ApplicationMessage.Payload.ToArray());
            }
            catch (System.Text.Json.JsonException ex)
            {
                logger.LogWarning(ex, "Ignoring malformed fault-injection command");
                return;
            }

            if (!SimulationFaultInjector.Handles(command.Fault))
            {
                return; // gateway- or backend-level fault
            }

            var result = simulation.FaultInjector.Apply(command);
            var ack = new FaultInjectionAck
            {
                VehicleId = vehicleId,
                Timestamp = DateTimeOffset.UtcNow,
                CorrelationId = command.CorrelationId,
                Fault = command.Fault,
                Action = command.Action,
                Accepted = result.Accepted,
                Message = result.Message,
                HandledBy = "simulator",
            };
            await client.PublishAsync(new MqttApplicationMessageBuilder()
                .WithTopic(MqttTopics.FaultInjectionAck(vehicleId))
                .WithPayload(AutoSphereJson.Serialize(ack))
                .WithQualityOfServiceLevel(MqttQualityOfServiceLevel.AtLeastOnce)
                .Build(), stoppingToken);
        };

        var delay = TimeSpan.FromSeconds(options.ReconnectDelaySeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!client.IsConnected)
                {
                    var builder = new MqttClientOptionsBuilder()
                        .WithTcpServer(options.Host, options.Port)
                        .WithClientId($"{options.ClientId}-simulator-{vehicleId}-{Environment.ProcessId}")
                        .WithKeepAlivePeriod(TimeSpan.FromSeconds(options.KeepAliveSeconds))
                        .WithCleanSession();
                    if (!string.IsNullOrEmpty(options.Username))
                    {
                        builder.WithCredentials(options.Username, options.Password);
                    }

                    await client.ConnectAsync(builder.Build(), stoppingToken);
                    await client.SubscribeAsync(new MqttClientSubscribeOptionsBuilder()
                        .WithTopicFilter(MqttTopics.FaultInjection(vehicleId), MqttQualityOfServiceLevel.AtLeastOnce)
                        .Build(), stoppingToken);
                    logger.LogInformation("Simulation control connected to MQTT {Host}:{Port}", options.Host, options.Port);
                    delay = TimeSpan.FromSeconds(options.ReconnectDelaySeconds);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning("Simulation control cannot reach MQTT broker {Host}:{Port}: {Error}. Retrying in {Delay}s",
                    options.Host, options.Port, ex.Message, delay.TotalSeconds);
                await Task.Delay(delay, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, options.MaxReconnectDelaySeconds));
            }
        }
    }
}
