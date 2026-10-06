using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AnomalyDetection.IntegrationTests.Infrastructure;

public enum StubBehavior
{
    Normal,
    Timeout,
    ServerError,
}

/// <summary>
/// TEST DOUBLE of the Python ML service's HTTP contract, used as the primary handler of the backend's ML HttpClients.
/// It lets integration tests exercise the real MlScoringClient (serialization, service tokens, resilience pipeline)
/// deterministically. The real Python service is exercised by the Python tests and by tests/e2e.
/// Scoring rule: score = avg_duration_ms / 600, threshold 1.0.
/// </summary>
public sealed class StubMlServiceHandler : HttpMessageHandler
{
    public const string ProductionModel = "ocsvm-ops-v1-stub";
    public const string ReferenceModel = "rf-ops-v1-stub";

    private readonly ConcurrentDictionary<string, bool> _active = new();

    public StubBehavior Behavior { get; set; } = StubBehavior.Normal;

    public ConcurrentQueue<(string Path, string? Scope)> Calls { get; } = new();

    public static readonly Guid ProductionModelId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    public static readonly Guid ReferenceModelId = Guid.Parse("22222222-2222-4222-8222-222222222222");

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath.TrimStart('/');
        var scope = ReadScope(request);
        Calls.Enqueue((path, scope));

        if (path == "health/ready")
        {
            return Json(HttpStatusCode.OK, new { status = "Healthy", registeredModels = 2, activeModels = _active.Keys.ToArray() });
        }

        if (scope is null)
        {
            return Json(HttpStatusCode.Unauthorized, new { error = new { code = "unauthorized" } });
        }

        switch (Behavior)
        {
            case StubBehavior.Timeout:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
            case StubBehavior.ServerError:
                return Json(HttpStatusCode.ServiceUnavailable, new { error = new { code = "unavailable" } });
        }

        if (path == "internal/models" && request.Method == HttpMethod.Get)
        {
            return Json(HttpStatusCode.OK, new { models = new[] { Metadata(ProductionModel), Metadata(ReferenceModel) } });
        }

        if (path.StartsWith("internal/models/", StringComparison.Ordinal))
        {
            var parts = path["internal/models/".Length..].Split('/');
            var version = parts[0];
            if (version is not (ProductionModel or ReferenceModel))
            {
                return Json(HttpStatusCode.NotFound, new { error = new { code = "model_not_registered" } });
            }

            if (parts.Length == 2 && parts[1] == "activate")
            {
                if (scope != "models.admin")
                {
                    return Json(HttpStatusCode.Forbidden, new { });
                }

                _active[version] = true;
            }
            else if (parts.Length == 2 && parts[1] == "deactivate")
            {
                _active.TryRemove(version, out _);
            }

            return Json(HttpStatusCode.OK, Metadata(version));
        }

        if (path == "internal/anomaly/score/batch")
        {
            if (scope != "anomaly.score")
            {
                return Json(HttpStatusCode.Forbidden, new { });
            }

            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            var version = body["modelVersion"]!.GetValue<string>();
            if (!_active.ContainsKey(version))
            {
                return Json(HttpStatusCode.Conflict, new { error = new { code = "model_inactive" } });
            }

            if (body["schemaVersion"]!.GetValue<string>() != "ops-v1")
            {
                return Json(HttpStatusCode.UnprocessableEntity, new { error = new { code = "schema_incompatible" } });
            }

            var results = body["items"]!.AsArray().Select(item =>
            {
                var features = item!["features"]!.AsObject();
                if (features.Count != 8)
                {
                    throw new InvalidOperationException("backend sent a vector that is not ops-v1");
                }

                var avg = features["avg_duration_ms"]!.GetValue<double>();
                var score = avg / 600d;
                return new
                {
                    windowId = item["windowId"]!.GetValue<string>(),
                    score,
                    threshold = 1.0,
                    isAnomaly = score >= 1.0,
                    modelVersion = version,
                    modelId = ProductionModelId,
                    algorithm = "ocsvm",
                    schemaVersion = "ops-v1",
                    reasonSummary = score >= 1.0
                        ? "Elevated average response time compared with the model's training baseline. Indicates where to investigate; not a root-cause determination."
                        : "Feature values are within the model's normal baseline.",
                    reasons = new[] { new { feature = "avg_duration_ms", value = avg, baselineMean = 250.0, baselineStd = 60.0, zScore = (avg - 250) / 60, direction = avg >= 250 ? "above" : "below" } },
                };
            }).ToArray();
            return Json(HttpStatusCode.OK, new { modelVersion = version, schemaVersion = "ops-v1", results });
        }

        return Json(HttpStatusCode.NotFound, new { });
    }

    private static object Metadata(string version) => new
    {
        modelId = version == ProductionModel ? ProductionModelId : ReferenceModelId,
        modelVersion = version,
        algorithm = version == ProductionModel ? "ocsvm" : "random_forest",
        featureSchemaVersion = "ops-v1",
        featureNames = new[] { "request_count", "error_rate", "avg_duration_ms", "p95_duration_ms", "auth_failure_rate", "dependency_failure_count", "retry_count", "endpoint_entropy" },
        trainingPeriodStartUtc = "2022-01-03T00:00:00Z",
        trainingPeriodEndUtc = "2022-01-17T00:00:00Z",
        randomSeed = 20220215,
        libraryVersions = new Dictionary<string, string> { ["scikit-learn"] = "stub" },
        parameters = new Dictionary<string, object> { ["nu"] = 0.04 },
        validationThreshold = 1.0,
        thresholdObjective = "max_f1",
        artifactPath = $"{version}/model.joblib",
        artifactSha256 = "a3f5c0de",
        productionEligible = version == ProductionModel,
        validationMetrics = new { f1 = 0.8 },
        createdAtUtc = "2022-02-01T00:00:00Z",
        artifactExists = true,
        activeInService = false,
        recommended = version == ProductionModel,
    };

    private static string? ReadScope(HttpRequestMessage request)
    {
        var token = request.Headers.Authorization?.Parameter;
        if (token is null)
        {
            return null;
        }

        var payload = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
        payload = payload.PadRight(payload.Length + ((4 - (payload.Length % 4)) % 4), '=');
        using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
        return doc.RootElement.GetProperty("aud").GetString() == "ml-scoring" ? doc.RootElement.GetProperty("scope").GetString() : null;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, object body) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };
}
