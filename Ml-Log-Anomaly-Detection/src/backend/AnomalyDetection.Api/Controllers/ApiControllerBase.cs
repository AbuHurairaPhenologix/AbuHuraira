using AnomalyDetection.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace AnomalyDetection.Api.Controllers;

/// <summary>Thin-controller helpers: map service results to HTTP responses.</summary>
[ApiController]
[Produces("application/json")]
public abstract class ApiControllerBase : ControllerBase
{
    protected string Actor => User.Identity?.Name ?? User.FindFirst("sub")?.Value ?? "unknown";

    protected IActionResult FromResult<T>(OperationResult<T> result, Func<T, IActionResult>? onOk = null) => result.Status switch
    {
        OperationStatus.Ok => onOk is null ? Ok(result.Value) : onOk(result.Value!),
        OperationStatus.NotFound => Problem(statusCode: StatusCodes.Status404NotFound, title: result.Error),
        OperationStatus.Conflict => Problem(statusCode: StatusCodes.Status409Conflict, title: result.Error),
        OperationStatus.Invalid => Problem(statusCode: StatusCodes.Status422UnprocessableEntity, title: result.Error),
        OperationStatus.Unavailable => Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: result.Error),
        _ => Problem(statusCode: StatusCodes.Status500InternalServerError),
    };
}
