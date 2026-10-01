using AnomalyDetection.Api.Demo;
using AnomalyDetection.Api.Middleware;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Api.Controllers;

/// <summary>
/// The monitored demo workload (a small "shop" API). These endpoints are the ordinary end-user request path: they never
/// call the ML service, so anomaly scoring failures cannot affect them (TC-04).
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("demo")]
[ApiExplorerSettings(GroupName = "demo")]
public sealed class DemoController(DemoWorkload workload) : ControllerBase
{
    private static readonly string[] Products = ["keyboard", "monitor", "headset", "dock", "webcam"];

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts(CancellationToken ct)
    {
        await Task.Delay(workload.SampleLatency(), ct);
        return workload.ShouldFail()
            ? Problem(statusCode: 500, title: "Simulated catalog failure")
            : Ok(Products.Select((p, i) => new { id = i + 1, name = p }));
    }

    [HttpGet("orders/{id:int}")]
    public async Task<IActionResult> GetOrder(int id, CancellationToken ct)
    {
        await Task.Delay(workload.SampleLatency(1.1), ct);
        return workload.ShouldFail()
            ? Problem(statusCode: 500, title: "Simulated order lookup failure")
            : Ok(new { id, status = "shipped" });
    }

    public sealed record LoginRequest(string? Username, string? Password);

    /// <summary>Simulated sign-in. The password is never logged or stored; only the outcome becomes telemetry.</summary>
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        await Task.Delay(workload.SampleLatency(0.8), ct);
        var correlationId = CorrelationIdMiddleware.Get(HttpContext);
        var failed = workload.LoginFails(request.Password);
        HttpContext.Items[OperationalEventMiddleware.AuthResultItemKey] = failed ? "failure" : "success";
        workload.EmitAuthentication(!failed, correlationId);
        return failed ? Unauthorized(new { message = "Invalid credentials" }) : Ok(new { message = "Signed in" });
    }

    [HttpGet("dependency")]
    public async Task<IActionResult> CallDependency(CancellationToken ct)
    {
        var (ok, retries) = await workload.CallDependencyAsync("inventory-db", CorrelationIdMiddleware.Get(HttpContext), ct);
        return ok ? Ok(new { inventory = "available", retries }) : Problem(statusCode: 503, title: "Inventory dependency unavailable");
    }

    [HttpPost("job")]
    public async Task<IActionResult> RunJob(CancellationToken ct)
    {
        var retries = await workload.RunJobAsync(CorrelationIdMiddleware.Get(HttpContext), ct);
        return Ok(new { job = "reconcile-inventory", retries });
    }
}

/// <summary>DEV-ONLY scenario switchboard. Anomaly scenarios activate only when intentionally triggered here.</summary>
[ApiController]
[AllowAnonymous]
[Route("demo/scenarios")]
[ApiExplorerSettings(GroupName = "demo")]
public sealed class DemoScenariosController(DemoScenarioState state, IOptions<DemoOptions> options) : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { enabled = options.Value.EnableScenarios, available = DemoScenarios.All, active = state.Snapshot() });

    [HttpPost("{name}")]
    public IActionResult Activate(string name, [FromQuery] int durationMinutes = 10)
    {
        if (!options.Value.EnableScenarios)
        {
            return NotFound();
        }

        if (!DemoScenarios.All.Contains(name))
        {
            return BadRequest(new { error = $"Unknown scenario. Available: {string.Join(", ", DemoScenarios.All)}" });
        }

        return Ok(state.Activate(name, TimeSpan.FromMinutes(Math.Clamp(durationMinutes, 1, 60))));
    }

    [HttpDelete("{name}")]
    public IActionResult Deactivate(string name) => !options.Value.EnableScenarios ? NotFound() : state.Deactivate(name) ? NoContent() : NotFound();

    [HttpDelete]
    public IActionResult Clear()
    {
        if (!options.Value.EnableScenarios)
        {
            return NotFound();
        }

        state.Clear();
        return NoContent();
    }
}
