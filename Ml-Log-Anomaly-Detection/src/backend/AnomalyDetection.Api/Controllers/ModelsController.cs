using System.ComponentModel.DataAnnotations;
using AnomalyDetection.Api.Auth;
using AnomalyDetection.Application.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AnomalyDetection.Api.Controllers;

public sealed class RegisterModelRequest
{
    /// <summary>A version label present in the controlled ML registry. File paths are never accepted.</summary>
    [Required]
    [RegularExpression(@"^[a-z0-9][a-z0-9.\-]{1,63}$")]
    public string ModelVersion { get; set; } = string.Empty;
}

public sealed class RetrainModelRequest
{
    [Required]
    public string Algorithm { get; set; } = "ocsvm";

    /// <summary><c>benchmark</c> (synthetic, labelled) or <c>feature-windows</c> (stored normal windows).</summary>
    [Required]
    public string Source { get; set; } = "benchmark";

    public DateTime? TrainingPeriodStartUtc { get; set; }

    public DateTime? TrainingPeriodEndUtc { get; set; }
}

/// <summary>FR-09 model lifecycle. Reads are available to engineers; every change requires Administrator and is audited.</summary>
[Route("api/v1/models")]
[Authorize(Policy = Policies.Engineer)]
public sealed class ModelsController(ModelLifecycleService lifecycle) : ApiControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<ModelDto>> List(CancellationToken ct) => lifecycle.ListAsync(ct);

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var model = await lifecycle.GetAsync(id, ct);
        return model is null ? NotFound() : Ok(model);
    }

    /// <summary>Model versions available in the controlled ML artifact registry.</summary>
    [HttpGet("registry")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Registry(CancellationToken ct) => FromResult(await lifecycle.ListRegistryAsync(ct));

    [HttpPost]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Register([FromBody] RegisterModelRequest request, CancellationToken ct) =>
        FromResult(await lifecycle.RegisterAsync(request.ModelVersion, Actor, ct), m => CreatedAtAction(nameof(Get), new { id = m.ModelId }, m));

    [HttpPost("{id:guid}/activate")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Activate(Guid id, CancellationToken ct) => FromResult(await lifecycle.ActivateAsync(id, Actor, ct));

    [HttpPost("{id:guid}/deactivate")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct) => FromResult(await lifecycle.DeactivateAsync(id, Actor, ct));

    /// <summary>Explicit retraining (never automatic). The resulting versions are registered inactive in the ML registry.</summary>
    [HttpPost("retrain")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> Retrain([FromBody] RetrainModelRequest request, CancellationToken ct) =>
        FromResult(
            await lifecycle.RetrainAsync(new RetrainRequest(request.Algorithm, request.Source, request.TrainingPeriodStartUtc, request.TrainingPeriodEndUtc), Actor, ct),
            job => Accepted(job));

    [HttpGet("training-jobs/{jobId}")]
    [Authorize(Policy = Policies.Administrator)]
    public async Task<IActionResult> TrainingJob([RegularExpression("^[a-f0-9]{8,64}$")] string jobId, CancellationToken ct) =>
        FromResult(await lifecycle.GetTrainingJobAsync(jobId, ct));
}
