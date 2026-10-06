using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Messages;
using AutoSphere.Domain.Alerts;
using AutoSphere.Domain.Common;
using AutoSphere.SharedKernel.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AutoSphere.Application.Alerts;

public sealed record AlertDto(
    Guid Id,
    string VehicleId,
    string AlertKey,
    AlertSource Source,
    DtcSeverity Severity,
    string Category,
    string Message,
    string? EcuId,
    string? SignalPath,
    double? Value,
    double? Threshold,
    DateTimeOffset RaisedAt,
    DateTimeOffset? ClearedAt,
    DateTimeOffset? AcknowledgedAt,
    string? AcknowledgedBy,
    bool IsActive);

/// <summary>Alert ingestion (edge alerts), backend-raised alerts and acknowledgement.</summary>
public sealed class AlertService(
    IAutoSphereDbContext db,
    IRealtimeNotifier notifier,
    ICurrentUser currentUser,
    TimeProvider timeProvider,
    ILogger<AlertService> logger)
{
    /// <summary>Category of event alerts that are closed by acknowledgement.</summary>
    public const string EventCategory = "Ota";

    public async Task HandleAsync(AlertMessage message, Guid vehicleKey, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.State == AlertState.Raised)
        {
            await RaiseAsync(message.VehicleId, vehicleKey, message.AlertKey, AlertSource.Vehicle, message.Severity, message.Category.ToString(),
                message.Message, message.Timestamp, message.EcuId, message.SignalPath, message.Value, message.Threshold, cancellationToken);
        }
        else
        {
            await ClearAsync(message.VehicleId, vehicleKey, message.AlertKey, message.Timestamp, cancellationToken);
        }
    }

    /// <summary>Raises (or updates) the active alert for <paramref name="alertKey"/>.</summary>
    public async Task RaiseAsync(string vehicleId, Guid vehicleKey, string alertKey, AlertSource source, DtcSeverity severity, string category,
        string message, DateTimeOffset at, string? ecuId = null, string? signalPath = null, double? value = null, double? threshold = null,
        CancellationToken cancellationToken = default)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.VehicleKey == vehicleKey && a.AlertKey == alertKey && a.ClearedAt == null, cancellationToken);
        if (alert is null)
        {
            alert = Alert.Raise(vehicleKey, alertKey, source, severity, category, message, at, ecuId, signalPath, value, threshold);
            db.Alerts.Add(alert);
            logger.Log(severity == DtcSeverity.Critical ? LogLevel.Warning : LogLevel.Information,
                "Alert {AlertKey} raised for {VehicleId} ({Severity}): {Message}", alertKey, vehicleId, severity, message);
        }
        else
        {
            alert.Update(severity, message, value, threshold);
        }

        await db.SaveChangesAsync(cancellationToken);
        await notifier.AlertAsync(vehicleId, ToDto(alert, vehicleId), cancellationToken);
    }

    public async Task ClearAsync(string vehicleId, Guid vehicleKey, string alertKey, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.VehicleKey == vehicleKey && a.AlertKey == alertKey && a.ClearedAt == null, cancellationToken);
        if (alert is null)
        {
            return;
        }

        alert.Clear(at);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Alert {AlertKey} cleared for {VehicleId}", alertKey, vehicleId);
        await notifier.AlertAsync(vehicleId, ToDto(alert, vehicleId), cancellationToken);
    }

    public async Task<IReadOnlyList<AlertDto>> ListAsync(string vehicleId, bool activeOnly, int take, CancellationToken cancellationToken)
    {
        var normalizedId = vehicleId.ToUpperInvariant();
        var vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(v => v.VehicleId == normalizedId, cancellationToken)
                      ?? throw NotFoundException.For("Vehicle", vehicleId);
        var query = db.Alerts.AsNoTracking().Where(a => a.VehicleKey == vehicle.Id);
        if (activeOnly)
        {
            query = query.Where(a => a.ClearedAt == null);
        }

        var alerts = await query.OrderByDescending(a => a.RaisedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
        return alerts.Select(a => ToDto(a, vehicle.VehicleId)).ToList();
    }

    public async Task<AlertDto> AcknowledgeAsync(Guid alertId, CancellationToken cancellationToken)
    {
        var alert = await db.Alerts.FirstOrDefaultAsync(a => a.Id == alertId, cancellationToken) ?? throw NotFoundException.For("Alert", alertId);
        var vehicleId = await db.Vehicles.Where(v => v.Id == alert.VehicleKey).Select(v => v.VehicleId).FirstAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        alert.Acknowledge(currentUser.UserName, now);
        if (alert.Source == AlertSource.Backend && alert.Category == EventCategory)
        {
            // Event alerts (e.g. a failed OTA deployment) have no ongoing condition: acknowledging closes them.
            alert.Clear(now);
        }

        await db.SaveChangesAsync(cancellationToken);
        var dto = ToDto(alert, vehicleId);
        await notifier.AlertAsync(vehicleId, dto, cancellationToken);
        return dto;
    }

    public static AlertDto ToDto(Alert alert, string vehicleId) => new(alert.Id, vehicleId, alert.AlertKey, alert.Source, alert.Severity, alert.Category,
        alert.Message, alert.EcuId, alert.SignalPath, alert.Value, alert.Threshold, alert.RaisedAt, alert.ClearedAt, alert.AcknowledgedAt,
        alert.AcknowledgedBy, alert.IsActive);
}
