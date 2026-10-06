using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnomalyDetection.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace AnomalyDetection.Infrastructure.Ml;

/// <summary>
/// HTTP client for the internal ML service. Transport failures, timeouts and an open circuit are mapped to
/// <see cref="MlCallStatus.Unavailable"/> so callers can defer work — this client never throws on ML unavailability.
/// </summary>
public sealed class MlScoringClient(
    IHttpClientFactory httpClientFactory,
    ServiceTokenIssuer tokens,
    ILogger<MlScoringClient> logger) : IMlScoringClient
{
    public const string ResilientClientName = "ml-service";
    public const string AdminClientName = "ml-service-admin";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<MlCallResult<IReadOnlyList<ScoreResult>>> ScoreBatchAsync(string modelVersion, string schemaVersion, IReadOnlyList<ScoreItem> items, CancellationToken cancellationToken)
    {
        var body = new
        {
            modelVersion,
            schemaVersion,
            items = items.Select(i => new
            {
                windowId = i.WindowId,
                service = i.Service,
                environment = i.Environment,
                windowStartUtc = i.WindowStartUtc,
                windowEndUtc = i.WindowEndUtc,
                features = i.Features,
            }),
        };

        var result = await SendAsync<BatchScoreResponse>(ResilientClientName, HttpMethod.Post, "internal/anomaly/score/batch", MlScopes.Score, body, cancellationToken);
        return result.IsSuccess
            ? MlCallResult<IReadOnlyList<ScoreResult>>.Ok(result.Value!.Results)
            : MlCallResult<IReadOnlyList<ScoreResult>>.Fail(result.Status, result.Error!);
    }

    public async Task<MlCallResult<IReadOnlyList<RegistryModelMetadata>>> ListRegistryAsync(CancellationToken cancellationToken)
    {
        var result = await SendAsync<RegistryListResponse>(ResilientClientName, HttpMethod.Get, "internal/models", MlScopes.ModelsRead, null, cancellationToken);
        return result.IsSuccess
            ? MlCallResult<IReadOnlyList<RegistryModelMetadata>>.Ok(result.Value!.Models)
            : MlCallResult<IReadOnlyList<RegistryModelMetadata>>.Fail(result.Status, result.Error!);
    }

    public Task<MlCallResult<RegistryModelMetadata>> GetModelAsync(string modelVersion, CancellationToken cancellationToken) =>
        SendAsync<RegistryModelMetadata>(ResilientClientName, HttpMethod.Get, $"internal/models/{Uri.EscapeDataString(modelVersion)}", MlScopes.ModelsRead, null, cancellationToken);

    public Task<MlCallResult<RegistryModelMetadata>> ActivateModelAsync(string modelVersion, CancellationToken cancellationToken) =>
        SendAsync<RegistryModelMetadata>(ResilientClientName, HttpMethod.Post, $"internal/models/{Uri.EscapeDataString(modelVersion)}/activate", MlScopes.ModelsAdmin, new { }, cancellationToken);

    public async Task<MlCallResult<bool>> DeactivateModelAsync(string modelVersion, CancellationToken cancellationToken)
    {
        var result = await SendAsync<JsonElement>(ResilientClientName, HttpMethod.Post, $"internal/models/{Uri.EscapeDataString(modelVersion)}/deactivate", MlScopes.ModelsAdmin, new { }, cancellationToken);
        return result.IsSuccess ? MlCallResult<bool>.Ok(true) : MlCallResult<bool>.Fail(result.Status, result.Error!);
    }

    public Task<MlCallResult<TrainingJobStatus>> StartTrainingAsync(TrainingJobRequest request, CancellationToken cancellationToken) =>
        SendAsync<TrainingJobStatus>(AdminClientName, HttpMethod.Post, "internal/training/jobs", MlScopes.TrainingRun, request, cancellationToken);

    public Task<MlCallResult<TrainingJobStatus>> GetTrainingJobAsync(string jobId, CancellationToken cancellationToken) =>
        SendAsync<TrainingJobStatus>(ResilientClientName, HttpMethod.Get, $"internal/training/jobs/{Uri.EscapeDataString(jobId)}", MlScopes.TrainingRun, null, cancellationToken);

    private async Task<MlCallResult<T>> SendAsync<T>(string clientName, HttpMethod method, string path, string scope, object? body, CancellationToken cancellationToken)
    {
        try
        {
            var client = httpClientFactory.CreateClient(clientName);
            using var request = new HttpRequestMessage(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokens.Issue(scope));
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
                return value is null
                    ? MlCallResult<T>.Fail(MlCallStatus.Rejected, "Empty response from ML service.")
                    : MlCallResult<T>.Ok(value);
            }

            var error = await ReadErrorAsync(response, cancellationToken);
            var status = response.StatusCode switch
            {
                HttpStatusCode.NotFound => MlCallStatus.ModelNotFound,
                HttpStatusCode.Conflict => MlCallStatus.ModelNotActive,
                HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => MlCallStatus.Rejected,
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => MlCallStatus.Unauthorized,
                _ => MlCallStatus.Unavailable,
            };

            logger.LogWarning(
                "ML service call {Method} {Path} failed with HTTP {StatusCode} ({MlStatus}): {Error}",
                method,
                path,
                (int)response.StatusCode,
                status,
                error);
            return MlCallResult<T>.Fail(status, $"HTTP {(int)response.StatusCode}: {error}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutRejectedException or BrokenCircuitException or TaskCanceledException or JsonException)
        {
            // Structured failure logging; the caller defers the work (TC-04).
            logger.LogWarning("ML service call {Method} {Path} unavailable: {ExceptionType}: {Message}", method, path, ex.GetType().Name, ex.Message);
            return MlCallResult<T>.Fail(MlCallStatus.Unavailable, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            return text.Length > 500 ? text[..500] : text;
        }
        catch (Exception)
        {
            return response.ReasonPhrase ?? "unknown error";
        }
    }

    private sealed record BatchScoreResponse(string ModelVersion, string SchemaVersion, IReadOnlyList<ScoreResult> Results);

    private sealed record RegistryListResponse(IReadOnlyList<RegistryModelMetadata> Models);
}
