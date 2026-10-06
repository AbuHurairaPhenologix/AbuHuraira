using Microsoft.AspNetCore.Mvc;
using ThermoTwin.Application.Scenarios;

namespace ThermoTwin.Api.Controllers;

/// <summary>Built-in scenario catalogue.</summary>
[ApiController]
[Route("api/scenarios")]
public sealed class ScenariosController : ControllerBase
{
    /// <summary>Lists the built-in scenarios (the first is the flagship demo).</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<ScenarioDefinition>> List() => Ok(ScenarioCatalog.All);

    /// <summary>Returns one scenario definition by key.</summary>
    [HttpGet("{key}")]
    public ActionResult<ScenarioDefinition> Get(string key) =>
        ScenarioCatalog.Find(key) is { } scenario ? Ok(scenario) : NotFound();
}
