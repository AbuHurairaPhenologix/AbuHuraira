using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Search;
using AnomalyDetection.Domain.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Infrastructure.Search;

public sealed class OpenSearchOptions
{
    public const string SectionName = "OpenSearch";

    public string Url { get; set; } = "http://localhost:9200";

    public string IndexName { get; set; } = "ops-events-v1";

    public string? Username { get; set; }

    public string? Password { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>OpenSearch event index (report §4.9). Queries are always built by <see cref="OpenSearchQueryBuilder"/>.</summary>
public sealed class OpenSearchEventSearchService(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenSearchOptions> options,
    ILogger<OpenSearchEventSearchService> logger) : IEventSearchService
{
    public const string ClientName = "opensearch";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private volatile bool _indexEnsured;

    private string Index => options.Value.IndexName;

    public async Task EnsureIndexAsync(CancellationToken cancellationToken)
    {
        if (_indexEnsured)
        {
            return;
        }

        var client = httpClientFactory.CreateClient(ClientName);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, Index), cancellationToken);
        if (head.StatusCode == HttpStatusCode.NotFound)
        {
            var content = new StringContent(OpenSearchQueryBuilder.IndexDefinition().ToJsonString(), Encoding.UTF8, "application/json");
            using var create = await client.PutAsync(Index, content, cancellationToken);
            if (!create.IsSuccessStatusCode && create.StatusCode != HttpStatusCode.BadRequest)
            {
                // 400 = resource_already_exists (created concurrently); anything else is a real failure.
                throw new HttpRequestException($"Failed to create index {Index}: HTTP {(int)create.StatusCode}");
            }

            logger.LogInformation("Created OpenSearch index {Index}", Index);
        }
        else
        {
            head.EnsureSuccessStatusCode();
        }

        _indexEnsured = true;
    }

    public async Task<IReadOnlyCollection<Guid>> IndexAsync(IReadOnlyList<OperationalEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return [];
        }

        await EnsureIndexAsync(cancellationToken);
        var body = new StringBuilder();
        foreach (var e in events)
        {
            body.Append(JsonSerializer.Serialize(new { index = new { _index = Index, _id = e.Id.ToString() } })).Append('\n');
            body.Append(JsonSerializer.Serialize(EventInvestigationService.ToDocument(e), Json)).Append('\n');
        }

        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsync("_bulk", new StringContent(body.ToString(), Encoding.UTF8, "application/x-ndjson"), cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [];

        var indexed = new HashSet<Guid>();
        foreach (var item in result["items"]?.AsArray() ?? [])
        {
            var op = item?["index"];
            var status = op?["status"]?.GetValue<int>() ?? 500;
            if (status is >= 200 and < 300 && Guid.TryParse(op?["_id"]?.GetValue<string>(), out var id))
            {
                indexed.Add(id);
            }
        }

        return indexed;
    }

    public async Task<EventSearchResult> SearchAsync(EventSearchCriteria criteria, CancellationToken cancellationToken)
    {
        await EnsureIndexAsync(cancellationToken);
        var query = OpenSearchQueryBuilder.Build(criteria);
        var client = httpClientFactory.CreateClient(ClientName);
        using var response = await client.PostAsync(
            $"{Index}/_search",
            new StringContent(query.ToJsonString(), Encoding.UTF8, "application/json"),
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken) ?? [];
        var hits = json["hits"];
        var total = hits?["total"]?["value"]?.GetValue<long>() ?? 0;
        var items = new List<EventDocument>();
        foreach (var hit in hits?["hits"]?.AsArray() ?? [])
        {
            var doc = hit?["_source"]?.Deserialize<EventDocument>(Json);
            if (doc is not null)
            {
                items.Add(doc);
            }
        }

        return new EventSearchResult(items, total, criteria.Page, criteria.PageSize, EventInvestigationService.SourceOpenSearch);
    }
}
