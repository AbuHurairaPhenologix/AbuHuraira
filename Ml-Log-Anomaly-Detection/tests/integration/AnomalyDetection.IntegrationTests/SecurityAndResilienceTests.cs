using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.IntegrationTests;

public sealed class SecurityTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Token_endpoint_issues_role_tokens_and_rejects_bad_credentials()
    {
        var client = factory.CreateClient();
        var bad = await client.PostAsJsonAsync("/api/v1/auth/token", new { username = "admin", password = "wrong" });
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/v1/auth/token", new { username = "root", password = ApiFactory.AdminPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);

        var admin = await factory.ClientAsAsync("admin");
        var me = await admin.GetFromJsonAsync<JsonElement>("/api/v1/auth/me");
        Assert.Equal("admin", me.GetProperty("username").GetString());
        Assert.Contains("Administrator", me.GetProperty("roles").EnumerateArray().Select(r => r.GetString()));
    }

    [Fact]
    public async Task Engineer_endpoints_require_authentication()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/api/v1/anomalies")).StatusCode);
        var engineer = await factory.ClientAsAsync("engineer");
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/v1/anomalies")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await engineer.GetAsync("/api/v1/models")).StatusCode);
    }

    [Fact]
    public async Task TC07_unauthorized_model_activation_is_denied_and_audited()
    {
        var engineer = await factory.ClientAsAsync("engineer");
        var anonymous = factory.CreateClient();
        var target = $"/api/v1/models/{StubMlServiceHandler.ProductionModelId}/activate";

        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsync(target, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync(target, null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsJsonAsync("/api/v1/models", new { modelVersion = "ocsvm-ops-v1-stub" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.PostAsJsonAsync("/api/v1/models/retrain", new { algorithm = "ocsvm", source = "benchmark" })).StatusCode);

        await using var db = factory.NewDbContext();
        var denials = await db.AuditEvents.Where(a => a.Action == AuditActions.AuthorizationDenied).ToListAsync();
        Assert.Contains(denials, a => a.Actor == "engineer" && a.TargetId == $"POST {target}" && a.Result == AuditResults.Denied);
        Assert.Contains(denials, a => a.Actor == "anonymous" && a.TargetId == $"POST {target}");
        Assert.False(await db.ModelVersions.AnyAsync(m => m.IsActive && m.ActivatedBy == "engineer"));

        // Administrators can read the audit trail; engineers cannot.
        var admin = await factory.ClientAsAsync("admin");
        var audit = await admin.GetFromJsonAsync<JsonElement>("/api/v1/audit?action=authorization.denied");
        Assert.True(audit.GetProperty("total").GetInt32() >= 2);
        Assert.Equal(HttpStatusCode.Forbidden, (await engineer.GetAsync("/api/v1/audit")).StatusCode);
    }

    [Fact]
    public async Task Benchmark_reference_model_cannot_be_activated_through_the_api()
    {
        var admin = await factory.ClientAsAsync("admin");
        var reg = await admin.PostAsJsonAsync("/api/v1/models", new { modelVersion = StubMlServiceHandler.ReferenceModel });
        Assert.True(reg.StatusCode is HttpStatusCode.Created or HttpStatusCode.Conflict);
        var act = await admin.PostAsync($"/api/v1/models/{StubMlServiceHandler.ReferenceModelId}/activate", null);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, act.StatusCode);
    }

    [Fact]
    public async Task Model_registration_rejects_paths_and_unknown_versions()
    {
        var admin = await factory.ClientAsAsync("admin");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/models", new { modelVersion = "../../etc/passwd" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/models", new { modelVersion = "ocsvm-ops-v1-nope" })).StatusCode);
    }

    [Fact]
    public async Task Ingestion_requires_the_service_api_key()
    {
        var body = new { events = new[] { EventFactory.Request("key-test", DateTime.UtcNow.AddHours(-1), 100) } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/api/v1/events", body)).StatusCode);
        var wrong = factory.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Api-Key", "wrong-key");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync("/api/v1/events", body)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await factory.IngestionClient().PostAsJsonAsync("/api/v1/events", body)).StatusCode);
    }

    [Fact]
    public async Task Search_accepts_only_constrained_filters()
    {
        var engineer = await factory.ClientAsAsync("engineer");
        var r = await engineer.GetAsync("/api/v1/events?service=orders-api&eventType=%7B%22match_all%22%3A%7B%7D%7D&correlationId=%22%7D%7D");
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, body.GetProperty("total").GetInt64());
    }

    [Fact]
    public async Task Demo_scenarios_are_disabled_outside_development()
    {
        var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsync("/demo/scenarios/latency_spike", null)).StatusCode);
        var state = await client.GetFromJsonAsync<JsonElement>("/demo/scenarios");
        Assert.False(state.GetProperty("enabled").GetBoolean());
    }
}

/// <summary>TC-04: ML service failure must not interrupt ordinary application processing.</summary>
public sealed class ResilienceTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task TC04_ml_timeout_does_not_affect_requests_and_scoring_is_deferred_then_recovered()
    {
        const string service = "tc04-api";
        var admin = await factory.ClientAsAsync("admin");
        await admin.PostAsJsonAsync("/api/v1/models", new { modelVersion = StubMlServiceHandler.ProductionModel });
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsync($"/api/v1/models/{StubMlServiceHandler.ProductionModelId}/activate", null)).StatusCode);

        var start = new DateTime(2022, 3, 1, 10, 0, 0, DateTimeKind.Utc);
        await factory.IngestionClient().PostAsJsonAsync("/api/v1/events", new { events = EventFactory.Window(service, start, 60, 250, "tc04") });

        factory.Ml.Behavior = StubBehavior.Timeout;
        try
        {
            // Ordinary application requests keep working while the ML service hangs.
            var demo = factory.CreateClient();
            var sw = Stopwatch.StartNew();
            var statuses = new List<HttpStatusCode>();
            foreach (var path in new[] { "/demo/products", "/demo/orders/42", "/demo/products", "/demo/orders/7" })
            {
                statuses.Add((await demo.GetAsync(path)).StatusCode);
            }

            // 200, or the workload's own 2 % simulated failure — never a timeout or gateway error from ML.
            Assert.All(statuses, s => Assert.True(s is HttpStatusCode.OK or HttpStatusCode.InternalServerError, s.ToString()));
            Assert.Contains(HttpStatusCode.OK, statuses);
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(30));
            Assert.DoesNotContain(factory.Ml.Calls, c => c.Path.StartsWith("demo", StringComparison.Ordinal));
            var health = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
            Assert.NotEqual("Unhealthy", health.GetProperty("status").GetString());

            // Scoring is deferred, not lost.
            var run = await admin.PostAsync("/api/v1/pipeline/run", null);
            Assert.Equal(HttpStatusCode.OK, run.StatusCode);
            await using (var db = factory.NewDbContext())
            {
                var window = await db.FeatureWindows.SingleAsync(w => w.ServiceName == service);
                Assert.Equal(ScoringStatus.Deferred, window.ScoringStatus);
                Assert.True(window.ScoringAttempts >= 1);
                Assert.NotNull(window.NextScoringAttemptUtc);
                Assert.Contains("Unavailable", window.LastScoringError);
                Assert.False(await db.ScoringRecords.AnyAsync(s => s.WindowId == window.WindowId));
            }
        }
        finally
        {
            factory.Ml.Behavior = StubBehavior.Normal;
        }

        // Recovery: after the circuit break and back-off elapse, the deferred window is scored.
        await Task.Delay(TimeSpan.FromSeconds(2.5));
        await admin.PostAsync("/api/v1/pipeline/requeue", null);
        await admin.PostAsync("/api/v1/pipeline/run", null);
        await using var verify = factory.NewDbContext();
        var scored = await verify.FeatureWindows.SingleAsync(w => w.ServiceName == service);
        Assert.Equal(ScoringStatus.Scored, scored.ScoringStatus);
        Assert.True(await verify.ScoringRecords.AnyAsync(s => s.WindowId == scored.WindowId));
    }

    [Fact]
    public async Task Health_reports_each_dependency()
    {
        var health = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/health");
        var components = health.GetProperty("components");
        Assert.Equal("Healthy", components.GetProperty("postgresql").GetString());
        Assert.Equal("Healthy", components.GetProperty("opensearch").GetString());
        Assert.Equal("Healthy", components.GetProperty("ml-service").GetString());

        var engineer = await factory.ClientAsAsync("engineer");
        var status = await engineer.GetFromJsonAsync<JsonElement>("/api/v1/system/status");
        Assert.Contains(status.GetProperty("components").EnumerateArray(), c => c.GetProperty("name").GetString() == "api");
        Assert.Equal(5, status.GetProperty("pipeline").GetProperty("windowSizeMinutes").GetInt32());
    }
}

/// <summary>Database-level guarantees (real PostgreSQL): migrations, uniqueness, one active model.</summary>
public sealed class DatabaseTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [Fact]
    public async Task Migrations_are_applied()
    {
        _ = factory.CreateClient();
        await using var db = factory.NewDbContext();
        var applied = await db.Database.GetAppliedMigrationsAsync();
        Assert.Contains(applied, m => m.EndsWith("_InitialCreate", StringComparison.Ordinal));
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task Event_id_uniqueness_is_enforced_by_the_database()
    {
        _ = factory.CreateClient();
        await using var db = factory.NewDbContext();
        var service = new ServiceDefinition("db-unique", "production", DateTime.UtcNow);
        db.Services.Add(service);
        await db.SaveChangesAsync();
        OperationalEvent Make() => new("same-id-db-test", service, DateTime.UtcNow.AddHours(-1), EventType.HttpRequest, "/a", 200, 10, false, AuthenticationResult.None, null, 0, "c", null, DateTime.UtcNow);
        db.OperationalEvents.Add(Make());
        await db.SaveChangesAsync();
        db.OperationalEvents.Add(Make());
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task Only_one_model_can_be_active_per_schema()
    {
        _ = factory.CreateClient();
        await using var db = factory.NewDbContext();
        ModelVersion Make(string v) => new(Guid.NewGuid(), v, "lof", "ops-v1", null, null, 1, "{}", "{}", 0.5, "max_f1", $"{v}/model.joblib", "abc", true, null, DateTime.UtcNow, DateTime.UtcNow, "test");
        var a = Make("db-test-a");
        var b = Make("db-test-b");
        a.Activate("test", DateTime.UtcNow);
        b.Activate("test", DateTime.UtcNow);
        db.ModelVersions.AddRange(a, b);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.True(db.IsUniqueViolation(ex));
    }

    [Fact]
    public async Task Swagger_document_describes_the_versioned_api()
    {
        var doc = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var paths = doc.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        foreach (var expected in new[] { "/api/v1/events", "/api/v1/windows/{id}", "/api/v1/anomalies/{id}/reviews", "/api/v1/models/{id}/activate", "/api/v1/system/status", "/api/v1/health" })
        {
            Assert.Contains(expected, paths);
        }
    }
}
