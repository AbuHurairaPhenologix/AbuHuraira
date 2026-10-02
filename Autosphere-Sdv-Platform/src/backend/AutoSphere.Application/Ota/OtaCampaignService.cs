using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Ota;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Ota;

public sealed class CreateCampaignValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignValidator()
    {
        RuleFor(r => r.Name).MaximumLength(128);
        RuleFor(r => r.PackageId).NotEmpty();
        RuleFor(r => r.VehicleIds).NotEmpty();
        RuleFor(r => r.VehicleIds.Count).LessThanOrEqualTo(100).When(r => r.VehicleIds is not null);
    }
}

/// <summary>Creates OTA campaigns and dispatches the signed update commands to the vehicles.</summary>
public sealed class OtaCampaignService(
    IAutoSphereDbContext db,
    IVehicleCommandPublisher publisher,
    IRealtimeNotifier notifier,
    ICurrentUser currentUser,
    IValidator<CreateCampaignRequest> validator,
    IOptions<OtaOptions> options,
    TimeProvider timeProvider,
    ILogger<OtaCampaignService> logger)
{
    public async Task<OtaCampaignDto> CreateAsync(CreateCampaignRequest request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var package = await db.SoftwarePackages.FirstOrDefaultAsync(p => p.Id == request.PackageId, cancellationToken)
                      ?? throw NotFoundException.For("Software package", request.PackageId);
        var now = timeProvider.GetUtcNow();
        var campaign = OtaCampaign.Create(request.Name ?? string.Empty, package, currentUser.UserName, now);

        var ids = request.VehicleIds.Select(v => v.ToUpperInvariant()).Distinct().ToList();
        var vehicles = await db.Vehicles.Include(v => v.Ecus).Where(v => ids.Contains(v.VehicleId)).ToListAsync(cancellationToken);
        foreach (var missing in ids.Except(vehicles.Select(v => v.VehicleId)))
        {
            throw NotFoundException.For("Vehicle", missing);
        }

        foreach (var vehicle in vehicles)
        {
            if (vehicle.Connectivity != ConnectivityStatus.Online)
            {
                throw new DomainException($"Vehicle {vehicle.VehicleId} is offline; OTA deployments require a connected gateway.");
            }

            var ecu = vehicle.Ecus.FirstOrDefault(e => e.Type == package.TargetEcuType)
                      ?? throw new DomainException($"Vehicle {vehicle.VehicleId} has no {package.TargetEcuType}.");
            var running = await db.OtaDeployments.AnyAsync(d => d.VehicleKey == vehicle.Id && d.EcuId == ecu.EcuId
                && d.Status != OtaUpdateStatus.Succeeded && d.Status != OtaUpdateStatus.Failed && d.Status != OtaUpdateStatus.RolledBack
                && d.Status != OtaUpdateStatus.RollbackFailed && d.Status != OtaUpdateStatus.Cancelled, cancellationToken);
            if (running)
            {
                throw new DomainException($"An update is already in progress for {vehicle.VehicleId}/{ecu.EcuId}.");
            }

            var deployment = campaign.AddDeployment(vehicle.Id, vehicle.VehicleId, ecu.EcuId, ecu.SoftwareVersion, package, request.SimulateTransportCorruption, now);

            // Persist "Pending" before the command leaves: the vehicle's first progress report may arrive immediately.
            deployment.TransitionTo(OtaUpdateStatus.Pending, 0, "Update command queued for the vehicle", now);
        }

        db.OtaCampaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("OTA campaign {CampaignId} '{Name}' created: {PackageVersion} for {Count} vehicle(s)",
            campaign.Id, campaign.Name, package.Version, campaign.Deployments.Count);

        var dto = ToDto(campaign, package);
        foreach (var deployment in campaign.Deployments)
        {
            try
            {
                await DispatchAsync(deployment, package, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "OTA command for deployment {OtaDeploymentId} could not be sent", deployment.Id);
                deployment.TransitionTo(OtaUpdateStatus.Failed, 0, "The update command could not be delivered to the broker.", timeProvider.GetUtcNow(), ex.Message);
                await db.SaveChangesAsync(cancellationToken);
            }
        }

        return dto;
    }

    public async Task<IReadOnlyList<OtaCampaignDto>> ListAsync(CancellationToken cancellationToken)
    {
        var campaigns = await db.OtaCampaigns.AsNoTracking().Include(c => c.Deployments).ThenInclude(d => d.Events)
            .OrderByDescending(c => c.CreatedAt).Take(100).ToListAsync(cancellationToken);
        var packageIds = campaigns.Select(c => c.PackageId).Distinct().ToList();
        var packages = await db.SoftwarePackages.AsNoTracking().Where(p => packageIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Version, p.TargetEcuType }).ToDictionaryAsync(p => p.Id, cancellationToken);
        return campaigns.Select(c => new OtaCampaignDto(c.Id, c.Name, c.PackageId, packages.GetValueOrDefault(c.PackageId)?.Version ?? "?",
            packages.GetValueOrDefault(c.PackageId)?.TargetEcuType ?? default, c.CreatedAt, c.CreatedBy, c.IsCompleted,
            c.Deployments.Select(d => d.ToDto()).ToList())).ToList();
    }

    public async Task<IReadOnlyList<OtaDeploymentDto>> ListDeploymentsAsync(string? vehicleId, CancellationToken cancellationToken)
    {
        var query = db.OtaDeployments.AsNoTracking().Include(d => d.Events).AsQueryable();
        if (vehicleId is not null)
        {
            var id = vehicleId.ToUpperInvariant();
            query = query.Where(d => d.VehicleId == id);
        }

        var deployments = await query.OrderByDescending(d => d.CreatedAt).Take(200).ToListAsync(cancellationToken);
        return deployments.Select(d => d.ToDto()).ToList();
    }

    public async Task<OtaDeploymentDto> GetDeploymentAsync(Guid id, CancellationToken cancellationToken) =>
        (await db.OtaDeployments.AsNoTracking().Include(d => d.Events).FirstOrDefaultAsync(d => d.Id == id, cancellationToken)
         ?? throw NotFoundException.For("OTA deployment", id)).ToDto();

    private async Task DispatchAsync(OtaDeployment deployment, SoftwarePackage package, CancellationToken cancellationToken)
    {
        var payload = package.Payload;
        if (deployment.SimulateTransportCorruption)
        {
            // Fault injection: corrupt the payload *after* signing, as a damaged transfer would.
            payload = (byte[])payload.Clone();
            for (var i = payload.Length / 2; i < Math.Min(payload.Length, (payload.Length / 2) + 16); i++)
            {
                payload[i] ^= 0x5A;
            }

            logger.LogWarning("Fault injection: payload of deployment {DeploymentId} corrupted in transit", deployment.Id);
        }

        await publisher.PublishOtaCommandAsync(new OtaUpdateCommand
        {
            VehicleId = deployment.VehicleId,
            Timestamp = timeProvider.GetUtcNow(),
            CorrelationId = deployment.Id,
            DeploymentId = deployment.Id,
            TargetEcuId = deployment.EcuId,
            PackageId = package.Id,
            TargetEcuType = package.TargetEcuType,
            Version = package.Version,
            MinimumCompatibleVersion = package.MinimumCompatibleVersion,
            PayloadSha256 = package.PayloadSha256,
            PayloadSize = package.PayloadSize,
            PackageCreatedAt = package.CreatedAt,
            SignatureBase64 = Convert.ToBase64String(package.Signature),
            PayloadBase64 = Convert.ToBase64String(payload),
            HealthCheckSeconds = options.Value.HealthCheckSeconds,
        }, cancellationToken);

        await notifier.OtaDeploymentAsync(deployment.VehicleId, deployment.ToDto(), cancellationToken);
    }

    private static OtaCampaignDto ToDto(OtaCampaign campaign, SoftwarePackage package) => new(campaign.Id, campaign.Name, package.Id, package.Version,
        package.TargetEcuType, campaign.CreatedAt, campaign.CreatedBy, campaign.IsCompleted, campaign.Deployments.Select(d => d.ToDto()).ToList());
}
