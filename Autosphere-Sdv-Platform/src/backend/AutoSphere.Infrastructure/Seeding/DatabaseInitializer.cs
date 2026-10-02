using AutoSphere.Application.Common;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Vehicles;
using AutoSphere.Domain.Vehicles;
using AutoSphere.Infrastructure.Identity;
using AutoSphere.Infrastructure.Persistence;
using AutoSphere.SharedKernel.Ota;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Infrastructure.Seeding;

/// <summary>Configuration section <c>Seed</c>. Passwords come from environment variables / user secrets.</summary>
public sealed class SeedOptions
{
    public const string SectionName = "Seed";

    public bool Enabled { get; set; }

    public string? AdminPassword { get; set; }

    public string? EngineerPassword { get; set; }

    public string? ViewerPassword { get; set; }

    /// <summary>Registers vehicle AUTO-001 with its ECUs.</summary>
    public bool DemoVehicle { get; set; } = true;

    /// <summary>Creates signed sample packages for the OTA demonstration.</summary>
    public bool DemoPackages { get; set; } = true;
}

/// <summary>Applies migrations (PostgreSQL) or creates the schema (SQLite) and seeds development data.</summary>
public sealed class DatabaseInitializer(
    AutoSphereDbContext db,
    UserManager<ApplicationUser> users,
    RoleManager<IdentityRole> roles,
    VehicleRegistry registry,
    SoftwarePackageService packages,
    IOptions<SeedOptions> options,
    TimeProvider timeProvider,
    ILogger<DatabaseInitializer> logger)
{
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (db.Database.IsSqlite())
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }
        else
        {
            await db.Database.MigrateAsync(cancellationToken);
        }

        foreach (var role in Roles.All)
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }

        // Registry warm-up: all registered vehicles are accepted on the MQTT path immediately.
        foreach (var vehicle in await db.Vehicles.AsNoTracking().Select(v => new { v.VehicleId, v.Id }).ToListAsync(cancellationToken))
        {
            registry.Register(vehicle.VehicleId, vehicle.Id);
        }

        var seed = options.Value;
        if (!seed.Enabled)
        {
            return;
        }

        await SeedUserAsync("admin", "admin@autosphere.local", seed.AdminPassword, Roles.Administrator);
        await SeedUserAsync("engineer", "engineer@autosphere.local", seed.EngineerPassword, Roles.Engineer);
        await SeedUserAsync("viewer", "viewer@autosphere.local", seed.ViewerPassword, Roles.Viewer);

        if (seed.DemoVehicle && !await db.Vehicles.AnyAsync(cancellationToken))
        {
            var vehicle = Vehicle.Register("AUTO-001", "WASPH1EV2T0000001", "AutoSphere EV Prototype", 2026, timeProvider.GetUtcNow());
            vehicle.AddOrGetEcu("CGW-001", EcuType.CentralGateway, "Central Gateway", "1.0.0");
            vehicle.AddOrGetEcu("VCU-001", EcuType.VehicleControlUnit, "Vehicle Control Unit", "1.0.0");
            vehicle.AddOrGetEcu("MCU-001", EcuType.MotorControlUnit, "Motor Control Unit", "1.0.0");
            vehicle.AddOrGetEcu("BMS-001", EcuType.BatteryManagementSystem, "Battery Management System", "1.0.0");
            vehicle.AddOrGetEcu("BCM-001", EcuType.BodyControlModule, "Body Control Module", "1.0.0");
            db.Vehicles.Add(vehicle);
            await db.SaveChangesAsync(cancellationToken);
            registry.Register(vehicle.VehicleId, vehicle.Id);
            logger.LogInformation("Seeded demo vehicle {VehicleId}", vehicle.VehicleId);
        }

        if (seed.DemoPackages && !await db.SoftwarePackages.AnyAsync(cancellationToken))
        {
            await packages.CreateSampleAsync(new CreateSamplePackageRequest(EcuType.BatteryManagementSystem, "1.1.0", "1.0.0",
                ReleaseNotes: "Improved cell balancing (simulated firmware)."), cancellationToken);
            await packages.CreateSampleAsync(new CreateSamplePackageRequest(EcuType.BatteryManagementSystem, "1.2.0", "1.0.0", FirmwareBootBehavior.CrashLoop,
                "Faulty build: crashes after boot. Demonstrates post-install health check failure and automatic rollback."), cancellationToken);
            await packages.CreateSampleAsync(new CreateSamplePackageRequest(EcuType.MotorControlUnit, "1.1.0", "1.0.0", FirmwareBootBehavior.SelfTestFailure,
                "Faulty build: power-on self-test fails (P0606). Demonstrates rollback triggered by a critical DTC."), cancellationToken);
            logger.LogInformation("Seeded signed demo OTA packages");
        }
    }

    private async Task SeedUserAsync(string userName, string email, string? password, string role)
    {
        if (await users.FindByNameAsync(userName) is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed user {UserName} skipped: no password configured (Seed__{Role}Password)", userName, role == Roles.Administrator ? "Admin" : role);
            return;
        }

        var user = new ApplicationUser { UserName = userName, Email = email, EmailConfirmed = true };
        var result = await users.CreateAsync(user, password);
        if (!result.Succeeded)
        {
            logger.LogWarning("Seed user {UserName} could not be created: {Errors}", userName, string.Join(" ", result.Errors.Select(e => e.Description)));
            return;
        }

        await users.AddToRoleAsync(user, role);
        logger.LogInformation("Seeded user {UserName} with role {Role}", userName, role);
    }
}

public static class DatabaseInitializerExtensions
{
    public static async Task InitializeDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().InitializeAsync(cancellationToken);
    }
}
