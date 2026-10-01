using System.Text.Json;
using AnomalyDetection.Application.Ingestion;

namespace AnomalyDetection.UnitTests.Ingestion;

/// <summary>TC-08: secret-like input is excluded or rejected before anything is persisted or used for features.</summary>
public sealed class EventSanitizerTests
{
    private static RawEventInput WithAttributes(Dictionary<string, string?> attributes) => new()
    {
        EventId = "evt-secret",
        EventTimestamp = "2026-10-01T11:00:00Z",
        ServiceName = "identity-api",
        Environment = "production",
        EventType = "http_request",
        EndpointGroup = "/login",
        StatusCode = "200",
        DurationMs = "10",
        Attributes = attributes,
    };

    [Theory]
    [InlineData("password")]
    [InlineData("Authorization")]
    [InlineData("access_token")]
    [InlineData("Cookie")]
    [InlineData("set-cookie")]
    [InlineData("client_secret")]
    [InlineData("x-api-key")]
    [InlineData("private_key")]
    [InlineData("sessionId")]
    public void Sensitive_attribute_keys_are_removed(string key)
    {
        var result = EventSanitizer.Sanitize(WithAttributes(new() { [key] = "s3cr3t-value", ["region"] = "eu-west" }));
        Assert.DoesNotContain(key, result.Sanitized.Attributes.Keys);
        Assert.Contains(key, result.RemovedAttributeKeys);
        Assert.Equal("eu-west", result.Sanitized.Attributes["region"]);
    }

    [Fact]
    public void Secret_like_values_are_redacted_and_identifiers_masked()
    {
        var result = EventSanitizer.Sanitize(WithAttributes(new()
        {
            ["note"] = "header was Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.abc123def",
            ["user"] = "jane.doe@example.com from 10.20.30.40",
            ["config"] = "password=hunter2",
        }));
        var attrs = result.Sanitized.Attributes;
        Assert.DoesNotContain("eyJ", attrs["note"]);
        Assert.Contains(EventSanitizer.Redacted, attrs["note"]);
        Assert.Equal($"{EventSanitizer.MaskedEmail} from {EventSanitizer.MaskedIp}", attrs["user"]);
        Assert.DoesNotContain("hunter2", attrs["config"]);
    }

    [Fact]
    public void Secret_in_a_canonical_field_quarantines_the_event_and_the_stored_copy_is_redacted()
    {
        var input = WithAttributes([]);
        input.CorrelationId = "Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0In0.sig";
        var result = EventNormalizer.Normalize(input, new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc), TimeSpan.FromMinutes(5));
        Assert.False(result.IsValid);
        Assert.Contains("secret_like_value:correlationId", result.ReasonCodes);
        var stored = JsonSerializer.Serialize(result.Sanitized.ToDictionary());
        Assert.DoesNotContain("eyJ", stored);
    }

    [Fact]
    public void Accepted_event_attributes_never_contain_removed_secrets()
    {
        var result = EventNormalizer.Normalize(
            WithAttributes(new() { ["password"] = "hunter2", ["Authorization"] = "Basic dXNlcjpwYXNz", ["build"] = "1.2.3" }),
            new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
            TimeSpan.FromMinutes(5));
        Assert.True(result.IsValid);
        Assert.DoesNotContain("hunter2", result.Event!.AttributesJson);
        Assert.DoesNotContain("dXNlcjpwYXNz", result.Event.AttributesJson);
        Assert.Contains("1.2.3", result.Event.AttributesJson);
    }

    [Fact]
    public void Unknown_top_level_json_fields_are_treated_as_attributes_and_sanitized()
    {
        using var doc = JsonDocument.Parse("""{"eventTimestamp":"2026-10-01T11:00:00Z","serviceName":"a","environment":"dev","eventType":"job","password":"x","apiKey":"y","tenant":"t1"}""");
        var raw = RawEventInput.FromJson(doc.RootElement);
        var sanitized = EventSanitizer.Sanitize(raw).Sanitized;
        Assert.Equal(["tenant"], sanitized.Attributes.Keys);
    }
}
