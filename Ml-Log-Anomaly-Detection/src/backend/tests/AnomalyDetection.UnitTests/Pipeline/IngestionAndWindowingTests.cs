using System.Globalization;
using AnomalyDetection.Application.Ingestion;
using AnomalyDetection.Application.Windowing;
using AnomalyDetection.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AnomalyDetection.UnitTests.Pipeline;

public sealed class IngestionAndWindowingTests
{
    private static readonly DateTime WindowStart = new(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc);

    private static RawEventInput Request(string id, DateTime ts, double duration = 100, string status = "200") => new()
    {
        EventId = id,
        EventTimestamp = ts.ToString("O", CultureInfo.InvariantCulture),
        ServiceName = "orders-api",
        Environment = "production",
        EventType = "http_request",
        EndpointGroup = "/orders",
        StatusCode = status,
        DurationMs = duration.ToString(CultureInfo.InvariantCulture),
    };

    private static (IngestionService Ingest, WindowAggregationService Aggregate, Infrastructure.Persistence.AnomalyDetectionDbContext Db) Setup()
    {
        var db = TestSupport.NewDb();
        var clock = TestSupport.Clock();
        var options = TestSupport.Pipeline();
        return (new IngestionService(db, clock, options, NullLogger<IngestionService>.Instance),
                new WindowAggregationService(db, clock, options, NullLogger<WindowAggregationService>.Instance),
                db);
    }

    [Fact]
    public async Task Invalid_records_are_quarantined_not_dropped()
    {
        var (ingest, _, db) = Setup();
        var bad = Request("bad", WindowStart);
        bad.DurationMs = "-10";
        var result = await ingest.IngestAsync([Request("ok", WindowStart), bad], "api", CancellationToken.None);
        Assert.Equal(1, result.Accepted);
        Assert.Equal(1, result.Quarantined);
        var q = await db.QuarantinedEvents.SingleAsync();
        Assert.Contains(QuarantineReasons.NegativeDuration, q.ReasonCodes);
        Assert.Equal("bad", q.EventId);
    }

    [Fact]
    public async Task TC09_duplicate_events_are_not_stored_or_counted_twice()
    {
        var (ingest, aggregate, db) = Setup();
        var first = await ingest.IngestAsync([Request("e1", WindowStart), Request("e2", WindowStart.AddMinutes(1)), Request("e1", WindowStart)], "api", CancellationToken.None);
        var second = await ingest.IngestAsync([Request("e2", WindowStart.AddMinutes(1)), Request("e3", WindowStart.AddMinutes(2))], "api", CancellationToken.None);

        Assert.Equal((2, 1), (first.Accepted, first.Duplicates));
        Assert.Equal((1, 1), (second.Accepted, second.Duplicates));
        Assert.Equal(3, await db.OperationalEvents.CountAsync());

        await aggregate.RunOnceAsync(CancellationToken.None);
        var window = await db.FeatureWindows.SingleAsync();
        Assert.Equal(3, window.RequestCount);
        Assert.Equal(3, window.EventCount);
    }

    [Fact]
    public async Task Windows_are_partitioned_by_service_environment_and_time()
    {
        var (ingest, aggregate, db) = Setup();
        var other = Request("x1", WindowStart);
        other.ServiceName = "identity-api";
        var staging = Request("x2", WindowStart);
        staging.Environment = "staging";
        await ingest.IngestAsync([Request("a1", WindowStart), Request("a2", WindowStart.AddMinutes(6)), other, staging], "api", CancellationToken.None);

        var summary = await aggregate.RunOnceAsync(CancellationToken.None);

        Assert.Equal(4, summary.WindowsCreated);
        var windows = await db.FeatureWindows.OrderBy(w => w.ServiceName).ThenBy(w => w.Environment).ThenBy(w => w.WindowStartUtc).ToListAsync();
        Assert.All(windows, w => Assert.Equal(1, w.RequestCount));
        Assert.All(windows, w => Assert.Equal(5, w.WindowSizeMinutes));
        Assert.All(windows, w => Assert.Equal("ops-v1", w.FeatureSchemaVersion));
    }

    [Fact]
    public async Task Open_windows_are_not_finalized_before_allowed_lateness()
    {
        var (ingest, aggregate, db) = Setup();
        // Now = 12:00; lateness 120 s → the 11:55–12:00 window may still receive events.
        await ingest.IngestAsync([Request("recent", TestSupport.Now.AddMinutes(-3))], "api", CancellationToken.None);
        var summary = await aggregate.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, summary.WindowsCreated);
        Assert.Equal(EventProcessingState.Pending, (await db.OperationalEvents.SingleAsync()).ProcessingState);
    }

    [Fact]
    public async Task TC10_late_arriving_event_is_stored_but_does_not_change_the_finalized_window()
    {
        var (ingest, aggregate, db) = Setup();
        await ingest.IngestAsync([Request("on-time-1", WindowStart, 100), Request("on-time-2", WindowStart.AddMinutes(2), 300)], "api", CancellationToken.None);
        await aggregate.RunOnceAsync(CancellationToken.None);
        var before = (await db.FeatureWindows.AsNoTracking().SingleAsync()).Features;

        var late = await ingest.IngestAsync([Request("late-1", WindowStart.AddMinutes(4), 5000, "500")], "api", CancellationToken.None);
        await aggregate.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, late.AcceptedLate);
        var window = await db.FeatureWindows.AsNoTracking().SingleAsync();
        Assert.Equal(before, window.Features);
        Assert.Equal(1, window.LateEventCount);
        var lateEvent = await db.OperationalEvents.SingleAsync(e => e.EventId == "late-1");
        Assert.True(lateEvent.IsLate);
        Assert.Equal(EventProcessingState.Late, lateEvent.ProcessingState);
        Assert.Equal(window.WindowId, lateEvent.FeatureWindowId);
    }

    [Fact]
    public async Task Aggregation_is_idempotent()
    {
        var (ingest, aggregate, db) = Setup();
        await ingest.IngestAsync([Request("a", WindowStart)], "api", CancellationToken.None);
        await aggregate.RunOnceAsync(CancellationToken.None);
        var again = await aggregate.RunOnceAsync(CancellationToken.None);
        Assert.Equal(0, again.WindowsCreated);
        Assert.Equal(1, await db.FeatureWindows.CountAsync());
    }

    [Fact]
    public async Task All_eight_features_are_computed_from_events()
    {
        var (ingest, aggregate, db) = Setup();
        var dep = Request("dep", WindowStart.AddSeconds(30));
        dep.EventType = "dependency_call";
        dep.ErrorFlag = "true";
        dep.RetryCount = "2";
        var auth = Request("auth", WindowStart.AddSeconds(40), status: "401");
        auth.EndpointGroup = "/login";
        await ingest.IngestAsync([Request("r1", WindowStart, 100), Request("r2", WindowStart.AddSeconds(10), 300, "503"), auth, dep], "api", CancellationToken.None);
        await aggregate.RunOnceAsync(CancellationToken.None);

        var f = (await db.FeatureWindows.SingleAsync()).Features;
        Assert.Equal(3, f.RequestCount);
        Assert.Equal(1d / 3, f.ErrorRate, 9);
        Assert.Equal(500d / 3, f.AvgDurationMs, 9);
        Assert.Equal(280, f.P95DurationMs, 9);
        Assert.Equal(1d / 3, f.AuthFailureRate, 9);
        Assert.Equal(1, f.DependencyFailureCount);
        Assert.Equal(2, f.RetryCount);
        Assert.Equal(Domain.Features.FeatureStatistics.ShannonEntropyBits(["/orders", "/orders", "/login"]), f.EndpointEntropy, 12);
    }
}
