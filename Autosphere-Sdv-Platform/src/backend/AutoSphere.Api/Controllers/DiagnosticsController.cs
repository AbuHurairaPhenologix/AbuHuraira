using AutoSphere.Api.Security;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Diagnostics;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoSphere.Api.Controllers;

public sealed record SessionControlRequest(string EcuId, DiagnosticSessionKind Session);

public sealed record EcuResetRequest(EcuResetKind ResetKind = EcuResetKind.HardReset);

public sealed record ReadDataRequest(IReadOnlyList<ushort> DataIdentifiers);

/// <summary>
/// UDS-inspired remote diagnostics. Each call is forwarded to the vehicle gateway via MQTT and answered
/// with the correlated response; raw request/response bytes are included for traceability.
/// </summary>
[ApiController]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class DiagnosticsController(DiagnosticService diagnostics) : ControllerBase
{
    /// <summary>Executes any supported diagnostic operation.</summary>
    [HttpPost("api/vehicles/{vehicleId}/diagnostics")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> Execute(string vehicleId, DiagnosticRequest request, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, request, cancellationToken));

    /// <summary>Full scan: identification, live data and fault memory of every ECU, with an interpreted diagnosis.</summary>
    [HttpPost("api/vehicles/{vehicleId}/diagnostics/scan")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> Scan(string vehicleId, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.FullScan), cancellationToken));

    /// <summary>DiagnosticSessionControl (0x10) on one ECU.</summary>
    [HttpPost("api/vehicles/{vehicleId}/diagnostics/session")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> Session(string vehicleId, SessionControlRequest request, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.SessionControl, request.EcuId, Session: request.Session), cancellationToken));

    [HttpGet("api/vehicles/{vehicleId}/diagnostics")]
    public Task<IReadOnlyList<DiagnosticSessionDto>> History(string vehicleId, [FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        diagnostics.GetHistoryAsync(vehicleId, take, cancellationToken);

    [HttpGet("api/diagnostics/{correlationId:guid}")]
    public Task<DiagnosticSessionDto> Get(Guid correlationId, CancellationToken cancellationToken) => diagnostics.GetSessionAsync(correlationId, cancellationToken);

    /// <summary>Stored DTCs (active and resolved); <c>includeHistory</c> adds cleared ones.</summary>
    [HttpGet("api/vehicles/{vehicleId}/dtcs")]
    public Task<IReadOnlyList<DtcRecordDto>> Dtcs(string vehicleId, [FromQuery] bool includeHistory = false, CancellationToken cancellationToken = default) =>
        diagnostics.GetDtcsAsync(vehicleId, includeHistory, cancellationToken);

    /// <summary>ClearDiagnosticInformation (0x14) for eligible (resolved) DTCs.</summary>
    [HttpPost("api/vehicles/{vehicleId}/dtcs/clear")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> Clear(string vehicleId, ClearDtcsRequest request, CancellationToken cancellationToken) =>
        Run(diagnostics.ClearAsync(vehicleId, request, cancellationToken));

    /// <summary>ECUReset (0x11).</summary>
    [HttpPost("api/vehicles/{vehicleId}/ecus/{ecuId}/reset")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> Reset(string vehicleId, string ecuId, [FromBody] EcuResetRequest? request, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.EcuReset, ecuId, ResetKind: request?.ResetKind ?? EcuResetKind.HardReset), cancellationToken));

    /// <summary>Reads the software and hardware version live from the ECU (ReadDataByIdentifier 0xF189/0xF191).</summary>
    [HttpGet("api/vehicles/{vehicleId}/ecus/{ecuId}/software-version")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> SoftwareVersion(string vehicleId, string ecuId, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.ReadSoftwareVersion, ecuId), cancellationToken));

    /// <summary>ReadDataByIdentifier (0x22) for the given DIDs.</summary>
    [HttpPost("api/vehicles/{vehicleId}/ecus/{ecuId}/data")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<ActionResult<DiagnosticSessionDto>> ReadData(string vehicleId, string ecuId, ReadDataRequest request, CancellationToken cancellationToken) =>
        Run(diagnostics.ExecuteAsync(vehicleId, new DiagnosticRequest(DiagnosticOperation.ReadDataByIdentifier, ecuId, DataIdentifiers: request.DataIdentifiers), cancellationToken));

    /// <summary>A negative diagnostic outcome (e.g. NRC) is still a valid answer (200); only a missing answer is a gateway timeout (504).</summary>
    private static async Task<ActionResult<DiagnosticSessionDto>> Run(Task<DiagnosticSessionDto> operation)
    {
        var session = await operation;
        return session.Status == DiagnosticSessionStatus.TimedOut
            ? new ObjectResult(session) { StatusCode = StatusCodes.Status504GatewayTimeout }
            : new OkObjectResult(session);
    }
}
