using System.Text.Json.Nodes;
using AnomalyDetection.Application.Abstractions;

namespace AnomalyDetection.Infrastructure.Search;

/// <summary>
/// Builds the only query shape the backend ever sends to OpenSearch: a <c>bool.filter</c> over whitelisted fields
/// (service, environment, time range, correlation ID, event type) with bounded paging. Values are passed as JSON
/// values (never concatenated into query strings), so client input cannot inject Query DSL.
/// </summary>
public static class OpenSearchQueryBuilder
{
    /// <summary>OpenSearch's default <c>index.max_result_window</c>.</summary>
    public const int MaxResultWindow = 10_000;

    public static JsonObject Build(EventSearchCriteria c)
    {
        var filters = new JsonArray();
        void Term(string field, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                filters.Add(new JsonObject { ["term"] = new JsonObject { [field] = value } });
            }
        }

        Term("serviceName", c.Service);
        Term("environment", c.Environment);
        Term("correlationId", c.CorrelationId);
        Term("eventType", c.EventType);

        if (c.FromUtc is not null || c.ToUtc is not null)
        {
            var range = new JsonObject();
            if (c.FromUtc is { } from)
            {
                range["gte"] = from.ToString("O");
            }

            if (c.ToUtc is { } to)
            {
                range["lt"] = to.ToString("O");
            }

            filters.Add(new JsonObject { ["range"] = new JsonObject { ["eventTimestampUtc"] = range } });
        }

        var pageSize = Math.Clamp(c.PageSize, 1, 200);
        var from0 = Math.Max(0, (c.Page - 1) * pageSize);
        if (from0 + pageSize > MaxResultWindow)
        {
            from0 = Math.Max(0, MaxResultWindow - pageSize);
        }

        return new JsonObject
        {
            ["from"] = from0,
            ["size"] = pageSize,
            ["track_total_hits"] = true,
            ["sort"] = new JsonArray(
                new JsonObject { ["eventTimestampUtc"] = new JsonObject { ["order"] = "asc" } },
                new JsonObject { ["eventId"] = new JsonObject { ["order"] = "asc" } }),
            ["query"] = new JsonObject { ["bool"] = new JsonObject { ["filter"] = filters } },
        };
    }

    /// <summary>Explicit index mapping: keyword fields for exact filters, date for time ranges.</summary>
    public static JsonObject IndexDefinition() => new()
    {
        ["settings"] = new JsonObject { ["number_of_shards"] = 1, ["number_of_replicas"] = 0 },
        ["mappings"] = new JsonObject
        {
            ["dynamic"] = "strict",
            ["properties"] = new JsonObject
            {
                ["id"] = Keyword(),
                ["eventId"] = Keyword(),
                ["eventTimestampUtc"] = new JsonObject { ["type"] = "date" },
                ["serviceName"] = Keyword(),
                ["environment"] = Keyword(),
                ["eventType"] = Keyword(),
                ["endpointGroup"] = Keyword(),
                ["statusCode"] = new JsonObject { ["type"] = "integer" },
                ["durationMs"] = new JsonObject { ["type"] = "double" },
                ["errorFlag"] = new JsonObject { ["type"] = "boolean" },
                ["authenticationResult"] = Keyword(),
                ["dependencyName"] = Keyword(),
                ["retryCount"] = new JsonObject { ["type"] = "integer" },
                ["correlationId"] = Keyword(),
                ["isLate"] = new JsonObject { ["type"] = "boolean" },
                ["featureWindowId"] = Keyword(),
            },
        },
    };

    private static JsonObject Keyword() => new() { ["type"] = "keyword" };
}
