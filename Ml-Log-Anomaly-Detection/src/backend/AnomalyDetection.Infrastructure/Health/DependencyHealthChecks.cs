using System.Text.Json.Nodes;
using AnomalyDetection.Infrastructure.Search;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AnomalyDetection.Infrastructure.Health;

/// <summary>OpenSearch health: unavailability degrades investigation search but not ingestion or scoring.</summary>
public sealed class OpenSearchHealthCheck(IHttpClientFactory factory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = factory.CreateClient(OpenSearchEventSearchService.ClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync("_cluster/health", cts.Token);
            if (!response.IsSuccessStatusCode)
            {
                return HealthCheckResult.Degraded($"OpenSearch returned HTTP {(int)response.StatusCode}");
            }

            var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cts.Token));
            var status = json?["status"]?.GetValue<string>() ?? "unknown";
            var data = new Dictionary<string, object> { ["clusterStatus"] = status };
            return status == "red"
                ? HealthCheckResult.Degraded("OpenSearch cluster status is red", data: data)
                : HealthCheckResult.Healthy($"OpenSearch cluster status {status}", data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("OpenSearch unreachable; investigation falls back to PostgreSQL", ex);
        }
    }
}

/// <summary>ML service health: unavailability delays scoring only (ordinary processing continues).</summary>
public sealed class MlServiceHealthCheck(IHttpClientFactory factory) : IHealthCheck
{
    public const string ProbeClientName = "ml-service-probe";

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var client = factory.CreateClient(ProbeClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await client.GetAsync("health/ready", cts.Token);
            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var json = JsonNode.Parse(body);
            var data = new Dictionary<string, object>
            {
                ["status"] = json?["status"]?.GetValue<string>() ?? "unknown",
                ["registeredModels"] = json?["registeredModels"]?.GetValue<int>() ?? 0,
                ["activeModels"] = json?["activeModels"]?.ToJsonString() ?? "[]",
            };
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy("ML scoring service ready", data)
                : HealthCheckResult.Degraded($"ML scoring service not ready (HTTP {(int)response.StatusCode}); scoring is deferred", data: data);
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("ML scoring service unreachable; scoring is deferred and retried", ex);
        }
    }
}
