using Microsoft.AspNetCore.Mvc;
using ThermoTwin.Application.Twin;
using ThermoTwin.Domain.Enums;
using ThermoTwin.SimulationWorker;

namespace ThermoTwin.Api.Controllers;

public sealed record SetModeRequest(CoolingMode Mode);

/// <summary>The live digital-twin session.</summary>
[ApiController]
[Route("api/twin")]
public sealed class TwinController : ControllerBase
{
    private readonly SimulationCoordinator _coordinator;

    public TwinController(SimulationCoordinator coordinator) => _coordinator = coordinator;

    /// <summary>Latest twin frame: statistics, sensors, estimation, forecast, cooling and (optionally) heatmap fields.</summary>
    [HttpGet("state")]
    [ProducesResponseType<TwinFrame>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult<TwinFrame> State([FromQuery] bool includeFields = true)
    {
        var frame = _coordinator.LatestFrame;
        if (frame is null)
        {
            return NotFound();
        }

        return Ok(includeFields ? frame : frame with { Fields = null });
    }

    /// <summary>Full per-step history of the live session.</summary>
    [HttpGet("history")]
    public ActionResult<IReadOnlyList<TwinHistoryPoint>> History() => Ok(_coordinator.History);

    /// <summary>Pauses the live simulation.</summary>
    [HttpPost("pause")]
    public IActionResult Pause()
    {
        _coordinator.Pause();
        return Accepted();
    }

    /// <summary>Resumes a paused simulation.</summary>
    [HttpPost("resume")]
    public IActionResult Resume()
    {
        _coordinator.Resume();
        return Accepted();
    }

    /// <summary>Stops the live simulation and stores its summary.</summary>
    [HttpPost("stop")]
    public IActionResult Stop()
    {
        _coordinator.Stop();
        return Accepted();
    }

    /// <summary>Switches between Fixed, Advisory and Autonomous (MPC) cooling.</summary>
    [HttpPut("mode")]
    public IActionResult SetMode(SetModeRequest request)
    {
        _coordinator.SetMode(request.Mode);
        return NoContent();
    }
}
