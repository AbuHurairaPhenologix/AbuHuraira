using AutoSphere.Api.Security;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoSphere.Api.Controllers;

[ApiController]
[Route("api/vehicles")]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class VehiclesController(VehicleService vehicles) : ControllerBase
{
    /// <summary>Lists all registered vehicles with connectivity and health.</summary>
    [HttpGet]
    public Task<IReadOnlyList<VehicleSummaryDto>> List(CancellationToken cancellationToken) => vehicles.ListAsync(cancellationToken);

    /// <summary>Vehicle details including ECUs, software versions and health.</summary>
    [HttpGet("{vehicleId}")]
    public Task<VehicleDetailsDto> Get(string vehicleId, CancellationToken cancellationToken) => vehicles.GetAsync(vehicleId, cancellationToken);

    [HttpGet("{vehicleId}/ecus")]
    public async Task<IReadOnlyList<EcuDto>> Ecus(string vehicleId, CancellationToken cancellationToken) =>
        (await vehicles.GetAsync(vehicleId, cancellationToken)).Ecus;

    [HttpGet("{vehicleId}/health/history")]
    public Task<IReadOnlyList<HealthSnapshotDto>> HealthHistory(string vehicleId, [FromQuery] int take = 50, CancellationToken cancellationToken = default) =>
        vehicles.GetHealthHistoryAsync(vehicleId, take, cancellationToken);

    [HttpPost]
    [Authorize(Policy = Policies.ManageVehicles)]
    [ProducesResponseType<VehicleDetailsDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<VehicleDetailsDto>> Register(RegisterVehicleRequest request, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.RegisterAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Get), new { vehicleId = vehicle.VehicleId }, vehicle);
    }

    [HttpDelete("{vehicleId}")]
    [Authorize(Policy = Policies.ManageVehicles)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string vehicleId, CancellationToken cancellationToken)
    {
        await vehicles.DeleteAsync(vehicleId, cancellationToken);
        return NoContent();
    }
}

[ApiController]
[Route("api/vehicles/{vehicleId}/telemetry")]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class TelemetryController(TelemetryQueryService telemetry) : ControllerBase
{
    /// <summary>Latest live state (initial load; live updates arrive via SignalR).</summary>
    [HttpGet("latest")]
    [ProducesResponseType<VehicleTelemetryDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<VehicleTelemetryDto>> Latest(string vehicleId, CancellationToken cancellationToken)
    {
        var latest = await telemetry.GetLatestAsync(vehicleId, cancellationToken);
        return latest is null ? NoContent() : Ok(latest);
    }

    /// <summary>Down-sampled history of up to 10 signals (VSS paths).</summary>
    [HttpGet("history")]
    public Task<IReadOnlyList<TelemetrySeriesDto>> History(string vehicleId, [FromQuery(Name = "signal")] string[] signals,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, [FromQuery] int maxPoints = 300, CancellationToken cancellationToken = default) =>
        telemetry.GetHistoryAsync(vehicleId, new TelemetryHistoryQuery(signals, from, to, maxPoints), cancellationToken);
}

[ApiController]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class AlertsController(AlertService alerts) : ControllerBase
{
    [HttpGet("api/vehicles/{vehicleId}/alerts")]
    public Task<IReadOnlyList<AlertDto>> List(string vehicleId, [FromQuery] bool activeOnly = false, [FromQuery] int take = 100,
        CancellationToken cancellationToken = default) => alerts.ListAsync(vehicleId, activeOnly, take, cancellationToken);

    [HttpPost("api/alerts/{alertId:guid}/acknowledge")]
    [Authorize(Policy = Policies.OperateDiagnostics)]
    public Task<AlertDto> Acknowledge(Guid alertId, CancellationToken cancellationToken) => alerts.AcknowledgeAsync(alertId, cancellationToken);
}

[ApiController]
[Route("api/vehicles/{vehicleId}/ota")]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class VehicleOtaController(OtaCampaignService campaigns) : ControllerBase
{
    /// <summary>OTA update history of the vehicle.</summary>
    [HttpGet("history")]
    public Task<IReadOnlyList<OtaDeploymentDto>> History(string vehicleId, CancellationToken cancellationToken) =>
        campaigns.ListDeploymentsAsync(vehicleId, cancellationToken);
}
