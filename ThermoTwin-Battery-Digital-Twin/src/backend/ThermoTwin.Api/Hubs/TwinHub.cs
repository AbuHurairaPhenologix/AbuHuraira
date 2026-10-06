using Microsoft.AspNetCore.SignalR;
using ThermoTwin.Application.Abstractions;
using ThermoTwin.Application.Twin;
using ThermoTwin.SimulationWorker;

namespace ThermoTwin.Api.Hubs;

/// <summary>
/// Real-time channel. Server → client messages:
/// <c>twinFrame</c> (live digital-twin state) and <c>experimentCompleted</c>.
/// </summary>
public sealed class TwinHub : Hub
{
    private readonly SimulationCoordinator _coordinator;

    public TwinHub(SimulationCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>Lets a newly connected client fetch the most recent frame immediately.</summary>
    public TwinFrame? GetLatestFrame() => _coordinator.LatestFrame;
}

public sealed class SignalRTwinNotifier : ITwinNotifier
{
    private readonly IHubContext<TwinHub> _hub;

    public SignalRTwinNotifier(IHubContext<TwinHub> hub) => _hub = hub;

    public Task PublishFrameAsync(TwinFrame frame, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("twinFrame", frame, cancellationToken);

    public Task PublishExperimentAsync(ExperimentSummaryDto experiment, CancellationToken cancellationToken) =>
        _hub.Clients.All.SendAsync("experimentCompleted", experiment, cancellationToken);
}
