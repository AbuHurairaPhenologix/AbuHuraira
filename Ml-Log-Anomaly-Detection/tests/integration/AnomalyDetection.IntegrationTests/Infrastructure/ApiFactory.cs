using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AnomalyDetection.Infrastructure.Ml;
using AnomalyDetection.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AnomalyDetection.IntegrationTests.Infrastructure;

/// <summary>
/// Hosts the real API in-process against real PostgreSQL + OpenSearch containers. Background workers are disabled so
/// tests drive the pipeline deterministically via <c>POST /api/v1/pipeline/run</c> (synthetic timestamps; no waiting).
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string IngestionKey = "it-ingestion-key-0123456789";
    public const string EngineerPassword = "it-engineer-pass";
    public const string AdminPassword = "it-admin-pass";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private string _connectionString = string.Empty;
    private string _openSearchUrl = string.Empty;

    public StubMlServiceHandler Ml { get; } = new();

    public string IndexName { get; } = $"it-events-{Guid.NewGuid():N}";

    public async Task InitializeAsync()
    {
        var (postgres, openSearch) = await SharedContainers.GetAsync();
        var database = $"it_{Guid.NewGuid():N}";
        await using (var conn = new NpgsqlConnection(postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        _connectionString = new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database }.ConnectionString;
        _openSearchUrl = SharedContainers.OpenSearchUrl(openSearch);
    }

    public new Task DisposeAsync() => base.DisposeAsync().AsTask();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", _connectionString);
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "true");
        builder.UseSetting("Pipeline:EnableBackgroundWorkers", "false");
        builder.UseSetting("Pipeline:ScoringRetryBaseDelaySeconds", "1");
        builder.UseSetting("MlService:BaseUrl", "http://ml-stub.internal");
        builder.UseSetting("MlService:ServiceTokenSigningKey", "it-service-token-signing-key-0123456789abcdef");
        builder.UseSetting("MlService:AttemptTimeoutSeconds", "1");
        builder.UseSetting("MlService:TotalTimeoutSeconds", "3");
        builder.UseSetting("MlService:MaxRetryAttempts", "1");
        builder.UseSetting("MlService:CircuitBreakDurationSeconds", "1");
        builder.UseSetting("OpenSearch:Url", _openSearchUrl);
        builder.UseSetting("OpenSearch:IndexName", IndexName);
        builder.UseSetting("Auth:SigningKey", "it-user-token-signing-key-0123456789abcdef");
        builder.UseSetting("Auth:DemoEngineerPassword", EngineerPassword);
        builder.UseSetting("Auth:DemoAdminPassword", AdminPassword);
        builder.UseSetting("Ingestion:ApiKey", IngestionKey);

        builder.ConfigureServices(services =>
        {
            foreach (var name in new[] { MlScoringClient.ResilientClientName, MlScoringClient.AdminClientName, AnomalyDetection.Infrastructure.Health.MlServiceHealthCheck.ProbeClientName })
            {
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Ml);
            }
        });
    }

    public AnomalyDetectionDbContext NewDbContext() => Services.CreateScope().ServiceProvider.GetRequiredService<AnomalyDetectionDbContext>();

    public async Task<HttpClient> ClientAsAsync(string user)
    {
        var client = CreateClient();
        var password = user == "admin" ? AdminPassword : EngineerPassword;
        var response = await client.PostAsJsonAsync("/api/v1/auth/token", new { username = user, password });
        response.EnsureSuccessStatusCode();
        var token = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public HttpClient IngestionClient()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", IngestionKey);
        return client;
    }
}
