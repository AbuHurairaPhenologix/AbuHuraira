using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Simulation;
using AutoSphere.SharedKernel.Simulation;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Simulation;

public sealed record FaultInjectionRequest(
    FaultType Fault,
    FaultAction Action = FaultAction.Inject,
    string? TargetEcuId = null,
    int? DurationSeconds = null,
    int? DelayMilliseconds = null,
    Guid? PackageId = null);

public sealed record FaultInjectionDto(
    Guid CorrelationId,
    string VehicleId,
    FaultType Fault,
    string Action,
    string? TargetEcuId,
    int? DurationSeconds,
    string RequestedBy,
    DateTimeOffset RequestedAt,
    bool? Accepted,
    string? Result,
    string? HandledBy,
    DateTimeOffset? AcknowledgedAt);

public sealed class FaultInjectionRequestValidator : AbstractValidator<FaultInjectionRequest>
{
    public FaultInjectionRequestValidator()
    {
        RuleFor(r => r.Fault).IsInEnum();
        RuleFor(r => r.Action).IsInEnum();
        RuleFor(r => r.DurationSeconds).InclusiveBetween(1, 3600).When(r => r.DurationSeconds is not null);
        RuleFor(r => r.DelayMilliseconds).InclusiveBetween(1, 10_000).When(r => r.DelayMilliseconds is not null);
        RuleFor(r => r.TargetEcuId).NotEmpty()
            .When(r => r.Fault is FaultType.EcuCrash or FaultType.CanMessageLoss or FaultType.CanMessageDelay)
            .WithMessage("This fault requires a target ECU.");
        RuleFor(r => r.PackageId).NotNull()
            .When(r => r.Fault == FaultType.CorruptedOtaPackage && r.Action == FaultAction.Inject)
            .WithMessage("A corrupted OTA package needs the package to deploy.");
    }
}

/// <summary>
/// Test-harness fault injection. Only available when <see cref="FaultInjectionOptions.Enabled"/> is set,
/// i.e. in simulation/demo environments.
/// </summary>
public sealed class FaultInjectionService(
    IAutoSphereDbContext db,
    VehicleService vehicles,
    OtaCampaignService campaigns,
    IVehicleCommandPublisher publisher,
    IRealtimeNotifier notifier,
    ICurrentUser currentUser,
    IValidator<FaultInjectionRequest> validator,
    IOptions<FaultInjectionOptions> options,
    TimeProvider timeProvider,
    ILogger<FaultInjectionService> logger)
{
    public bool IsEnabled => options.Value.Enabled;

    public async Task<FaultInjectionDto> InjectAsync(string vehicleId, FaultInjectionRequest request, CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            throw new DomainException("Fault injection is disabled in this environment.");
        }

        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        if (request.TargetEcuId is not null)
        {
            VehicleService.RequireEcu(vehicle, request.TargetEcuId);
        }

        var now = timeProvider.GetUtcNow();
        var record = FaultInjectionRecord.Create(Guid.NewGuid(), vehicle.Id, request.Fault, request.Action.ToString(), request.TargetEcuId,
            request.DurationSeconds, currentUser.UserName, now);
        db.FaultInjections.Add(record);

        if (request.Fault == FaultType.CorruptedOtaPackage)
        {
            if (request.Action == FaultAction.Clear)
            {
                record.Acknowledge(true, "Nothing to clear: corruption only affects the deployment it was injected into.", "backend", now);
            }
            else
            {
                var campaign = await campaigns.CreateAsync(new CreateCampaignRequest("Corrupted package (fault injection)", request.PackageId!.Value,
                    [vehicle.VehicleId], SimulateTransportCorruption: true), cancellationToken);
                record.Acknowledge(true, $"Deployment {campaign.Deployments[0].Id} started with a payload corrupted in transit.", "backend", now);
            }
        }
        else
        {
            await publisher.PublishFaultInjectionAsync(new FaultInjectionCommand
            {
                VehicleId = vehicle.VehicleId,
                Timestamp = now,
                CorrelationId = record.Id,
                Fault = request.Fault,
                Action = request.Action,
                TargetEcuId = request.TargetEcuId,
                DurationSeconds = request.DurationSeconds,
                DelayMilliseconds = request.DelayMilliseconds,
            }, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Fault {Fault} {Action} requested for {VehicleId}/{EcuId} by {User} (correlation {CorrelationId})",
            request.Fault, request.Action, vehicle.VehicleId, request.TargetEcuId ?? "-", currentUser.UserName, record.Id);
        var dto = ToDto(record, vehicle.VehicleId);
        await notifier.FaultInjectionAsync(vehicle.VehicleId, dto, cancellationToken);
        return dto;
    }

    public async Task HandleAckAsync(FaultInjectionAck ack, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ack);
        var record = await db.FaultInjections.FirstOrDefaultAsync(f => f.Id == ack.CorrelationId && f.VehicleKey == vehicleKey, cancellationToken);
        if (record is null)
        {
            return;
        }

        record.Acknowledge(ack.Accepted, ack.Message, ack.HandledBy, ack.Timestamp);
        await db.SaveChangesAsync(cancellationToken);
        await notifier.FaultInjectionAsync(ack.VehicleId, ToDto(record, ack.VehicleId), cancellationToken);
    }

    public async Task<IReadOnlyList<FaultInjectionDto>> HistoryAsync(string vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await vehicles.LoadAsync(vehicleId, tracking: false, cancellationToken);
        var records = await db.FaultInjections.AsNoTracking().Where(f => f.VehicleKey == vehicle.Id)
            .OrderByDescending(f => f.RequestedAt).Take(100).ToListAsync(cancellationToken);
        return records.Select(r => ToDto(r, vehicle.VehicleId)).ToList();
    }

    private static FaultInjectionDto ToDto(FaultInjectionRecord record, string vehicleId) => new(record.Id, vehicleId, record.Fault, record.Action,
        record.TargetEcuId, record.DurationSeconds, record.RequestedBy, record.RequestedAt, record.Accepted, record.Result, record.HandledBy, record.AcknowledgedAt);
}
