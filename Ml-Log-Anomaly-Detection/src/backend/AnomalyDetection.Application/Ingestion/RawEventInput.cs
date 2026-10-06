using System.Globalization;
using System.Text.Json;

namespace AnomalyDetection.Application.Ingestion;

/// <summary>
/// An event exactly as received, before validation. Every field is captured as text so that one malformed value
/// quarantines only that event instead of failing a whole batch.
/// </summary>
public sealed class RawEventInput
{
    public static readonly IReadOnlyList<string> CanonicalFieldNames =
    [
        "eventId", "eventTimestamp", "serviceName", "environment", "eventType", "endpointGroup", "statusCode",
        "durationMs", "errorFlag", "authenticationResult", "dependencyName", "retryCount", "correlationId", "attributes",
    ];

    public string? EventId { get; set; }

    public string? EventTimestamp { get; set; }

    public string? ServiceName { get; set; }

    public string? Environment { get; set; }

    public string? EventType { get; set; }

    public string? EndpointGroup { get; set; }

    public string? StatusCode { get; set; }

    public string? DurationMs { get; set; }

    public string? ErrorFlag { get; set; }

    public string? AuthenticationResult { get; set; }

    public string? DependencyName { get; set; }

    public string? RetryCount { get; set; }

    public string? CorrelationId { get; set; }

    /// <summary>Optional extra fields. Always sanitized; never used as model features.</summary>
    public Dictionary<string, string?> Attributes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Parses one JSON object leniently. Unknown top-level properties are moved into <see cref="Attributes"/>
    /// (where the sanitizer removes anything secret-like). Field names are matched case-insensitively.
    /// </summary>
    public static RawEventInput FromJson(JsonElement element)
    {
        var raw = new RawEventInput();
        if (element.ValueKind != JsonValueKind.Object)
        {
            raw.Attributes["_malformed"] = "event is not a JSON object";
            return raw;
        }

        foreach (var property in element.EnumerateObject())
        {
            var value = property.Value;
            switch (property.Name.ToLowerInvariant())
            {
                case "eventid": raw.EventId = AsText(value); break;
                case "eventtimestamp" or "timestamp": raw.EventTimestamp = AsText(value); break;
                case "servicename" or "service": raw.ServiceName = AsText(value); break;
                case "environment": raw.Environment = AsText(value); break;
                case "eventtype": raw.EventType = AsText(value); break;
                case "endpointgroup" or "endpoint": raw.EndpointGroup = AsText(value); break;
                case "statuscode": raw.StatusCode = AsText(value); break;
                case "durationms": raw.DurationMs = AsText(value); break;
                case "errorflag": raw.ErrorFlag = AsText(value); break;
                case "authenticationresult": raw.AuthenticationResult = AsText(value); break;
                case "dependencyname": raw.DependencyName = AsText(value); break;
                case "retrycount": raw.RetryCount = AsText(value); break;
                case "correlationid": raw.CorrelationId = AsText(value); break;
                case "attributes" when value.ValueKind == JsonValueKind.Object:
                    foreach (var attribute in value.EnumerateObject())
                    {
                        raw.Attributes[attribute.Name] = AsText(attribute.Value);
                    }

                    break;
                default:
                    raw.Attributes[property.Name] = AsText(value);
                    break;
            }
        }

        return raw;
    }

    private static string? AsText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.GetRawText(),
    };

    public static string FormatNumber(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>Serializable copy of all fields (used for quarantine storage after sanitization).</summary>
    public Dictionary<string, object?> ToDictionary() => new()
    {
        ["eventId"] = EventId,
        ["eventTimestamp"] = EventTimestamp,
        ["serviceName"] = ServiceName,
        ["environment"] = Environment,
        ["eventType"] = EventType,
        ["endpointGroup"] = EndpointGroup,
        ["statusCode"] = StatusCode,
        ["durationMs"] = DurationMs,
        ["errorFlag"] = ErrorFlag,
        ["authenticationResult"] = AuthenticationResult,
        ["dependencyName"] = DependencyName,
        ["retryCount"] = RetryCount,
        ["correlationId"] = CorrelationId,
        ["attributes"] = Attributes,
    };
}
