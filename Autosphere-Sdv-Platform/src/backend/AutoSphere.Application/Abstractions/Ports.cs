using AutoSphere.Application.Alerts;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.Domain.Ota;
using AutoSphere.Domain.Simulation;
using AutoSphere.Domain.Telemetry;
using AutoSphere.Domain.Vehicles;
using AutoSphere.SharedKernel.Ota;
using Microsoft.EntityFrameworkCore;

namespace AutoSphere.Application.Abstractions;

/// <summary>
/// Unit of work over the AutoSphere database. EF Core's <see cref="DbSet{TEntity}"/> already is a
/// repository, so no additional repository wrappers are introduced.
/// </summary>
public interface IAutoSphereDbContext
{
    DbSet<Vehicle> Vehicles { get; }

    DbSet<Ecu> Ecus { get; }

    DbSet<TelemetryRecord> TelemetryRecords { get; }

    DbSet<DiagnosticTroubleCode> DiagnosticTroubleCodes { get; }

    DbSet<DiagnosticSession> DiagnosticSessions { get; }

    DbSet<Alert> Alerts { get; }

    DbSet<SoftwarePackage> SoftwarePackages { get; }

    DbSet<OtaCampaign> OtaCampaigns { get; }

    DbSet<OtaDeployment> OtaDeployments { get; }

    DbSet<VehicleHealthSnapshot> VehicleHealthSnapshots { get; }

    DbSet<FaultInjectionRecord> FaultInjections { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>Sends commands to vehicles (implemented over MQTT).</summary>
public interface IVehicleCommandPublisher
{
    bool IsConnected { get; }

    Task PublishDiagnosticRequestAsync(DiagnosticRequestMessage request, CancellationToken cancellationToken);

    Task PublishOtaCommandAsync(OtaUpdateCommand command, CancellationToken cancellationToken);

    Task PublishFaultInjectionAsync(FaultInjectionCommand command, CancellationToken cancellationToken);
}

/// <summary>Latest live state per vehicle (Redis, or in-memory when Redis is not configured).</summary>
public interface ILiveVehicleStateCache
{
    Task<VehicleLiveState?> GetAsync(string vehicleId, CancellationToken cancellationToken);

    Task SetAsync(VehicleLiveState state, CancellationToken cancellationToken);
}

/// <summary>Pushes changes to connected dashboards (SignalR).</summary>
public interface IRealtimeNotifier
{
    Task TelemetryAsync(VehicleTelemetryDto telemetry, CancellationToken cancellationToken);

    Task VehicleUpdatedAsync(VehicleDetailsDto vehicle, CancellationToken cancellationToken);

    Task DtcsChangedAsync(string vehicleId, IReadOnlyList<DtcRecordDto> activeAndStored, CancellationToken cancellationToken);

    Task AlertAsync(string vehicleId, AlertDto alert, CancellationToken cancellationToken);

    Task OtaDeploymentAsync(string vehicleId, OtaDeploymentDto deployment, CancellationToken cancellationToken);

    Task DiagnosticCompletedAsync(string vehicleId, DiagnosticSessionDto session, CancellationToken cancellationToken);

    Task FaultInjectionAsync(string vehicleId, FaultInjectionDto fault, CancellationToken cancellationToken);
}

/// <summary>Signs OTA package manifests with the platform's private key.</summary>
public interface IPackageSigner
{
    /// <summary>Fingerprint of the signing key.</summary>
    string KeyId { get; }

    /// <summary>PEM-encoded public key that vehicles must trust.</summary>
    string PublicKeyPem { get; }

    byte[] Sign(PackageManifest manifest);
}

/// <summary>The user executing the current operation.</summary>
public interface ICurrentUser
{
    string UserName { get; }
}

/// <summary>Receives sampled telemetry for batched persistence.</summary>
public interface ITelemetrySampleSink
{
    void Enqueue(TelemetryRecord record);
}
