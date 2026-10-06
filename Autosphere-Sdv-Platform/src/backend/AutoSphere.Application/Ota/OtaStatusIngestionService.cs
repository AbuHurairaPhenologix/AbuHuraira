using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Common;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Alerts;
using AutoSphere.SharedKernel.Diagnostics;
using AutoSphere.SharedKernel.Ota;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AutoSphere.Application.Ota;

/// <summary>Applies OTA progress reported by vehicles and supervises stuck deployments.</summary>
public sealed class OtaStatusIngestionService(
    IAutoSphereDbContext db,
    AlertService alerts,
    IRealtimeNotifier notifier,
    IOptions<OtaOptions> options,
    TimeProvider timeProvider,
    ILogger<OtaStatusIngestionService> logger)
{
    public async Task HandleAsync(OtaStatusMessage message, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var deployment = await db.OtaDeployments.Include(d => d.Events)
            .FirstOrDefaultAsync(d => d.Id == message.DeploymentId && d.VehicleKey == vehicleKey, cancellationToken);
        if (deployment is null)
        {
            logger.LogWarning("OTA status for unknown deployment {DeploymentId} ignored", message.DeploymentId);
            return;
        }

        if (!deployment.ApplyVehicleReport(message.Status, message.ProgressPercent, message.Message, message.InstalledVersion, message.FailureReason, message.Timestamp))
        {
            return;
        }

        if (deployment.Status.IsTerminal())
        {
            var vehicle = await db.Vehicles.Include(v => v.Ecus).FirstAsync(v => v.Id == vehicleKey, cancellationToken);
            var ecu = vehicle.FindEcu(deployment.EcuId);
            if (ecu is not null && deployment.InstalledVersion is not null)
            {
                ecu.SetSoftwareVersion(deployment.InstalledVersion);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("OTA deployment {OtaDeploymentId} on {VehicleId}/{EcuId}: {Status} {Progress}% — {Message}",
            deployment.Id, deployment.VehicleId, deployment.EcuId, deployment.Status, deployment.ProgressPercent, message.Message);
        await notifier.OtaDeploymentAsync(deployment.VehicleId, deployment.ToDto(), cancellationToken);

        if (deployment.Status is OtaUpdateStatus.Failed or OtaUpdateStatus.RolledBack or OtaUpdateStatus.RollbackFailed)
        {
            var severity = deployment.Status == OtaUpdateStatus.RollbackFailed ? DtcSeverity.Critical : DtcSeverity.Warning;
            await alerts.RaiseAsync(deployment.VehicleId, vehicleKey, $"ota-{deployment.Id}", AlertSource.Backend, severity, "Ota",
                $"Update of {deployment.EcuId} to {deployment.ToVersion} {deployment.Status}: {deployment.FailureReason ?? message.Message}",
                message.Timestamp, deployment.EcuId, cancellationToken: cancellationToken);
        }
    }

    /// <summary>Fails deployments that made no progress within the configured timeout.</summary>
    public async Task<int> FailStuckDeploymentsAsync(CancellationToken cancellationToken)
    {
        var limit = timeProvider.GetUtcNow().AddMinutes(-options.Value.DeploymentTimeoutMinutes);
        var candidates = await db.OtaDeployments.Include(d => d.Events)
            .Where(d => d.Status == OtaUpdateStatus.Created || d.Status == OtaUpdateStatus.Pending || d.Status == OtaUpdateStatus.Downloading
                        || d.Status == OtaUpdateStatus.Verifying || d.Status == OtaUpdateStatus.Installing)
            .ToListAsync(cancellationToken);
        var stuck = candidates.Where(d => (d.Events.Count == 0 ? d.CreatedAt : d.Events.Max(e => e.Timestamp)) < limit).ToList();
        foreach (var deployment in stuck)
        {
            deployment.TransitionTo(OtaUpdateStatus.Failed, deployment.ProgressPercent, "No progress reported by the vehicle (timeout).",
                timeProvider.GetUtcNow(), "Timeout");
            logger.LogWarning("OTA deployment {OtaDeploymentId} timed out in state {Status}", deployment.Id, deployment.Status);
        }

        if (stuck.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
            foreach (var deployment in stuck)
            {
                await notifier.OtaDeploymentAsync(deployment.VehicleId, deployment.ToDto(), cancellationToken);
            }
        }

        return stuck.Count;
    }
}
