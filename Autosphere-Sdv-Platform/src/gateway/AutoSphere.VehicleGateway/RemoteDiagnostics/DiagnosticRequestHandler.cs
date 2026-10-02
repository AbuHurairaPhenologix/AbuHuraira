using System.Diagnostics;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Network;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.RemoteDiagnostics;

/// <summary>Executes diagnostic requests from the cloud and publishes the correlated response.</summary>
public sealed class DiagnosticRequestHandler(
    UdsDiagnosticService diagnostics,
    EcuNetworkMonitor network,
    DtcMonitorService dtcMonitor,
    GatewayMqttClient mqtt,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<DiagnosticRequestHandler> logger)
{
    public async Task HandleAsync(DiagnosticRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["CorrelationId"] = request.CorrelationId,
            ["VehicleId"] = request.VehicleId,
            ["Operation"] = request.Operation,
        });
        logger.LogInformation("Diagnostic request {Operation} for {EcuId} received", request.Operation, request.EcuId ?? "all ECUs");

        var stopwatch = Stopwatch.StartNew();
        var targets = request.EcuId is not null
            ? [request.EcuId]
            : network.Nodes.Select(n => n.EcuId).Append(diagnostics.GatewayId).ToList();

        var results = new List<EcuDiagnosticResultDto>();
        foreach (var ecuId in targets)
        {
            results.Add(await diagnostics.ExecuteAsync(request, ecuId, cancellationToken));
        }

        if (request.Operation is DiagnosticOperation.ClearDtcs or DiagnosticOperation.EcuReset)
        {
            dtcMonitor.RequestPoll();
        }

        var success = results.All(r => r.Success);
        mqtt.Publish(MqttTopics.DiagnosticResponse(options.Value.VehicleId), new DiagnosticResponseMessage
        {
            VehicleId = options.Value.VehicleId,
            Timestamp = timeProvider.GetUtcNow(),
            CorrelationId = request.CorrelationId,
            Operation = request.Operation,
            Success = success,
            Error = success ? null : string.Join("; ", results.Where(r => !r.Success).Select(r => $"{r.EcuId}: {r.Error}")),
            Results = results,
            TotalDurationMs = Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2),
        });
        logger.LogInformation("Diagnostic request {Operation} completed in {Duration} ms (success: {Success})",
            request.Operation, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 1), success);
    }
}
