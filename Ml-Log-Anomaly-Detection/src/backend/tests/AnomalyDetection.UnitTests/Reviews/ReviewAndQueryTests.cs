using System.Text.Json.Nodes;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Audit;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Reviews;
using AnomalyDetection.Application.Scoring;
using AnomalyDetection.Domain.Entities;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Infrastructure.Search;
using Microsoft.EntityFrameworkCore;

namespace AnomalyDetection.UnitTests.Reviews;

public sealed class ReviewLifecycleTests
{
    private static (ReviewService Svc, Infrastructure.Persistence.AnomalyDetectionDbContext Db, AnomalyRecord Anomaly) Setup()
    {
        var db = TestSupport.NewDb();
        var service = new ServiceDefinition("orders-api", "production", TestSupport.Now);
        var window = TestSupport.Window(service, TestSupport.Now.AddHours(-1), 900);
        var model = TestSupport.Model();
        var scoring = new ScoringRecord(window.WindowId, model.ModelId, model.Version, "ops-v1", 1.5, 1.0, true, "Elevated p95", "[]", TestSupport.Now);
        var anomaly = new AnomalyRecord(scoring, window, TestSupport.Now);
        db.AddRange(service, window, model, scoring, anomaly);
        db.SaveChanges();
        var clock = TestSupport.Clock();
        return (new ReviewService(db, new AuditService(db, clock), clock), db, anomaly);
    }

    [Fact]
    public async Task Reviews_append_history_and_preserve_previous_state()
    {
        var (svc, db, anomaly) = Setup();
        Assert.True((await svc.ReviewAsync(anomaly.AnomalyId, "FalsePositive", "Deploy at 14:30 caused warm-up latency", "engineer", default)).IsOk);
        Assert.True((await svc.ReviewAsync(anomaly.AnomalyId, "ConfirmedIssue", "Re-opened: dependency failures confirmed", "admin", default)).IsOk);

        var history = await svc.HistoryAsync(anomaly.AnomalyId, default);
        Assert.Equal(2, history!.Count);
        Assert.Equal(("Unreviewed", "FalsePositive", "engineer"), (history[0].PreviousState, history[0].Outcome, history[0].Reviewer));
        Assert.Equal(("FalsePositive", "ConfirmedIssue"), (history[1].PreviousState, history[1].Outcome));
        var stored = await db.Anomalies.SingleAsync();
        Assert.Equal(ReviewState.ConfirmedIssue, stored.ReviewState);
        Assert.Equal(1.5, stored.Score);
        Assert.Equal(2, db.AuditEvents.Count(a => a.Action == AuditActions.AnomalyReview));
    }

    [Theory]
    [InlineData("Escalated")]
    [InlineData("")]
    [InlineData("99")]
    public async Task Unknown_outcomes_are_rejected(string outcome)
    {
        var (svc, _, anomaly) = Setup();
        Assert.Equal(OperationStatus.Invalid, (await svc.ReviewAsync(anomaly.AnomalyId, outcome, null, "engineer", default)).Status);
    }

    [Fact]
    public async Task Overlong_notes_and_unknown_anomalies_are_rejected()
    {
        var (svc, _, anomaly) = Setup();
        Assert.Equal(OperationStatus.Invalid, (await svc.ReviewAsync(anomaly.AnomalyId, "BenignChange", new string('x', 4001), "engineer", default)).Status);
        Assert.Equal(OperationStatus.NotFound, (await svc.ReviewAsync(Guid.NewGuid(), "BenignChange", null, "engineer", default)).Status);
    }

    [Fact]
    public void All_documented_outcomes_exist()
    {
        Assert.Equal(
            ["Unreviewed", "ConfirmedIssue", "BenignChange", "FalsePositive", "DuplicateAlert", "InsufficientEvidence"],
            Enum.GetNames<ReviewState>());
    }
}

public sealed class ReasonSummaryTests
{
    [Fact]
    public void Summary_lists_the_most_unusual_features_without_causal_claims()
    {
        var reasons = new List<FeatureReason>
        {
            new("p95_duration_ms", 1800, 650, 180, 6.4, "above"),
            new("dependency_failure_count", 4, 0.15, 0.4, 9.6, "above"),
            new("request_count", 141, 140, 12, 0.08, "above"),
        };
        var text = ReasonSummaryBuilder.Build(reasons);
        Assert.StartsWith("Elevated dependency failure count", text);
        Assert.Contains("p95 response time", text);
        Assert.DoesNotContain("request count", text);
        Assert.Contains("not a root-cause determination", text);
        Assert.DoesNotContain("caused", text);
    }
}

public sealed class OpenSearchQueryBuilderTests
{
    [Fact]
    public void Builds_only_whitelisted_filters()
    {
        var q = OpenSearchQueryBuilder.Build(new EventSearchCriteria("orders-api", "production", new DateTime(2022, 2, 15, 14, 30, 0, DateTimeKind.Utc), new DateTime(2022, 2, 15, 14, 35, 0, DateTimeKind.Utc), "corr-1", "http_request", 2, 25));
        var filters = q["query"]!["bool"]!["filter"]!.AsArray();
        Assert.Equal(5, filters.Count);
        Assert.Equal("orders-api", filters[0]!["term"]!["serviceName"]!.GetValue<string>());
        Assert.Equal("2022-02-15T14:30:00.0000000Z", filters[4]!["range"]!["eventTimestampUtc"]!["gte"]!.GetValue<string>());
        Assert.Equal(25, q["from"]!.GetValue<int>());
        Assert.Equal(25, q["size"]!.GetValue<int>());
    }

    [Fact]
    public void Injection_attempts_stay_literal_term_values()
    {
        const string malicious = "x\"}},{\"match_all\":{}}";
        var q = OpenSearchQueryBuilder.Build(new EventSearchCriteria(null, null, null, null, malicious, null));
        var filters = q["query"]!["bool"]!["filter"]!.AsArray();
        Assert.Single(filters);
        Assert.Equal(malicious, filters[0]!["term"]!["correlationId"]!.GetValue<string>());
        var reparsed = JsonNode.Parse(q.ToJsonString())!;
        Assert.Single(reparsed["query"]!["bool"]!["filter"]!.AsArray());
    }

    [Fact]
    public void Paging_is_bounded_by_the_result_window()
    {
        var q = OpenSearchQueryBuilder.Build(new EventSearchCriteria(null, null, null, null, null, null, Page: 100_000, PageSize: 1000));
        Assert.Equal(200, q["size"]!.GetValue<int>());
        Assert.True(q["from"]!.GetValue<int>() + 200 <= OpenSearchQueryBuilder.MaxResultWindow);
    }
}
