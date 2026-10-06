using AutoSphere.Application.Abstractions;
using AutoSphere.Contracts.Mqtt;
using AutoSphere.Domain.Common;
using AutoSphere.Domain.Vehicles;
using AutoSphere.SharedKernel.Vehicles;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutoSphere.Application.Vehicles;

public sealed class RegisterVehicleValidator : AbstractValidator<RegisterVehicleRequest>
{
    public RegisterVehicleValidator()
    {
        RuleFor(r => r.VehicleId).NotEmpty().MaximumLength(64)
            .Must(MqttTopics.IsValidVehicleId).WithMessage("Vehicle id may only contain letters, digits, '-' and '_'.");
        RuleFor(r => r.Vin).NotEmpty().Length(17)
            .Matches("^[A-HJ-NPR-Z0-9]{17}$").WithMessage("VIN must be 17 characters without I, O or Q.");
        RuleFor(r => r.Model).NotEmpty().MaximumLength(128);
        RuleFor(r => r.ModelYear).InclusiveBetween(1990, 2100);
        RuleForEach(r => r.Ecus).ChildRules(ecu =>
        {
            ecu.RuleFor(e => e.EcuId).NotEmpty().MaximumLength(32);
            ecu.RuleFor(e => e.Name).NotEmpty().MaximumLength(128);
            ecu.RuleFor(e => e.Type).IsInEnum();
        });
    }
}

/// <summary>Vehicle management use cases.</summary>
public sealed class VehicleService(
    IAutoSphereDbContext db,
    VehicleRegistry registry,
    IValidator<RegisterVehicleRequest> validator,
    TimeProvider timeProvider,
    ILogger<VehicleService> logger)
{
    public async Task<IReadOnlyList<VehicleSummaryDto>> ListAsync(CancellationToken cancellationToken)
    {
        var vehicles = await db.Vehicles.AsNoTracking().Include(v => v.Ecus).OrderBy(v => v.VehicleId).ToListAsync(cancellationToken);
        return vehicles.Select(v => v.ToSummary()).ToList();
    }

    public async Task<VehicleDetailsDto> GetAsync(string vehicleId, CancellationToken cancellationToken) =>
        (await LoadAsync(vehicleId, tracking: false, cancellationToken)).ToDetails();

    public async Task<VehicleDetailsDto> RegisterAsync(RegisterVehicleRequest request, CancellationToken cancellationToken)
    {
        await validator.ValidateAndThrowAsync(request, cancellationToken);
        var id = request.VehicleId.ToUpperInvariant();
        var vin = request.Vin.ToUpperInvariant();
        if (await db.Vehicles.AnyAsync(v => v.VehicleId == id || v.Vin == vin, cancellationToken))
        {
            throw new DomainException($"A vehicle with id {id} or VIN {request.Vin} is already registered.");
        }

        var vehicle = Vehicle.Register(request.VehicleId, request.Vin, request.Model, request.ModelYear, timeProvider.GetUtcNow());
        foreach (var ecu in request.Ecus ?? [])
        {
            vehicle.AddOrGetEcu(ecu.EcuId, ecu.Type, ecu.Name, ecu.SoftwareVersion);
        }

        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync(cancellationToken);
        registry.Register(vehicle.VehicleId, vehicle.Id);
        logger.LogInformation("Vehicle {VehicleId} registered with {EcuCount} ECUs", vehicle.VehicleId, vehicle.Ecus.Count);
        return vehicle.ToDetails();
    }

    public async Task DeleteAsync(string vehicleId, CancellationToken cancellationToken)
    {
        var vehicle = await LoadAsync(vehicleId, tracking: true, cancellationToken);
        db.Vehicles.Remove(vehicle);
        await db.SaveChangesAsync(cancellationToken);
        registry.Remove(vehicle.VehicleId);
        logger.LogWarning("Vehicle {VehicleId} deleted", vehicle.VehicleId);
    }

    public async Task<IReadOnlyList<HealthSnapshotDto>> GetHealthHistoryAsync(string vehicleId, int take, CancellationToken cancellationToken)
    {
        var vehicle = await LoadAsync(vehicleId, tracking: false, cancellationToken);
        var snapshots = await db.VehicleHealthSnapshots.AsNoTracking()
            .Where(s => s.VehicleKey == vehicle.Id)
            .OrderByDescending(s => s.Id)
            .Take(Math.Clamp(take, 1, 500))
            .ToListAsync(cancellationToken);
        return snapshots.Select(s => new HealthSnapshotDto(s.Timestamp, s.Status, s.Score, s.Summary)).ToList();
    }

    public async Task<Vehicle> LoadAsync(string vehicleId, bool tracking, CancellationToken cancellationToken)
    {
        var id = vehicleId.ToUpperInvariant();
        var query = db.Vehicles.Include(v => v.Ecus).Where(v => v.VehicleId == id);
        var vehicle = await (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(cancellationToken);
        return vehicle ?? throw NotFoundException.For("Vehicle", vehicleId);
    }

    /// <summary>Ensures the ECU belongs to the vehicle (used before ECU-specific operations).</summary>
    public static Ecu RequireEcu(Vehicle vehicle, string ecuId) =>
        vehicle.FindEcu(ecuId) ?? throw NotFoundException.For("ECU", $"{vehicle.VehicleId}/{ecuId}");
}
