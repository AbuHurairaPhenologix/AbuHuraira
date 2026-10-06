using System.Diagnostics;
using System.Globalization;
using AutoSphere.Contracts.Messages;
using AutoSphere.Diagnostics.DataIdentifiers;
using AutoSphere.Diagnostics.FaultMemory;
using AutoSphere.Diagnostics.Uds;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.VehicleGateway.Configuration;
using AutoSphere.VehicleGateway.Instrumentation;
using AutoSphere.VehicleGateway.Network;
using Microsoft.Extensions.Options;

namespace AutoSphere.VehicleGateway.RemoteDiagnostics;

/// <summary>
/// Executes UDS-inspired diagnostic operations on the vehicle's ECUs (the gateway acts as tester) and
/// translates the raw protocol into the typed results sent to the cloud.
/// </summary>
public sealed class UdsDiagnosticService(
    DiagnosticClientPool clients,
    EcuNetworkMonitor network,
    GatewayMetrics metrics,
    IOptions<GatewayOptions> options,
    ILogger<UdsDiagnosticService> logger)
{
    /// <summary>Status mask used to read the whole fault memory.</summary>
    public const DtcStatusBits AllStatusBits = (DtcStatusBits)0xFF;

    public string GatewayId => options.Value.GatewayId;

    public bool IsGateway(string ecuId) => string.Equals(ecuId, GatewayId, StringComparison.OrdinalIgnoreCase);

    public async Task<EcuDiagnosticResultDto> ExecuteAsync(DiagnosticRequestMessage request, string ecuId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var stopwatch = Stopwatch.StartNew();
        if (IsGateway(ecuId))
        {
            return ExecuteOnGateway(request, stopwatch);
        }

        var node = network.Find(ecuId);
        if (node is null)
        {
            return new EcuDiagnosticResultDto(ecuId, false, $"ECU '{ecuId}' is not part of this vehicle.", 0, []);
        }

        var trace = new UdsTrace();
        try
        {
            var client = await clients.GetAsync(node.Type);
            IReadOnlyList<DtcDto>? dtcs = null;
            IReadOnlyList<DataIdentifierValueDto>? data = null;
            switch (request.Operation)
            {
                case DiagnosticOperation.FullScan:
                    await client.TesterPresentAsync(trace, cancellationToken);
                    data = await ReadDataAsync(client, node, DataIdentifierCatalog.Identification.Concat(DataIdentifierCatalog.LiveDataFor(node.Type)), trace, cancellationToken);
                    dtcs = await ReadDtcsAsync(client, node, includeSnapshots: true, trace, cancellationToken);
                    break;
                case DiagnosticOperation.ReadDtcs:
                    dtcs = await ReadDtcsAsync(client, node, includeSnapshots: true, trace, cancellationToken);
                    break;
                case DiagnosticOperation.ClearDtcs:
                    await client.ClearDtcsAsync(request.DtcCode is null ? null : DtcCode.Parse(request.DtcCode), trace, cancellationToken);
                    dtcs = await ReadDtcsAsync(client, node, includeSnapshots: false, trace, cancellationToken);
                    break;
                case DiagnosticOperation.EcuReset:
                    await client.EcuResetAsync((UdsResetType)(int)(request.ResetKind ?? EcuResetKind.HardReset), trace, cancellationToken);
                    node.SoftwareVersion = null; // re-read after the ECU is back
                    break;
                case DiagnosticOperation.ReadDataByIdentifier:
                    var requested = (request.DataIdentifiers ?? []).Select(id => DataIdentifierCatalog.TryGet(id, out var d)
                        ? d
                        : throw new UdsException($"DID 0x{id:X4} is not defined."));
                    data = await ReadDataAsync(client, node, requested, trace, cancellationToken);
                    break;
                case DiagnosticOperation.ReadSoftwareVersion:
                    data = await ReadDataAsync(client, node, [DataIdentifierCatalog.SoftwareVersion, DataIdentifierCatalog.HardwareVersion], trace, cancellationToken);
                    break;
                case DiagnosticOperation.SessionControl:
                    var session = await client.StartSessionAsync((UdsSession)(int)(request.Session ?? DiagnosticSessionKind.Default), trace, cancellationToken);
                    data = [new DataIdentifierValueDto(DataIdentifierCatalog.ActiveDiagnosticSession.Hex, "ActiveDiagnosticSession", session.ToString(), null)];
                    break;
                case DiagnosticOperation.TesterPresent:
                    await client.TesterPresentAsync(trace, cancellationToken);
                    break;
                default:
                    throw new UdsException($"Operation {request.Operation} is not supported.");
            }

            return Result(node.EcuId, true, null, stopwatch, trace, dtcs, data);
        }
        catch (UdsException ex)
        {
            logger.LogWarning("Diagnostic {Operation} on {EcuId} failed: {Error} (correlation {CorrelationId})",
                request.Operation, ecuId, ex.Message, request.CorrelationId);
            return Result(node.EcuId, false, ex.Message, stopwatch, trace, null, null);
        }
        catch (Exception ex) when (ex is AutoSphere.CanBus.IsoTp.IsoTpException or AutoSphere.CanBus.CanBusException)
        {
            await clients.ResetAsync(node.Type);
            return Result(node.EcuId, false, $"Transport error: {ex.Message}", stopwatch, trace, null, null);
        }
    }

    /// <summary>Reads the complete fault memory, optionally including snapshot data for confirmed DTCs.</summary>
    public async Task<IReadOnlyList<DtcDto>> ReadDtcsAsync(UdsClient client, EcuNode node, bool includeSnapshots, UdsTrace? trace, CancellationToken cancellationToken)
    {
        var records = await client.ReadDtcsAsync(AllStatusBits, trace, cancellationToken);
        var result = new List<DtcDto>(records.Count);
        foreach (var record in records)
        {
            IReadOnlyList<DtcSnapshotValueDto> snapshot = [];
            if (includeSnapshots && record.Confirmed)
            {
                var values = await client.ReadDtcSnapshotAsync(record.Code, trace, cancellationToken);
                snapshot = values.Select(v => new DtcSnapshotValueDto(v.Definition.Name, v.Numeric ?? 0, v.Definition.Unit ?? string.Empty)).ToList();
            }

            result.Add(ToDto(record.Code, node.EcuId, record.Status, snapshot));
        }

        return result;
    }

    public IReadOnlyList<DtcDto> GatewayDtcs() =>
        network.GatewayDtcs.Query(AllStatusBits).Select(r => ToDto(r.Code, GatewayId, r.Status, [])).ToList();

    public static DtcDto ToDto(DtcCode code, string ecuId, DtcStatusBits status, IReadOnlyList<DtcSnapshotValueDto> snapshot)
    {
        var definition = KnownDtcs.Describe(code);
        return new DtcDto(code.Value, ecuId, (byte)status, status.HasFlag(DtcStatusBits.TestFailed), status.HasFlag(DtcStatusBits.ConfirmedDtc),
            definition.Description, definition.Severity, definition.FaultCategory, snapshot);
    }

    private async Task<IReadOnlyList<DataIdentifierValueDto>> ReadDataAsync(
        UdsClient client, EcuNode node, IEnumerable<DataIdentifierDefinition> identifiers, UdsTrace trace, CancellationToken cancellationToken)
    {
        var values = new List<DataIdentifierValueDto>();
        foreach (var definition in identifiers.Where(d => DataIdentifierCatalog.IsSupportedBy(d, node.Type)))
        {
            var (_, text, _) = await client.ReadDataByIdentifierAsync(definition, trace, cancellationToken);
            values.Add(new DataIdentifierValueDto(definition.Hex, definition.Name, text, definition.Unit));
            if (definition == DataIdentifierCatalog.SoftwareVersion)
            {
                node.SoftwareVersion = text;
            }
            else if (definition == DataIdentifierCatalog.HardwareVersion)
            {
                node.HardwareVersion = text;
            }
        }

        return values;
    }

    private EcuDiagnosticResultDto ExecuteOnGateway(DiagnosticRequestMessage request, Stopwatch stopwatch)
    {
        switch (request.Operation)
        {
            case DiagnosticOperation.ClearDtcs:
                var cleared = network.GatewayDtcs.Clear(request.DtcCode is null ? null : DtcCode.Parse(request.DtcCode));
                var success = cleared != DtcClearResult.ConditionStillPresent;
                return new EcuDiagnosticResultDto(GatewayId, success, success ? null : "conditionsNotCorrect: the fault condition is still present",
                    stopwatch.Elapsed.TotalMilliseconds, [], GatewayDtcs());
            case DiagnosticOperation.FullScan or DiagnosticOperation.ReadDtcs:
                return new EcuDiagnosticResultDto(GatewayId, true, null, stopwatch.Elapsed.TotalMilliseconds, [], GatewayDtcs(),
                    [new DataIdentifierValueDto(DataIdentifierCatalog.SoftwareVersion.Hex, "ApplicationSoftwareVersion", GatewayVersion, null)]);
            case DiagnosticOperation.ReadSoftwareVersion:
                return new EcuDiagnosticResultDto(GatewayId, true, null, stopwatch.Elapsed.TotalMilliseconds, [], null,
                    [new DataIdentifierValueDto(DataIdentifierCatalog.SoftwareVersion.Hex, "ApplicationSoftwareVersion", GatewayVersion, null)]);
            default:
                return new EcuDiagnosticResultDto(GatewayId, false, $"{request.Operation} is not supported by the gateway node.", 0, []);
        }
    }

    public static string GatewayVersion { get; } =
        typeof(UdsDiagnosticService).Assembly.GetName().Version is { } v
            ? string.Create(CultureInfo.InvariantCulture, $"{v.Major}.{v.Minor}.{v.Build}")
            : "1.0.0";

    private EcuDiagnosticResultDto Result(string ecuId, bool success, string? error, Stopwatch stopwatch, UdsTrace trace,
        IReadOnlyList<DtcDto>? dtcs, IReadOnlyList<DataIdentifierValueDto>? data)
    {
        metrics.DiagnosticCompleted(stopwatch.Elapsed.TotalMilliseconds);
        var exchanges = trace.Exchanges.Select(e => new UdsExchangeDto(
            e.Service.ToString(),
            Convert.ToHexString(e.Request),
            e.Response is null ? null : Convert.ToHexString(e.Response),
            Math.Round(e.Duration.TotalMilliseconds, 2),
            e.NegativeResponse is { } nrc ? $"0x{(byte)nrc:X2} {UdsProtocol.Describe(nrc)}" : null)).ToList();
        return new EcuDiagnosticResultDto(ecuId, success, error, Math.Round(stopwatch.Elapsed.TotalMilliseconds, 2), exchanges, dtcs, data);
    }
}
