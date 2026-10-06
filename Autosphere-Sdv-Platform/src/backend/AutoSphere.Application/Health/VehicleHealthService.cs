using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Vehicles;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.Domain.Vehicles;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Signals;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Health;

/// <summary>
/// Evaluates <see cref="VehicleHealthCalculator"/> with the current state of a vehicle, persists status
/// changes as <see cref="VehicleHealthSnapshot"/>s and notifies dashboards.
/// </summary>
public sealed class VehicleHealthService(
    IAutoSphereDbContext db,
    ILiveVehicleStateCache cache,
    IRealtimeNotifier notifier,
    AlertService alerts,
    IOptions<HealthThresholds> thresholds,
    TimeProvider timeProvider,
    ILogger<VehicleHealthService> logger)
{
    private static readonly TimeSpan PeriodicSnapshot = TimeSpan.FromMinutes(5);

    public async Task<HealthAssessment> EvaluateAsync(Guid vehicleKey, CancellationToken cancellationToken)
    {
        var vehicle = await db.Vehicles.Include(v => v.Ecus).FirstAsync(v => v.Id == vehicleKey, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var live = await cache.GetAsync(vehicle.VehicleId, cancellationToken);
        var activeDtcs = await db.DiagnosticTroubleCodes.AsNoTracking()
            .Where(d => d.VehicleKey == vehicleKey && d.Status == DtcRecordStatus.Active)
            .Select(d => new { d.Code, d.Severity })
            .ToListAsync(cancellationToken);

        double? Signal(string path) => live?.Signals.FirstOrDefault(s => s.Path == path && s.Quality == Contracts.Messages.SignalQuality.Valid)?.Value;

        var input = new HealthInput(
            vehicle.Connectivity,
            live?.ReceivedAt,
            now,
            vehicle.Ecus.Where(e => e.Type != EcuType.CentralGateway).Select(e => new EcuHealthInput(e.EcuId, e.Type, e.Status, e.TimeoutCount + e.E2EErrorCount)).ToList(),
            activeDtcs.Select(d => (d.Code, d.Severity)).ToList(),
            Signal(VssPaths.BatteryTemperature),
            Signal(VssPaths.MotorTemperature),
            live?.Statistics?.FramesRejected ?? 0);

        var assessment = VehicleHealthCalculator.Evaluate(input, thresholds.Value);
        var previousStatus = vehicle.Health;
        var previousScore = vehicle.HealthScore;
        var previousSummary = vehicle.HealthSummary;
        var healthChanged = vehicle.ApplyHealth(assessment, now);

        var lastSnapshot = await db.VehicleHealthSnapshots.AsNoTracking().Where(s => s.VehicleKey == vehicleKey)
            .OrderByDescending(s => s.Id).Select(s => (DateTimeOffset?)s.Timestamp).FirstOrDefaultAsync(cancellationToken);
        if (healthChanged || lastSnapshot is null || now - lastSnapshot > PeriodicSnapshot)
        {
            db.VehicleHealthSnapshots.Add(VehicleHealthSnapshot.Create(vehicleKey, assessment, now));
        }

        // Telemetry stopped although the gateway never said goodbye (e.g. silent MQTT session loss):
        // infer the connectivity loss. Before the first telemetry message nothing can be inferred.
        var connectivityChanged = assessment.Status == HealthStatus.Offline && live is not null
                                  && vehicle.SetConnectivity(ConnectivityStatus.Offline, now);

        await db.SaveChangesAsync(cancellationToken);

        if (healthChanged)
        {
            logger.LogInformation("Vehicle {VehicleId} health {Previous} → {Status} (score {Score}): {Summary}",
                vehicle.VehicleId, previousStatus, assessment.Status, assessment.Score, assessment.Summary);
            await UpdateOfflineAlertAsync(vehicle, assessment, now, cancellationToken);
        }

        if (healthChanged || connectivityChanged || previousScore != assessment.Score || previousSummary != assessment.Summary)
        {
            await notifier.VehicleUpdatedAsync(vehicle.ToDetails(), cancellationToken);
        }

        return assessment;
    }

    private async Task UpdateOfflineAlertAsync(Vehicle vehicle, HealthAssessment assessment, DateTimeOffset now, CancellationToken cancellationToken)
    {
        const string key = "vehicle-offline";
        if (assessment.Status == HealthStatus.Offline)
        {
            await alerts.RaiseAsync(vehicle.VehicleId, vehicle.Id, key, AlertSource.Backend, DtcSeverity.Warning, "Connectivity",
                assessment.Summary, now, cancellationToken: cancellationToken);
        }
        else
        {
            await alerts.ClearAsync(vehicle.VehicleId, vehicle.Id, key, now, cancellationToken);
        }
    }
}
