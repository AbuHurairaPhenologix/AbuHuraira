using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Features;

namespace AnomalyDetection.UnitTests.Features;

public sealed class FeatureStatisticsTests
{
    [Fact]
    public void P95_uses_linear_interpolation_like_numpy()
    {
        var values = Enumerable.Range(1, 100).Select(v => (double)v).ToArray();
        Assert.Equal(95.05, FeatureStatistics.Percentile(values, 95), 9);
        Assert.Equal(19.5, FeatureStatistics.Percentile([10d, 20d], 95), 9);
        Assert.Equal(42d, FeatureStatistics.Percentile([42d], 95));
        Assert.Equal(0d, FeatureStatistics.Percentile([], 95));
    }

    [Fact]
    public void P95_is_order_independent()
    {
        double[] values = [500, 120, 80, 1000, 240, 260, 90];
        Assert.Equal(FeatureStatistics.Percentile(values, 95), FeatureStatistics.Percentile(values.Reverse().ToArray(), 95));
    }

    [Fact]
    public void Percentile_rejects_out_of_range()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FeatureStatistics.Percentile([1d], 101));
    }

    [Fact]
    public void Shannon_entropy_in_bits()
    {
        Assert.Equal(0d, FeatureStatistics.ShannonEntropyBits(Array.Empty<string>()));
        Assert.Equal(0d, FeatureStatistics.ShannonEntropyBits(["/a", "/a", "/a"]));
        Assert.Equal(1d, FeatureStatistics.ShannonEntropyBits(["/a", "/b"]), 12);
        Assert.Equal(2d, FeatureStatistics.ShannonEntropyBits(["/a", "/b", "/c", "/d"]), 12);
        Assert.Equal(1.5d, FeatureStatistics.ShannonEntropyBits(["/a", "/a", "/b", "/c"]), 12);
    }
}

public sealed class FeatureCalculatorTests
{
    private static FeatureInputEvent Request(string id, double? duration = 100, bool error = false, string endpoint = "/a", AuthenticationResult auth = AuthenticationResult.None, int? status = 200) =>
        new(id, EventType.HttpRequest, endpoint, status, duration, error, auth, 0);

    [Fact]
    public void Empty_window_yields_zero_vector()
    {
        var f = FeatureCalculator.Calculate([]);
        Assert.Equal(new FeatureVector(0, 0, 0, 0, 0, 0, 0, 0), f);
    }

    [Fact]
    public void Request_count_counts_only_http_requests()
    {
        var f = FeatureCalculator.Calculate(
        [
            Request("1"), Request("2"),
            new("3", EventType.DependencyCall, "/dependency/db", 200, 10, false, AuthenticationResult.None, 0),
            new("4", EventType.Authentication, "/auth/login", 200, null, false, AuthenticationResult.Success, 0),
        ]);
        Assert.Equal(2, f.RequestCount);
    }

    [Fact]
    public void Error_rate_is_errors_over_requests()
    {
        var f = FeatureCalculator.Calculate([Request("1", error: true), Request("2"), Request("3"), Request("4")]);
        Assert.Equal(0.25, f.ErrorRate, 12);
    }

    [Fact]
    public void Average_and_p95_duration_ignore_missing_durations()
    {
        var f = FeatureCalculator.Calculate([Request("1", 100), Request("2", 200), Request("3", 300), Request("4", null)]);
        Assert.Equal(200, f.AvgDurationMs, 12);
        Assert.Equal(290, f.P95DurationMs, 12);
    }

    [Fact]
    public void Auth_failure_rate_is_failures_over_requests()
    {
        var f = FeatureCalculator.Calculate(
        [
            Request("1", auth: AuthenticationResult.Failure, status: 401),
            Request("2", auth: AuthenticationResult.Success),
            Request("3"), Request("4"),
        ]);
        Assert.Equal(0.25, f.AuthFailureRate, 12);
    }

    [Fact]
    public void TC06_authentication_failure_burst_increases_auth_failure_rate()
    {
        var normal = Enumerable.Range(0, 100).Select(i => Request($"n{i}", auth: i < 2 ? AuthenticationResult.Failure : AuthenticationResult.Success)).ToList();
        var burst = Enumerable.Range(0, 100).Select(i => Request($"b{i}", auth: i < 15 ? AuthenticationResult.Failure : AuthenticationResult.Success)).ToList();
        var before = FeatureCalculator.Calculate(normal).AuthFailureRate;
        var after = FeatureCalculator.Calculate(burst).AuthFailureRate;
        Assert.Equal(0.02, before, 12);
        Assert.Equal(0.15, after, 12);
        Assert.True(after > before * 6);
    }

    [Fact]
    public void Dependency_failures_and_retries()
    {
        var f = FeatureCalculator.Calculate(
        [
            new("1", EventType.DependencyCall, "/dependency/db", 503, 20, true, AuthenticationResult.None, 2),
            new("2", EventType.DependencyCall, "/dependency/db", 200, 20, false, AuthenticationResult.None, 0),
            new("3", EventType.DependencyCall, "/dependency/cache", 503, 20, true, AuthenticationResult.None, 1),
            new("4", EventType.BackgroundJob, "/jobs/x", 200, 50, false, AuthenticationResult.None, 3),
            Request("5"),
        ]);
        Assert.Equal(2, f.DependencyFailureCount);
        Assert.Equal(6, f.RetryCount);
    }

    [Fact]
    public void Endpoint_entropy_is_shannon_entropy_of_request_endpoints()
    {
        var f = FeatureCalculator.Calculate([Request("1", endpoint: "/a"), Request("2", endpoint: "/a"), Request("3", endpoint: "/b"), Request("4", endpoint: "/c")]);
        Assert.Equal(1.5, f.EndpointEntropy, 12);
    }

    [Fact]
    public void TC09_duplicate_event_ids_are_counted_once()
    {
        var once = FeatureCalculator.Calculate([Request("1", 100, error: true), Request("2", 300)]);
        var twice = FeatureCalculator.Calculate([Request("1", 100, error: true), Request("1", 100, error: true), Request("2", 300)]);
        Assert.Equal(once, twice);
        Assert.Equal(2, twice.RequestCount);
    }

    [Fact]
    public void Calculation_is_deterministic_regardless_of_order()
    {
        var events = Enumerable.Range(0, 50)
            .Select(i => Request($"e{i}", 50 + (i * 13 % 400), error: i % 7 == 0, endpoint: $"/ep{i % 5}"))
            .ToList();
        var shuffled = events.OrderBy(e => e.EventId.GetHashCode(StringComparison.Ordinal)).ToList();
        Assert.Equal(FeatureCalculator.Calculate(events), FeatureCalculator.Calculate(shuffled));
    }

    [Fact]
    public void Parity_with_python_reference_example()
    {
        // Same window as src/ml-service/tests/test_features.py::test_window_features_and_duplicates_counted_once
        var f = FeatureCalculator.Calculate(
        [
            Request("e1", 100),
            Request("e2", 200, error: true, endpoint: "/b"),
            Request("e3", 300, auth: AuthenticationResult.Failure, endpoint: "/b"),
            Request("e3", 300, auth: AuthenticationResult.Failure, endpoint: "/b"),
            new("e4", EventType.DependencyCall, "/a", null, null, true, AuthenticationResult.None, 2),
            new("e5", EventType.BackgroundJob, "/a", null, null, false, AuthenticationResult.None, 1),
        ]);
        Assert.Equal(3, f.RequestCount);
        Assert.Equal(1d / 3, f.ErrorRate, 12);
        Assert.Equal(200, f.AvgDurationMs, 12);
        Assert.Equal(290, f.P95DurationMs, 12);
        Assert.Equal(1d / 3, f.AuthFailureRate, 12);
        Assert.Equal(1, f.DependencyFailureCount);
        Assert.Equal(3, f.RetryCount);
        Assert.Equal(FeatureStatistics.ShannonEntropyBits(["/a", "/b", "/b"]), f.EndpointEntropy, 12);
    }

    [Fact]
    public void Ordered_array_follows_ops_v1_schema()
    {
        var v = new FeatureVector(1, 2, 3, 4, 5, 6, 7, 8);
        Assert.Equal([1d, 2, 3, 4, 5, 6, 7, 8], v.ToOrderedArray());
        Assert.Equal(FeatureSchema.OrderedFeatureNames, v.ToDictionary().Keys);
        Assert.Equal("ops-v1", FeatureSchema.CurrentVersion);
        Assert.Equal(8, FeatureSchema.OrderedFeatureNames.Count);
    }
}
