using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AnomalyDetection.Domain.Enums;

namespace AnomalyDetection.Application.Ingestion;

/// <summary>A validated event in canonical form, ready to persist.</summary>
public sealed record NormalizedEvent(
    string EventId,
    DateTime EventTimestampUtc,
    string ServiceName,
    string Environment,
    EventType EventType,
    string EndpointGroup,
    int? StatusCode,
    double? DurationMs,
    bool ErrorFlag,
    AuthenticationResult AuthenticationResult,
    string? DependencyName,
    int RetryCount,
    string? CorrelationId,
    string? AttributesJson);

public sealed record NormalizationResult(NormalizedEvent? Event, IReadOnlyList<string> ReasonCodes, RawEventInput Sanitized)
{
    public bool IsValid => Event is not null;
}

/// <summary>Machine-readable quarantine reasons.</summary>
public static class QuarantineReasons
{
    public const string MissingTimestamp = "missing_timestamp";
    public const string InvalidTimestamp = "invalid_timestamp";
    public const string FutureTimestamp = "future_timestamp";
    public const string MissingService = "missing_service";
    public const string InvalidService = "invalid_service";
    public const string MissingEnvironment = "missing_environment";
    public const string InvalidEnvironment = "invalid_environment";
    public const string MissingEventType = "missing_event_type";
    public const string UnknownEventType = "unknown_event_type";
    public const string InvalidStatusCode = "invalid_status_code";
    public const string MalformedDuration = "malformed_duration";
    public const string NegativeDuration = "negative_duration";
    public const string ImplausibleDuration = "implausible_duration";
    public const string MalformedErrorFlag = "malformed_error_flag";
    public const string UnknownAuthenticationResult = "unknown_authentication_result";
    public const string InvalidDependencyName = "invalid_dependency_name";
    public const string MalformedRetryCount = "malformed_retry_count";
    public const string NegativeRetryCount = "negative_retry_count";
    public const string ImplausibleRetryCount = "implausible_retry_count";
    public const string InvalidCorrelationId = "invalid_correlation_id";
    public const string InvalidEventId = "invalid_event_id";
    public const string SecretLikeValue = "secret_like_value";
    public const string MalformedEvent = "malformed_event";
}

/// <summary>
/// Deterministic validation and normalization into the canonical schema (report §3.4–§3.5, §4.3):
/// timestamp conversion to UTC, categorical mapping, null handling, range validation and duplicate keys.
/// The same input always yields the same output; invalid input yields quarantine reasons instead of an exception.
/// </summary>
public static partial class EventNormalizer
{
    public const double MaxDurationMs = 3_600_000d;
    public const int MaxRetryCount = 1_000;
    public const string UnknownEndpoint = "unknown";
    public const string NoEndpoint = "none";

    private static readonly DateTime MinimumTimestampUtc = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static readonly Dictionary<string, EventType> EventTypeSynonyms = new(StringComparer.Ordinal)
    {
        ["httprequest"] = EventType.HttpRequest,
        ["request"] = EventType.HttpRequest,
        ["http"] = EventType.HttpRequest,
        ["requestcompleted"] = EventType.HttpRequest,
        ["apirequest"] = EventType.HttpRequest,
        ["authentication"] = EventType.Authentication,
        ["auth"] = EventType.Authentication,
        ["authn"] = EventType.Authentication,
        ["login"] = EventType.Authentication,
        ["signin"] = EventType.Authentication,
        ["dependencycall"] = EventType.DependencyCall,
        ["dependency"] = EventType.DependencyCall,
        ["externalcall"] = EventType.DependencyCall,
        ["databasecall"] = EventType.DependencyCall,
        ["dbcall"] = EventType.DependencyCall,
        ["backgroundjob"] = EventType.BackgroundJob,
        ["job"] = EventType.BackgroundJob,
        ["scheduledjob"] = EventType.BackgroundJob,
        ["worker"] = EventType.BackgroundJob,
        ["batchjob"] = EventType.BackgroundJob,
    };

    private static readonly Dictionary<string, string> EnvironmentSynonyms = new(StringComparer.Ordinal)
    {
        ["prod"] = "production",
        ["prd"] = "production",
        ["production"] = "production",
        ["live"] = "production",
        ["stage"] = "staging",
        ["stg"] = "staging",
        ["staging"] = "staging",
        ["preprod"] = "staging",
        ["dev"] = "development",
        ["develop"] = "development",
        ["development"] = "development",
        ["local"] = "development",
        ["test"] = "test",
        ["testing"] = "test",
        ["qa"] = "test",
    };

    private static readonly Dictionary<string, AuthenticationResult> AuthSynonyms = new(StringComparer.Ordinal)
    {
        ["none"] = AuthenticationResult.None,
        ["na"] = AuthenticationResult.None,
        ["anonymous"] = AuthenticationResult.None,
        ["notapplicable"] = AuthenticationResult.None,
        ["success"] = AuthenticationResult.Success,
        ["succeeded"] = AuthenticationResult.Success,
        ["successful"] = AuthenticationResult.Success,
        ["ok"] = AuthenticationResult.Success,
        ["authenticated"] = AuthenticationResult.Success,
        ["allowed"] = AuthenticationResult.Success,
        ["granted"] = AuthenticationResult.Success,
        ["failure"] = AuthenticationResult.Failure,
        ["failed"] = AuthenticationResult.Failure,
        ["fail"] = AuthenticationResult.Failure,
        ["denied"] = AuthenticationResult.Failure,
        ["unauthorized"] = AuthenticationResult.Failure,
        ["forbidden"] = AuthenticationResult.Failure,
        ["invalid"] = AuthenticationResult.Failure,
        ["rejected"] = AuthenticationResult.Failure,
    };

    [GeneratedRegex(@"^[a-z0-9][a-z0-9.\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceNameRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9\-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex EnvironmentRegex();

    [GeneratedRegex(@"^[a-z0-9][a-z0-9._:\-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex DependencyRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:|/]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex CorrelationIdRegex();

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventIdRegex();

    [GeneratedRegex(@"^(\d+|[0-9a-fA-F]{8}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{4}-?[0-9a-fA-F]{12}|[0-9a-fA-F]{16,}|.*@.*|\[masked-[a-z]+\])$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierSegmentRegex();

    [GeneratedRegex(@"[\s_]+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRegex();

    public static NormalizationResult Normalize(RawEventInput input, DateTime nowUtc, TimeSpan maxFutureSkew)
    {
        var sanitization = EventSanitizer.Sanitize(input);
        var raw = sanitization.Sanitized;
        var reasons = new List<string>();

        foreach (var field in sanitization.SecretLikeCanonicalFields)
        {
            reasons.Add($"{QuarantineReasons.SecretLikeValue}:{field}");
        }

        if (raw.Attributes.ContainsKey("_malformed"))
        {
            reasons.Add(QuarantineReasons.MalformedEvent);
        }

        var timestamp = ParseTimestamp(raw.EventTimestamp, nowUtc, maxFutureSkew, reasons);
        var service = NormalizeServiceName(raw.ServiceName, reasons);
        var environment = NormalizeEnvironment(raw.Environment, reasons);
        var eventType = NormalizeEventType(raw.EventType, reasons);
        var statusCode = ParseStatusCode(raw.StatusCode, reasons);
        var duration = ParseDuration(raw.DurationMs, reasons);
        var explicitError = ParseBool(raw.ErrorFlag, reasons);
        var auth = NormalizeAuthentication(raw.AuthenticationResult, reasons);
        var dependency = NormalizeDependency(raw.DependencyName, reasons);
        var retries = ParseRetryCount(raw.RetryCount, reasons);
        var correlationId = NormalizeCorrelationId(raw.CorrelationId, reasons);
        var endpoint = NormalizeEndpoint(raw.EndpointGroup, eventType);

        if (raw.EventId is not null && !EventIdRegex().IsMatch(raw.EventId.Trim()))
        {
            reasons.Add(QuarantineReasons.InvalidEventId);
        }

        if (reasons.Count > 0 || timestamp is null || service is null || environment is null || eventType is null)
        {
            return new NormalizationResult(null, reasons.Distinct().ToList(), raw);
        }

        // Derived values (documented rules):
        //  * ErrorFlag defaults to StatusCode >= 500 when not supplied explicitly.
        //  * HTTP 401/403 responses without an explicit outcome count as authentication/authorization failures.
        var errorFlag = explicitError ?? statusCode is >= 500;
        if (auth == AuthenticationResult.None && eventType == EventType.HttpRequest && statusCode is 401 or 403)
        {
            auth = AuthenticationResult.Failure;
        }

        var eventId = string.IsNullOrWhiteSpace(raw.EventId)
            ? DeriveEventId(timestamp.Value, service, environment, eventType.Value, endpoint, statusCode, duration, dependency, retries, correlationId)
            : raw.EventId.Trim();

        var attributesJson = raw.Attributes.Count == 0 ? null : JsonSerializer.Serialize(raw.Attributes);

        var normalized = new NormalizedEvent(
            eventId,
            timestamp.Value,
            service,
            environment,
            eventType.Value,
            endpoint,
            statusCode,
            duration,
            errorFlag,
            auth,
            dependency,
            retries,
            correlationId,
            attributesJson);

        return new NormalizationResult(normalized, [], raw);
    }

    /// <summary>Normalizes an endpoint into a low-cardinality group: no host, no query string, identifiers templated.</summary>
    public static string NormalizeEndpoint(string? endpoint, EventType? eventType)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return eventType == EventType.HttpRequest ? UnknownEndpoint : NoEndpoint;
        }

        var value = endpoint.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
        {
            value = absolute.AbsolutePath;
        }

        var cut = value.IndexOfAny(['?', '#']);
        if (cut >= 0)
        {
            value = value[..cut];
        }

        // An HTTP method prefix such as "GET /orders/42" is kept as part of the group.
        var method = string.Empty;
        var space = value.IndexOf(' ');
        if (space > 0 && space < 8)
        {
            method = value[..space].ToUpperInvariant() + " ";
            value = value[(space + 1)..].Trim();
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => IdentifierSegmentRegex().IsMatch(s) ? "{id}" : s.ToLowerInvariant());
        var path = "/" + string.Join('/', segments);
        var result = method + path;
        return result.Length <= 200 ? result : result[..200];
    }

    public static string DeriveEventId(
        DateTime timestampUtc,
        string service,
        string environment,
        EventType eventType,
        string endpoint,
        int? statusCode,
        double? durationMs,
        string? dependency,
        int retries,
        string? correlationId)
    {
        var canonical = string.Join(
            '|',
            timestampUtc.Ticks.ToString(CultureInfo.InvariantCulture),
            service,
            environment,
            eventType.ToToken(),
            endpoint,
            statusCode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            durationMs?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            dependency ?? string.Empty,
            retries.ToString(CultureInfo.InvariantCulture),
            correlationId ?? string.Empty);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return "derived-" + Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static DateTime? ParseTimestamp(string? value, DateTime nowUtc, TimeSpan maxFutureSkew, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add(QuarantineReasons.MissingTimestamp);
            return null;
        }

        // Timestamps without an explicit offset are interpreted as UTC (documented assumption A3).
        if (!DateTimeOffset.TryParse(value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            reasons.Add(QuarantineReasons.InvalidTimestamp);
            return null;
        }

        var utc = parsed.UtcDateTime;
        if (utc < MinimumTimestampUtc)
        {
            reasons.Add(QuarantineReasons.InvalidTimestamp);
            return null;
        }

        if (utc > nowUtc + maxFutureSkew)
        {
            reasons.Add(QuarantineReasons.FutureTimestamp);
            return null;
        }

        return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
    }

    private static string? NormalizeServiceName(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add(QuarantineReasons.MissingService);
            return null;
        }

        var normalized = SeparatorRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        if (!ServiceNameRegex().IsMatch(normalized))
        {
            reasons.Add(QuarantineReasons.InvalidService);
            return null;
        }

        return normalized;
    }

    private static string? NormalizeEnvironment(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add(QuarantineReasons.MissingEnvironment);
            return null;
        }

        var key = SeparatorRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        var normalized = EnvironmentSynonyms.TryGetValue(key.Replace("-", string.Empty, StringComparison.Ordinal), out var mapped) ? mapped : key;
        if (!EnvironmentRegex().IsMatch(normalized))
        {
            reasons.Add(QuarantineReasons.InvalidEnvironment);
            return null;
        }

        return normalized;
    }

    private static EventType? NormalizeEventType(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reasons.Add(QuarantineReasons.MissingEventType);
            return null;
        }

        var key = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (EventTypeSynonyms.TryGetValue(key, out var type))
        {
            return type;
        }

        reasons.Add(QuarantineReasons.UnknownEventType);
        return null;
    }

    private static int? ParseStatusCode(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) && code is >= 100 and <= 599)
        {
            return code;
        }

        reasons.Add(QuarantineReasons.InvalidStatusCode);
        return null;
    }

    private static double? ParseDuration(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var duration) || double.IsNaN(duration) || double.IsInfinity(duration))
        {
            reasons.Add(QuarantineReasons.MalformedDuration);
            return null;
        }

        if (duration < 0)
        {
            reasons.Add(QuarantineReasons.NegativeDuration);
            return null;
        }

        if (duration > MaxDurationMs)
        {
            reasons.Add(QuarantineReasons.ImplausibleDuration);
            return null;
        }

        return Math.Round(duration, 3);
    }

    private static bool? ParseBool(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "true" or "1" or "yes" or "y": return true;
            case "false" or "0" or "no" or "n": return false;
            default:
                reasons.Add(QuarantineReasons.MalformedErrorFlag);
                return null;
        }
    }

    private static AuthenticationResult NormalizeAuthentication(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return AuthenticationResult.None;
        }

        var key = new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (AuthSynonyms.TryGetValue(key, out var result))
        {
            return result;
        }

        reasons.Add(QuarantineReasons.UnknownAuthenticationResult);
        return AuthenticationResult.None;
    }

    private static string? NormalizeDependency(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = SeparatorRegex().Replace(value.Trim().ToLowerInvariant(), "-");
        if (!DependencyRegex().IsMatch(normalized))
        {
            reasons.Add(QuarantineReasons.InvalidDependencyName);
            return null;
        }

        return normalized;
    }

    private static int ParseRetryCount(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var retries))
        {
            reasons.Add(QuarantineReasons.MalformedRetryCount);
            return 0;
        }

        if (retries < 0)
        {
            reasons.Add(QuarantineReasons.NegativeRetryCount);
            return 0;
        }

        if (retries > MaxRetryCount)
        {
            reasons.Add(QuarantineReasons.ImplausibleRetryCount);
            return 0;
        }

        return retries;
    }

    private static string? NormalizeCorrelationId(string? value, List<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (!CorrelationIdRegex().IsMatch(trimmed))
        {
            reasons.Add(QuarantineReasons.InvalidCorrelationId);
            return null;
        }

        return trimmed;
    }
}
