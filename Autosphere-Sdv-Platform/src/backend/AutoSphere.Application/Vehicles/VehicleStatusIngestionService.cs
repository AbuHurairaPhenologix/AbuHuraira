using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Health;
using AutoSphere.Contracts.Messages;
using AutoSphere.SharedKernel.Vehicles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutoSphere.Application.Vehicles;

/// <summary>Applies the gateway's retained status message: connectivity and ECU inventory.</summary>
public sealed class VehicleStatusIngestionService(
    IAutoSphereDbContext db,
    VehicleHealthService health,
    IRealtimeNotifier notifier,
    TimeProvider timeProvider,
    ILogger<VehicleStatusIngestionService> logger)
{
    public async Task HandleAsync(VehicleStatusMessage message, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var vehicle = await db.Vehicles.Include(v => v.Ecus).FirstAsync(v => v.Id == vehicleKey, cancellationToken);
        var now = timeProvider.GetUtcNow();

        // A retained "online" message may be replayed on (re)subscription; only trust recent ones.
        var connectivity = message.Connectivity == ConnectivityStatus.Online && now - message.Timestamp > TimeSpan.FromMinutes(2)
            ? ConnectivityStatus.Offline
            : message.Connectivity;

        var changed = vehicle.SetConnectivity(connectivity, connectivity == ConnectivityStatus.Online ? message.Timestamp : now);
        if (connectivity == ConnectivityStatus.Online)
        {
            vehicle.SetGateway(message.GatewayId, message.GatewaySoftwareVersion, message.SimulationMode);
            foreach (var observed in message.Ecus)
            {
                var ecu = vehicle.FindEcu(observed.EcuId);
                if (ecu is null)
                {
                    ecu = vehicle.AddOrGetEcu(observed.EcuId, observed.EcuType, observed.Name, observed.SoftwareVersion);
                    db.Ecus.Add(ecu);
                    logger.LogInformation("ECU {EcuId} ({EcuType}) discovered on {VehicleId}", observed.EcuId, observed.EcuType, vehicle.VehicleId);
                }

                changed |= ecu.ApplyObservation(observed.Status, observed.SoftwareVersion, observed.HardwareVersion, observed.LastSeen,
                    observed.ActiveDtcCount, observed.TimeoutCount, observed.E2EErrorCount);
            }
        }
        else
        {
            foreach (var ecu in vehicle.Ecus)
            {
                ecu.MarkUnreachable();
            }

            logger.LogWarning("Vehicle {VehicleId} reported offline (gateway last will or shutdown)", vehicle.VehicleId);
        }

        await db.SaveChangesAsync(cancellationToken);
        await health.EvaluateAsync(vehicleKey, cancellationToken);
        if (changed)
        {
            var refreshed = await db.Vehicles.AsNoTracking().Include(v => v.Ecus).FirstAsync(v => v.Id == vehicleKey, cancellationToken);
            await notifier.VehicleUpdatedAsync(refreshed.ToDetails(), cancellationToken);
        }
    }
}
