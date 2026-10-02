using System.Text;
using AutoSphere.CanBus;
using AutoSphere.CanBus.IsoTp;
using AutoSphere.Contracts.Messages;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Messaging;
using AutoSphere.VehicleGateway.Network;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.RemoteDiagnostics;

/// <summary>
/// Periodically reads the fault memory and identification of every reachable ECU (like a telematics
/// unit's remote-diagnostics scheduler), merges the gateway's own network DTCs and publishes a
/// <see cref="DtcReportMessage"/> whenever anything changed.
/// </summary>
public sealed class DtcMonitorService(
    DiagnosticClientPool clients,
    UdsDiagnosticService diagnostics,
    EcuNetworkMonitor network,
    GatewayMqttClient mqtt,
    IOptions<GatewayOptions> options,
    TimeProvider timeProvider,
    ILogger<DtcMonitorService> logger) : BackgroundService
{
    private static readonly TimeSpan RepublishInterval = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, IReadOnlyList<DtcDto>> _lastByEcu = new(StringComparer.Ordinal);
    private readonly Dictionary<(string EcuId, string Code), IReadOnlyList<DtcSnapshotValueDto>> _snapshots = [];
    private readonly SemaphoreSlim _pollNow = new(0, 1);
    private string _lastFingerprint = string.Empty;
    private DateTimeOffset _lastPublished = DateTimeOffset.MinValue;

    /// <summary>Requests an immediate poll (e.g. after DTCs were cleared).</summary>
    public void RequestPoll()
    {
        if (_pollNow.CurrentCount == 0)
        {
            try
            {
                _pollNow.Release();
            }
            catch (SemaphoreFullException)
            {
            }
        }
    }

    public override void Dispose()
    {
        _pollNow.Dispose();
        base.Dispose();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMilliseconds(options.Value.DtcPollIntervalMs);
        await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            await PollAsync(stoppingToken);
            await _pollNow.WaitAsync(interval, stoppingToken);
        }
    }

    public async Task PollAsync(CancellationToken cancellationToken)
    {
        var reported = new List<string> { diagnostics.GatewayId };
        foreach (var node in network.Nodes)
        {
            if (!node.IsReachable || node.IsUpdating)
            {
                continue;
            }

            try
            {
                var client = await clients.GetAsync(node.Type);
                if (node.SoftwareVersion is null)
                {
                    var version = await client.ReadDataByIdentifierAsync(AutoSphere.Diagnostics.DataIdentifiers.DataIdentifierCatalog.SoftwareVersion, null, cancellationToken);
                    var hardware = await client.ReadDataByIdentifierAsync(AutoSphere.Diagnostics.DataIdentifiers.DataIdentifierCatalog.HardwareVersion, null, cancellationToken);
                    node.SoftwareVersion = version.Text;
                    node.HardwareVersion = hardware.Text;
                }

                var dtcs = await diagnostics.ReadDtcsAsync(client, node, includeSnapshots: false, null, cancellationToken);
                _lastByEcu[node.EcuId] = await AttachSnapshotsAsync(client, node.EcuId, dtcs, cancellationToken);
                var active = dtcs.Where(d => d.TestFailed).ToList();
                node.ActiveDtcCount = active.Count;
                node.WorstActiveDtcSeverity = active.Count == 0 ? null : active.Max(d => d.Severity);
                reported.Add(node.EcuId);
            }
            catch (Exception ex) when (ex is UdsException or IsoTpException or CanBusException)
            {
                logger.LogDebug("DTC poll of {EcuId} failed: {Error}", node.EcuId, ex.Message);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        _lastByEcu[diagnostics.GatewayId] = diagnostics.GatewayDtcs();
        PublishIfChanged(reported);
    }

    private async Task<IReadOnlyList<DtcDto>> AttachSnapshotsAsync(UdsClient client, string ecuId, IReadOnlyList<DtcDto> dtcs, CancellationToken cancellationToken)
    {
        foreach (var key in _snapshots.Keys.Where(k => k.EcuId == ecuId && dtcs.All(d => d.Code != k.Code)).ToList())
        {
            _snapshots.Remove(key);
        }

        var result = new List<DtcDto>(dtcs.Count);
        foreach (var dtc in dtcs)
        {
            if (dtc.Confirmed && !_snapshots.ContainsKey((ecuId, dtc.Code)))
            {
                var values = await client.ReadDtcSnapshotAsync(DtcCode.Parse(dtc.Code), null, cancellationToken);
                _snapshots[(ecuId, dtc.Code)] = values.Select(v => new DtcSnapshotValueDto(v.Definition.Name, v.Numeric ?? 0, v.Definition.Unit ?? string.Empty)).ToList();
            }

            result.Add(_snapshots.TryGetValue((ecuId, dtc.Code), out var snapshot) ? dtc with { Snapshot = snapshot } : dtc);
        }

        return result;
    }

    private void PublishIfChanged(IReadOnlyList<string> reportedEcuIds)
    {
        var dtcs = reportedEcuIds.SelectMany(id => _lastByEcu.GetValueOrDefault(id, [])).ToList();
        var fingerprint = new StringBuilder();
        foreach (var dtc in dtcs.OrderBy(d => d.EcuId, StringComparer.Ordinal).ThenBy(d => d.Code, StringComparer.Ordinal))
        {
            fingerprint.Append(dtc.EcuId).Append(':').Append(dtc.Code).Append(':').Append(dtc.StatusMask).Append(';');
        }

        fingerprint.Append('|').Append(string.Join(',', reportedEcuIds));
        var now = timeProvider.GetUtcNow();
        if (fingerprint.ToString() == _lastFingerprint && now - _lastPublished < RepublishInterval)
        {
            return;
        }

        _lastFingerprint = fingerprint.ToString();
        _lastPublished = now;
        mqtt.Publish(MqttTopics.Dtcs(options.Value.VehicleId), new DtcReportMessage
        {
            VehicleId = options.Value.VehicleId,
            Timestamp = now,
            ReportedEcuIds = reportedEcuIds,
            Dtcs = dtcs,
        });
        logger.LogInformation("DTC report published: {Count} DTCs ({Active} active) from {Ecus}",
            dtcs.Count, dtcs.Count(d => d.TestFailed), string.Join(", ", reportedEcuIds));
    }
}
