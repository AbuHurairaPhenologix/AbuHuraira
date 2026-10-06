namespace AnomalyDetection.IntegrationTests.Infrastructure;

/// <summary>Builds canonical events with synthetic (historical) timestamps so windows close immediately.</summary>
public static class EventFactory
{
    public static object Request(string service, DateTime ts, double durationMs, string endpoint = "/orders", int status = 200, string? correlationId = null, string? id = null, object? attributes = null) => new
    {
        eventId = id ?? Guid.NewGuid().ToString("N"),
        eventTimestamp = ts.ToString("O"),
        serviceName = service,
        environment = "production",
        eventType = "http_request",
        endpointGroup = endpoint,
        statusCode = status,
        durationMs,
        errorFlag = status >= 500,
        authenticationResult = status == 401 ? "failure" : "none",
        retryCount = 0,
        correlationId = correlationId ?? $"corr-{Guid.NewGuid():N}",
        attributes,
    };

    public static object Dependency(string service, DateTime ts, bool failed, int retries, string? correlationId = null) => new
    {
        eventId = Guid.NewGuid().ToString("N"),
        eventTimestamp = ts.ToString("O"),
        serviceName = service,
        environment = "production",
        eventType = "dependency_call",
        endpointGroup = "/dependency/inventory-db",
        statusCode = failed ? 503 : 200,
        durationMs = 40.0,
        errorFlag = failed,
        dependencyName = "inventory-db",
        retryCount = retries,
        correlationId = correlationId ?? $"corr-{Guid.NewGuid():N}",
    };

    /// <summary>A 5-minute window of request events with mean duration <paramref name="avgMs"/>.</summary>
    public static List<object> Window(string service, DateTime windowStart, int requests, double avgMs, string correlationPrefix)
    {
        var endpoints = new[] { "/products", "/orders/{id}", "/login", "/dependency", "/job" };
        return Enumerable.Range(0, requests)
            .Select(i => Request(
                service,
                windowStart.AddSeconds(i * (290.0 / requests)),
                avgMs * (0.5 + ((i % 11) / 10.0)),
                endpoints[i % endpoints.Length],
                status: i % 25 == 0 ? 500 : 200,
                correlationId: $"{correlationPrefix}-{i:D3}"))
            .ToList();
    }
}
