using AutoSphere.Api.Security;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Alerts;
using AutoSphere.Application.Diagnostics;
using AutoSphere.Application.Ota;
using AutoSphere.Application.Simulation;
using AutoSphere.Application.Telemetry;
using AutoSphere.Application.Vehicles;
using AutoSphere.Contracts.Mqtt;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AutoSphere.Api.Hubs;

/// <summary>Strongly typed client contract of the dashboard.</summary>
public interface IVehicleClient
{
    Task Telemetry(VehicleTelemetryDto telemetry);

    Task VehicleUpdated(VehicleDetailsDto vehicle);

    Task DtcsChanged(string vehicleId, IReadOnlyList<DtcRecordDto> dtcs);

    Task Alert(AlertDto alert);

    Task OtaDeployment(OtaDeploymentDto deployment);

    Task DiagnosticCompleted(DiagnosticSessionDto session);

    Task FaultInjection(FaultInjectionDto fault);
}

/// <summary>
/// Real-time channel to dashboards. Clients join the group of the vehicle they display; every client is
/// in the fleet group for vehicle list updates. Live data is pushed — dashboards never poll.
/// </summary>
[Authorize(Policy = Policies.ViewVehicles)]
public sealed class VehicleHub : Hub<IVehicleClient>
{
    public const string Path = "/hubs/vehicles";
    public const string FleetGroup = "fleet";

    public static string VehicleGroup(string vehicleId) => $"vehicle:{vehicleId.ToUpperInvariant()}";

    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, FleetGroup);
        await base.OnConnectedAsync();
    }

    public async Task SubscribeVehicle(string vehicleId)
    {
        if (!MqttTopics.IsValidVehicleId(vehicleId))
        {
            throw new HubException("Invalid vehicle id.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, VehicleGroup(vehicleId));
    }

    public Task UnsubscribeVehicle(string vehicleId) => Groups.RemoveFromGroupAsync(Context.ConnectionId, VehicleGroup(vehicleId));
}

/// <summary>Implements the application's real-time port with SignalR.</summary>
public sealed class SignalRRealtimeNotifier(IHubContext<VehicleHub, IVehicleClient> hub) : IRealtimeNotifier
{
    public Task TelemetryAsync(VehicleTelemetryDto telemetry, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.VehicleGroup(telemetry.VehicleId)).Telemetry(telemetry);

    public Task VehicleUpdatedAsync(VehicleDetailsDto vehicle, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.FleetGroup).VehicleUpdated(vehicle);

    public Task DtcsChangedAsync(string vehicleId, IReadOnlyList<DtcRecordDto> activeAndStored, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.VehicleGroup(vehicleId)).DtcsChanged(vehicleId, activeAndStored);

    public Task AlertAsync(string vehicleId, AlertDto alert, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.FleetGroup).Alert(alert);

    public Task OtaDeploymentAsync(string vehicleId, OtaDeploymentDto deployment, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.FleetGroup).OtaDeployment(deployment);

    public Task DiagnosticCompletedAsync(string vehicleId, DiagnosticSessionDto session, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.VehicleGroup(vehicleId)).DiagnosticCompleted(session);

    public Task FaultInjectionAsync(string vehicleId, FaultInjectionDto fault, CancellationToken cancellationToken) =>
        hub.Clients.Group(VehicleHub.VehicleGroup(vehicleId)).FaultInjection(fault);
}
