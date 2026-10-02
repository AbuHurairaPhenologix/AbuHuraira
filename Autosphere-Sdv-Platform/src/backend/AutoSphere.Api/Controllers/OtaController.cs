using AutoSphere.Api.Security;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AutoSphere.Api.Controllers;

[ApiController]
[Route("api/ota")]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class OtaController(SoftwarePackageService packages, OtaCampaignService campaigns) : ControllerBase
{
    private const long MaxUploadBytes = 2 * 1024 * 1024;

    [HttpGet("packages")]
    public Task<IReadOnlyList<SoftwarePackageDto>> Packages(CancellationToken cancellationToken) => packages.ListAsync(cancellationToken);

    [HttpGet("packages/{packageId:guid}")]
    public Task<SoftwarePackageDto> Package(Guid packageId, CancellationToken cancellationToken) => packages.GetAsync(packageId, cancellationToken);

    /// <summary>Uploads a binary; the backend computes its SHA-256 and signs the manifest.</summary>
    [HttpPost("packages")]
    [Authorize(Policy = Policies.ManageOta)]
    [RequestSizeLimit(MaxUploadBytes)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType<SoftwarePackageDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SoftwarePackageDto>> Upload([FromForm] UploadPackageForm form, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await form.File.CopyToAsync(stream, cancellationToken);
        var package = await packages.CreateAsync(new CreatePackageRequest(form.Name, form.TargetEcuType, form.Version, form.MinimumCompatibleVersion,
            form.ReleaseNotes, stream.ToArray()), cancellationToken);
        return CreatedAtAction(nameof(Package), new { packageId = package.Id }, package);
    }

    /// <summary>Generates and signs a <b>simulated</b> firmware image for the ECU simulators (demo helper).</summary>
    [HttpPost("packages/sample")]
    [Authorize(Policy = Policies.ManageOta)]
    [ProducesResponseType<SoftwarePackageDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<SoftwarePackageDto>> CreateSample(CreateSamplePackageRequest request, CancellationToken cancellationToken)
    {
        var package = await packages.CreateSampleAsync(request, cancellationToken);
        return CreatedAtAction(nameof(Package), new { packageId = package.Id }, package);
    }

    /// <summary>The public key vehicles must trust to verify package signatures (PEM).</summary>
    [HttpGet("trust-anchor")]
    [Produces("application/x-pem-file")]
    public ContentResult TrustAnchor() => Content(packages.TrustedPublicKeyPem, "application/x-pem-file");

    [HttpGet("campaigns")]
    public Task<IReadOnlyList<OtaCampaignDto>> Campaigns(CancellationToken cancellationToken) => campaigns.ListAsync(cancellationToken);

    /// <summary>Starts an OTA campaign: one deployment per vehicle, dispatched immediately.</summary>
    [HttpPost("campaigns")]
    [Authorize(Policy = Policies.ManageOta)]
    [ProducesResponseType<OtaCampaignDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<OtaCampaignDto>> CreateCampaign(CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        var campaign = await campaigns.CreateAsync(request with { SimulateTransportCorruption = false }, cancellationToken);
        return Created($"/api/ota/campaigns/{campaign.Id}", campaign);
    }

    [HttpGet("deployments")]
    public Task<IReadOnlyList<OtaDeploymentDto>> Deployments([FromQuery] string? vehicleId, CancellationToken cancellationToken) =>
        campaigns.ListDeploymentsAsync(vehicleId, cancellationToken);

    [HttpGet("deployments/{deploymentId:guid}")]
    public Task<OtaDeploymentDto> Deployment(Guid deploymentId, CancellationToken cancellationToken) => campaigns.GetDeploymentAsync(deploymentId, cancellationToken);
}

public sealed class UploadPackageForm
{
    public required IFormFile File { get; init; }

    public required string Name { get; init; }

    public required EcuType TargetEcuType { get; init; }

    public required string Version { get; init; }

    public required string MinimumCompatibleVersion { get; init; }

    public string? ReleaseNotes { get; init; }
}

/// <summary>Whether this environment is a simulation/demo environment (readable by every role).</summary>
[ApiController]
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class SimulationController(FaultInjectionService faults) : ControllerBase
{
    [HttpGet("api/simulation/status")]
    public ActionResult<object> Status() => Ok(new { FaultInjectionEnabled = faults.IsEnabled });
}

/// <summary>Test-harness fault injection (Simulation / Development Mode only, Administrators only).</summary>
[ApiController]
[Authorize(Policy = Policies.InjectFaults)]
public sealed class FaultInjectionController(FaultInjectionService faults) : ControllerBase
{
    [HttpGet("api/vehicles/{vehicleId}/faults")]
    public Task<IReadOnlyList<FaultInjectionDto>> History(string vehicleId, CancellationToken cancellationToken) => faults.HistoryAsync(vehicleId, cancellationToken);

    [HttpPost("api/vehicles/{vehicleId}/faults")]
    [ProducesResponseType<FaultInjectionDto>(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<FaultInjectionDto>> Inject(string vehicleId, FaultInjectionRequest request, CancellationToken cancellationToken) =>
        Accepted(await faults.InjectAsync(vehicleId, request, cancellationToken));
}
