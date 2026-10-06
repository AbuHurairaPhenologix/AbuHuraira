using AnomalyDetection.Application.Ingestion;
using AnomalyDetection.Domain.Enums;
using AnomalyDetection.Domain.Windowing;

namespace AnomalyDetection.UnitTests.Ingestion;

public sealed class EventNormalizerTests
{
    private static readonly DateTime Now = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Skew = TimeSpan.FromMinutes(5);

    private static RawEventInput Valid() => new()
    {
        EventId = "evt-1",
        EventTimestamp = "2026-10-01T11:00:00Z",
        ServiceName = "Orders API",
        Environment = "PROD",
        EventType = "HttpRequest",
        EndpointGroup = "GET /orders/12345?access_token=abc",
        StatusCode = "200",
        DurationMs = "123.4567",
        CorrelationId = "corr-1",
    };

    private static NormalizationResult Normalize(RawEventInput input) => EventNormalizer.Normalize(input, Now, Skew);

    [Fact]
    public void Valid_event_is_normalized_to_canonical_schema()
    {
        var e = Normalize(Valid()).Event!;
        Assert.Equal("orders-api", e.ServiceName);
        Assert.Equal("production", e.Environment);
        Assert.Equal(EventType.HttpRequest, e.EventType);
        Assert.Equal("GET /orders/{id}", e.EndpointGroup);
        Assert.Equal(200, e.StatusCode);
        Assert.Equal(123.457, e.DurationMs);
        Assert.False(e.ErrorFlag);
        Assert.Equal(AuthenticationResult.None, e.AuthenticationResult);
        Assert.Null(e.DependencyName);
        Assert.Equal(0, e.RetryCount);
        Assert.Equal("corr-1", e.CorrelationId);
        Assert.Equal(DateTimeKind.Utc, e.EventTimestampUtc.Kind);
    }

    [Fact]
    public void Timestamps_are_converted_to_utc()
    {
        var input = Valid();
        input.EventTimestamp = "2026-10-01T16:00:00+05:00";
        Assert.Equal(new DateTime(2026, 10, 1, 11, 0, 0, DateTimeKind.Utc), Normalize(input).Event!.EventTimestampUtc);
        input.EventTimestamp = "2026-10-01 11:30:00"; // no offset => UTC (documented assumption)
        Assert.Equal(new DateTime(2026, 10, 1, 11, 30, 0, DateTimeKind.Utc), Normalize(input).Event!.EventTimestampUtc);
    }

    [Theory]
    [InlineData(null, QuarantineReasons.MissingTimestamp)]
    [InlineData("not-a-date", QuarantineReasons.InvalidTimestamp)]
    [InlineData("1970-01-01T00:00:00Z", QuarantineReasons.InvalidTimestamp)]
    [InlineData("2026-10-01T12:30:00Z", QuarantineReasons.FutureTimestamp)]
    public void Invalid_timestamps_are_quarantined(string? timestamp, string reason)
    {
        var input = Valid();
        input.EventTimestamp = timestamp;
        var result = Normalize(input);
        Assert.False(result.IsValid);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Theory]
    [InlineData("-5", QuarantineReasons.NegativeDuration)]
    [InlineData("NaN", QuarantineReasons.MalformedDuration)]
    [InlineData("abc", QuarantineReasons.MalformedDuration)]
    [InlineData("99999999", QuarantineReasons.ImplausibleDuration)]
    public void Impossible_durations_are_quarantined(string duration, string reason)
    {
        var input = Valid();
        input.DurationMs = duration;
        Assert.Contains(reason, Normalize(input).ReasonCodes);
    }

    [Theory]
    [InlineData("statusCode", "700", QuarantineReasons.InvalidStatusCode)]
    [InlineData("statusCode", "OK", QuarantineReasons.InvalidStatusCode)]
    [InlineData("eventType", "telemetry", QuarantineReasons.UnknownEventType)]
    [InlineData("retryCount", "-1", QuarantineReasons.NegativeRetryCount)]
    [InlineData("retryCount", "two", QuarantineReasons.MalformedRetryCount)]
    [InlineData("errorFlag", "maybe", QuarantineReasons.MalformedErrorFlag)]
    [InlineData("authenticationResult", "sometimes", QuarantineReasons.UnknownAuthenticationResult)]
    [InlineData("correlationId", "has spaces and <tags>", QuarantineReasons.InvalidCorrelationId)]
    [InlineData("serviceName", "", QuarantineReasons.MissingService)]
    [InlineData("environment", "", QuarantineReasons.MissingEnvironment)]
    public void Malformed_values_are_quarantined_with_reasons(string field, string value, string reason)
    {
        var input = Valid();
        typeof(RawEventInput).GetProperties().Single(p => string.Equals(p.Name, field, StringComparison.OrdinalIgnoreCase)).SetValue(input, value);
        var result = Normalize(input);
        Assert.False(result.IsValid);
        Assert.Contains(reason, result.ReasonCodes);
    }

    [Theory]
    [InlineData("request", EventType.HttpRequest)]
    [InlineData("HTTP_REQUEST", EventType.HttpRequest)]
    [InlineData("Dependency-Call", EventType.DependencyCall)]
    [InlineData("login", EventType.Authentication)]
    [InlineData("background job", EventType.BackgroundJob)]
    public void Event_types_are_mapped_from_synonyms(string raw, EventType expected)
    {
        var input = Valid();
        input.EventType = raw;
        Assert.Equal(expected, Normalize(input).Event!.EventType);
    }

    [Theory]
    [InlineData("Denied", AuthenticationResult.Failure)]
    [InlineData("SUCCEEDED", AuthenticationResult.Success)]
    [InlineData(null, AuthenticationResult.None)]
    public void Authentication_results_are_normalized(string? raw, AuthenticationResult expected)
    {
        var input = Valid();
        input.AuthenticationResult = raw;
        Assert.Equal(expected, Normalize(input).Event!.AuthenticationResult);
    }

    [Fact]
    public void Http_401_without_explicit_outcome_counts_as_auth_failure_and_5xx_as_error()
    {
        var input = Valid();
        input.StatusCode = "401";
        Assert.Equal(AuthenticationResult.Failure, Normalize(input).Event!.AuthenticationResult);
        input.StatusCode = "503";
        Assert.True(Normalize(input).Event!.ErrorFlag);
        input.ErrorFlag = "false";
        Assert.False(Normalize(input).Event!.ErrorFlag);
    }

    [Theory]
    [InlineData("/orders/42", "/orders/{id}")]
    [InlineData("/users/3f2504e0-4f89-11d3-9a0c-0305e82c3301/profile", "/users/{id}/profile")]
    [InlineData("https://shop.example.com/Products/?q=1#x", "/products")]
    [InlineData("/customers/jane.doe@example.com", "/customers/{id}")]
    [InlineData(null, "unknown")]
    public void Endpoints_are_grouped_without_identifiers_or_query_strings(string? raw, string expected)
    {
        Assert.Equal(expected, EventNormalizer.NormalizeEndpoint(raw, EventType.HttpRequest));
    }

    [Fact]
    public void Missing_event_id_gets_a_deterministic_derived_id()
    {
        var a = Valid();
        a.EventId = null;
        var b = Valid();
        b.EventId = null;
        var idA = Normalize(a).Event!.EventId;
        Assert.StartsWith("derived-", idA);
        Assert.Equal(idA, Normalize(b).Event!.EventId);
    }

    [Fact]
    public void Null_optional_fields_get_canonical_defaults()
    {
        var input = new RawEventInput { EventTimestamp = "2026-10-01T11:00:00Z", ServiceName = "svc", Environment = "dev", EventType = "background_job" };
        var e = Normalize(input).Event!;
        Assert.Equal("none", e.EndpointGroup);
        Assert.Null(e.StatusCode);
        Assert.Null(e.DurationMs);
        Assert.False(e.ErrorFlag);
        Assert.Equal(0, e.RetryCount);
        Assert.Null(e.CorrelationId);
        Assert.Equal("development", e.Environment);
    }
}

public sealed class WindowAlignerTests
{
    [Theory]
    [InlineData(5, "2022-02-15T14:33:59Z", "2022-02-15T14:30:00Z", "2022-02-15T14:35:00Z")]
    [InlineData(5, "2022-02-15T14:35:00Z", "2022-02-15T14:35:00Z", "2022-02-15T14:40:00Z")]
    [InlineData(1, "2022-02-15T14:33:59Z", "2022-02-15T14:33:00Z", "2022-02-15T14:34:00Z")]
    [InlineData(15, "2022-02-15T14:33:59Z", "2022-02-15T14:30:00Z", "2022-02-15T14:45:00Z")]
    public void Timestamps_align_to_fixed_windows(int minutes, string ts, string start, string end)
    {
        var w = WindowAligner.Align(DateTime.Parse(ts, null, System.Globalization.DateTimeStyles.AdjustToUniversal), minutes);
        Assert.Equal(DateTime.Parse(start, null, System.Globalization.DateTimeStyles.AdjustToUniversal), w.StartUtc);
        Assert.Equal(DateTime.Parse(end, null, System.Globalization.DateTimeStyles.AdjustToUniversal), w.EndUtc);
    }

    [Fact]
    public void Unsupported_sizes_and_local_times_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WindowAligner.Align(DateTime.UtcNow, 7));
        Assert.Throws<ArgumentException>(() => WindowAligner.Align(new DateTime(2022, 1, 1, 0, 0, 0, DateTimeKind.Local), 5));
        Assert.Equal([1, 5, 15], WindowAligner.SupportedWindowSizesMinutes);
    }
}
