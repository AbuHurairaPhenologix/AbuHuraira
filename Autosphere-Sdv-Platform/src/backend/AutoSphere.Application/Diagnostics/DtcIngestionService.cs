using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Health;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Diagnostics;
using AutoSphere.SharedKernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutoSphere.Application.Diagnostics;

/// <summary>
/// Reconciles the vehicle's fault-memory image with the DTC records in the cloud:
/// new DTC → detected, testFailed cleared → resolved, no longer stored → cleared.
/// </summary>
public sealed class DtcIngestionService(
    IAutoSphereDbContext db,
    AlertService alerts,
    VehicleHealthService health,
    IRealtimeNotifier notifier,
    ILogger<DtcIngestionService> logger)
{
    public async Task HandleAsync(DtcReportMessage message, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var at = message.Timestamp;
        var reportedEcus = message.ReportedEcuIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var stored = await db.DiagnosticTroubleCodes
            .Where(d => d.VehicleKey == vehicleKey && d.Status != DtcRecordStatus.Cleared)
            .ToListAsync(cancellationToken);

        var newlyActive = new List<DiagnosticTroubleCode>();
        var changed = false;
        foreach (var reported in message.Dtcs)
        {
            var snapshot = reported.Snapshot.Select(s => new DtcSnapshotValue(s.Name, s.Value, s.Unit)).ToList();
            var record = stored.FirstOrDefault(d => d.Code == reported.Code && string.Equals(d.EcuId, reported.EcuId, StringComparison.OrdinalIgnoreCase));
            if (record is null)
            {
                record = DiagnosticTroubleCode.Detect(vehicleKey, reported.EcuId, reported.Code, reported.StatusMask, reported.TestFailed,
                    reported.Confirmed, snapshot, at);
                db.DiagnosticTroubleCodes.Add(record);
                stored.Add(record);
                changed = true;
                if (reported.TestFailed)
                {
                    newlyActive.Add(record);
                }

                logger.LogWarning("DTC {Code} ({Description}) detected on {VehicleId}/{EcuId}, status 0x{Status:X2}",
                    record.Code, record.Description, message.VehicleId, record.EcuId, reported.StatusMask);
            }
            else if (record.Observe(reported.StatusMask, reported.TestFailed, reported.Confirmed, snapshot, at))
            {
                changed = true;
                if (record.Status == DtcRecordStatus.Active)
                {
                    newlyActive.Add(record);
                }

                logger.LogInformation("DTC {Code} on {VehicleId}/{EcuId} is now {Status}", record.Code, message.VehicleId, record.EcuId, record.Status);
            }
        }

        // DTCs of ECUs that were read successfully but are absent from the report were cleared in the ECU.
        foreach (var record in stored.Where(d => reportedEcus.Contains(d.EcuId) && d.Status != DtcRecordStatus.Cleared
                                                  && !message.Dtcs.Any(r => r.Code == d.Code && string.Equals(r.EcuId, d.EcuId, StringComparison.OrdinalIgnoreCase))))
        {
            record.MarkCleared(at);
            changed = true;
            logger.LogInformation("DTC {Code} on {VehicleId}/{EcuId} cleared from fault memory", record.Code, message.VehicleId, record.EcuId);
        }

        await db.SaveChangesAsync(cancellationToken);

        foreach (var dtc in newlyActive.Where(d => d.Severity == DtcSeverity.Critical))
        {
            await alerts.RaiseAsync(message.VehicleId, vehicleKey, $"dtc-{dtc.EcuId}-{dtc.Code}", AlertSource.Backend, dtc.Severity, "Diagnostics",
                $"{dtc.FaultCategory}: {dtc.Code} {dtc.Description} on {dtc.EcuId}", at, dtc.EcuId, cancellationToken: cancellationToken);
        }

        foreach (var dtc in stored.Where(d => d.Status != DtcRecordStatus.Active && d.Severity == DtcSeverity.Critical))
        {
            await alerts.ClearAsync(message.VehicleId, vehicleKey, $"dtc-{dtc.EcuId}-{dtc.Code}", at, cancellationToken);
        }

        if (changed)
        {
            await notifier.DtcsChangedAsync(message.VehicleId,
                stored.Where(d => d.Status != DtcRecordStatus.Cleared).Select(d => d.ToDto()).ToList(), cancellationToken);
            await health.EvaluateAsync(vehicleKey, cancellationToken);
        }
    }
}
