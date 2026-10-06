using AnomalyDetection.Api.Auth;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Queries;
using AnomalyDetection.Domain.Enums;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnomalyDetection.Api.Controllers;

/// <summary>Feature windows (FR-03/FR-04) and their scores.</summary>
[Route("api/v1/windows")]
[Authorize(Policy = Policies.Engineer)]
public sealed class WindowsController(WindowAndEventQueryService queries) : ApiControllerBase
{
    [HttpGet]
    public Task<PagedResult<FeatureWindowDto>> List(
        [FromQuery] string? service,
        [FromQuery] string? environment,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] ScoringStatus? scoringStatus,
        [FromQuery] int? page,
        [FromQuery] int? pageSize,
        CancellationToken ct) =>
        queries.ListWindowsAsync(service, environment, from, to, scoringStatus, page, pageSize, ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var w = await queries.GetWindowAsync(id, ct);
        return w is null ? NotFound() : Ok(w);
    }
}
