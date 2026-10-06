using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.IntegrationTests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.IntegrationTests;

/// <summary>
/// Automated end-to-end flow over the real API, PostgreSQL and OpenSearch (ML at the HTTP boundary is a stub):
/// send events → normalize → store → aggregate a logical 5-minute window → 8 features → ML score → persist score →
/// anomaly → retrieve via API → submit review → verify persistence. Synthetic timestamps; no waiting.
/// </summary>
public sealed class PipelineEndToEndTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static readonly DateTime NormalWindow = new(2022, 2, 15, 14, 30, 0, DateTimeKind.Utc);
    private static readonly DateTime SpikeWindow = NormalWindow.AddMinutes(5);

    private async Task ActivateProductionModelAsync(HttpClient admin)
    {
        var models = await admin.GetFromJsonAsync<JsonElement>("/api/v1/models");
        if (!models.EnumerateArray().Any(m => m.GetProperty("modelVersion").GetString() == StubMlServiceHandler.ProductionModel))
        {
            var reg = await admin.PostAsJsonAsync("/api/v1/models", new { modelVersion = StubMlServiceHandler.ProductionModel });
            Assert.Equal(HttpStatusCode.Created, reg.StatusCode);
        }

        var act = await admin.PostAsync($"/api/v1/models/{StubMlServiceHandler.ProductionModelId}/activate", null);
        Assert.Equal(HttpStatusCode.OK, act.StatusCode);
    }

    [Fact]
    public async Task Full_pipeline_from_events_to_reviewed_anomaly()
    {
        const string service = "e2e-shop-api";
        var admin = await factory.ClientAsAsync("admin");
        var engineer = await factory.ClientAsAsync("engineer");
        await ActivateProductionModelAsync(admin);

        // 1-3. Send events (one normal window, one high-latency window, plus a duplicate and an invalid record).
        var events = EventFactory.Window(service, NormalWindow, 140, 220, "norm");
        events.AddRange(EventFactory.Window(service, SpikeWindow, 150, 900, "spike"));
        events.Add(EventFactory.Dependency(service, SpikeWindow.AddMinutes(1), failed: true, retries: 2, correlationId: "spike-dep"));
        var duplicateId = Guid.NewGuid().ToString("N");
        events.Add(EventFactory.Request(service, SpikeWindow.AddMinutes(2), 880, id: duplicateId, correlationId: "spike-dup"));
        events.Add(EventFactory.Request(service, SpikeWindow.AddMinutes(2), 880, id: duplicateId, correlationId: "spike-dup"));
        events.Add(new { eventId = "invalid-1", eventTimestamp = "yesterday", serviceName = service, environment = "production", eventType = "http_request" });

        var ingest = await factory.IngestionClient().PostAsJsonAsync("/api/v1/events", new { events });
        Assert.Equal(HttpStatusCode.Accepted, ingest.StatusCode);
        var ingestion = await ingest.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(292, ingestion.GetProperty("accepted").GetInt32());
        Assert.Equal(1, ingestion.GetProperty("duplicates").GetInt32());
        Assert.Equal(1, ingestion.GetProperty("quarantined").GetInt32());

        // 4-8. Aggregate windows, compute features, score and persist (explicit admin trigger; workers run the same code).
        var run = await admin.PostAsync("/api/v1/pipeline/run", null);
        Assert.Equal(HttpStatusCode.OK, run.StatusCode);

        var windows = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/windows?service={service}");
        Assert.Equal(2, windows.GetProperty("total").GetInt32());
        var spike = windows.GetProperty("items").EnumerateArray().Single(w => w.GetProperty("windowStartUtc").GetDateTime() == SpikeWindow);
        var normal = windows.GetProperty("items").EnumerateArray().Single(w => w.GetProperty("windowStartUtc").GetDateTime() == NormalWindow);
        var features = spike.GetProperty("features");
        Assert.Equal(8, features.EnumerateObject().Count());
        Assert.Equal(151, features.GetProperty("request_count").GetDouble()); // duplicate counted once (TC-09)
        Assert.Equal(1, features.GetProperty("dependency_failure_count").GetDouble());
        Assert.Equal(2, features.GetProperty("retry_count").GetDouble());
        Assert.True(features.GetProperty("avg_duration_ms").GetDouble() > 800);
        Assert.InRange(features.GetProperty("endpoint_entropy").GetDouble(), 2.0, 2.5);
        Assert.Equal("Scored", spike.GetProperty("scoringStatus").GetString());
        Assert.Equal(5, spike.GetProperty("windowSizeMinutes").GetInt32());

        // TC-01: the normal window was scored and persisted, but not flagged.
        var normalDetail = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/windows/{normal.GetProperty("windowId").GetGuid()}");
        var normalScore = normalDetail.GetProperty("scores")[0];
        Assert.False(normalScore.GetProperty("isAnomaly").GetBoolean());
        Assert.True(normalScore.GetProperty("score").GetDouble() < normalScore.GetProperty("threshold").GetDouble());
        Assert.Equal(JsonValueKind.Null, normalDetail.GetProperty("anomalyId").ValueKind);

        // 9. Retrieve the anomaly through the API.
        var list = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies?service={service}");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items")[0];
        var anomalyId = item.GetProperty("anomalyId").GetGuid();
        Assert.Equal(StubMlServiceHandler.ProductionModel, item.GetProperty("modelVersion").GetString());
        Assert.Equal(1.0, item.GetProperty("threshold").GetDouble());
        Assert.True(item.GetProperty("score").GetDouble() >= 1.0);
        Assert.Equal("Unreviewed", item.GetProperty("reviewState").GetString());

        var detail = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies/{anomalyId}");
        Assert.Equal(8, detail.GetProperty("featureDeviations").GetArrayLength());
        Assert.Contains("not a root-cause", detail.GetProperty("anomaly").GetProperty("reasonSummary").GetString());
        var correlationIds = detail.GetProperty("correlationIds").EnumerateArray().Select(c => c.GetString()).ToList();
        Assert.Contains("spike-dep", correlationIds);
        Assert.All(correlationIds, c => Assert.StartsWith("spike", c));

        // FR-07: related raw events come from OpenSearch (constrained filters built by the backend).
        JsonElement related = default;
        for (var i = 0; i < 20; i++)
        {
            related = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies/{anomalyId}/events?pageSize=200");
            if (related.GetProperty("total").GetInt64() == 152)
            {
                break;
            }

            await Task.Delay(500);
        }

        Assert.Equal("opensearch", related.GetProperty("source").GetString());
        Assert.Equal(152, related.GetProperty("total").GetInt64());
        var byCorrelation = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies/{anomalyId}/events?correlationId=spike-dep");
        Assert.Equal(1, byCorrelation.GetProperty("total").GetInt64());
        Assert.Equal("dependency_call", byCorrelation.GetProperty("items")[0].GetProperty("eventType").GetString());

        // 10-11. Submit an engineering review and verify it persisted with history.
        var review = await engineer.PostAsJsonAsync($"/api/v1/anomalies/{anomalyId}/reviews", new { outcome = "ConfirmedIssue", note = "Inventory DB latency during spike window." });
        Assert.Equal(HttpStatusCode.Created, review.StatusCode);
        var history = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies/{anomalyId}/reviews");
        Assert.Equal(1, history.GetArrayLength());
        Assert.Equal("engineer", history[0].GetProperty("reviewer").GetString());
        Assert.Equal("Unreviewed", history[0].GetProperty("previousState").GetString());

        await using var db = factory.NewDbContext();
        var stored = await db.Anomalies.Include(a => a.Reviews).SingleAsync(a => a.AnomalyId == anomalyId);
        Assert.Equal(ReviewState.ConfirmedIssue, stored.ReviewState);
        Assert.Equal("Inventory DB latency during spike window.", stored.Reviews.Single().Note);
        Assert.Contains(await db.AuditEvents.ToListAsync(), a => a.Action == AuditActions.AnomalyReview && a.TargetId == anomalyId.ToString());
        Assert.True(await db.QuarantinedEvents.AnyAsync(q => q.EventId == "invalid-1" && q.ReasonCodes.Contains("invalid_timestamp")));

        // Historical anomalies keep their model even if the model is later deactivated.
        await admin.PostAsync($"/api/v1/models/{StubMlServiceHandler.ProductionModelId}/deactivate", null);
        var after = await engineer.GetFromJsonAsync<JsonElement>($"/api/v1/anomalies/{anomalyId}");
        Assert.Equal(StubMlServiceHandler.ProductionModel, after.GetProperty("anomaly").GetProperty("modelVersion").GetString());
        Assert.False(after.GetProperty("modelIsActive").GetBoolean());
        await ActivateProductionModelAsync(admin);
    }

    [Fact]
    public async Task TC10_late_event_is_accepted_but_does_not_change_the_scored_window()
    {
        const string service = "late-policy-api";
        var admin = await factory.ClientAsAsync("admin");
        await ActivateProductionModelAsync(admin);
        var ingest = factory.IngestionClient();
        await ingest.PostAsJsonAsync("/api/v1/events", new { events = EventFactory.Window(service, NormalWindow, 50, 200, "late") });
        await admin.PostAsync("/api/v1/pipeline/run", null);

        var late = await ingest.PostAsJsonAsync("/api/v1/events", new { events = new[] { EventFactory.Request(service, NormalWindow.AddMinutes(4), 9000, status: 500) } });
        var result = await late.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, result.GetProperty("acceptedLate").GetInt32());

        await using var db = factory.NewDbContext();
        var window = await db.FeatureWindows.SingleAsync(w => w.ServiceName == service);
        Assert.Equal(50, window.RequestCount);
        Assert.Equal(1, window.LateEventCount);
        Assert.Equal(1, await db.ScoringRecords.CountAsync(s => s.WindowId == window.WindowId));
    }

    [Fact]
    public async Task TC08_secrets_never_reach_storage_or_features()
    {
        const string service = "secrets-api";
        var events = new object[]
        {
            EventFactory.Request(service, NormalWindow, 120, id: "with-secret-attrs", attributes: new { password = "hunter2", Authorization = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig", tenant = "t-1" }),
            EventFactory.Request(service, NormalWindow, 120, id: "secret-correlation", correlationId: "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.sig"),
            EventFactory.Request(service, NormalWindow, 120, endpoint: "/login?access_token=abcdef123456", id: "token-in-url"),
        };
        var response = await factory.IngestionClient().PostAsJsonAsync("/api/v1/events", new { events });
        var result = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, result.GetProperty("accepted").GetInt32());
        Assert.Equal(1, result.GetProperty("quarantined").GetInt32());

        await using var db = factory.NewDbContext();
        var stored = await db.OperationalEvents.Where(e => e.ServiceName == service).ToListAsync();
        var serialized = JsonSerializer.Serialize(stored.Select(e => new { e.AttributesJson, e.EndpointGroup, e.CorrelationId }));
        Assert.DoesNotContain("hunter2", serialized);
        Assert.DoesNotContain("eyJ", serialized);
        Assert.DoesNotContain("abcdef123456", serialized);
        Assert.Contains("t-1", serialized);
        var quarantined = await db.QuarantinedEvents.SingleAsync(q => q.EventId == "secret-correlation");
        Assert.DoesNotContain("eyJ", quarantined.SanitizedPayloadJson);
    }
}
