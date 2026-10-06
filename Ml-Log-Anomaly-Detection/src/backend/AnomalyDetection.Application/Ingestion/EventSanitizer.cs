using System.Text.RegularExpressions;

namespace AnomalyDetection.Application.Ingestion;

public sealed record SanitizationResult(
    RawEventInput Sanitized,
    IReadOnlyList<string> RemovedAttributeKeys,
    IReadOnlyList<string> SecretLikeCanonicalFields);

/// <summary>
/// Removes secrets and masks identifiers before anything is persisted (report §3.3: "the preprocessing layer removes
/// secrets, masks identifiers that are not required for correlation, and restricts the feature set to operational
/// quantities"). Passwords, API secrets, access tokens, Authorization headers, session cookies, client secrets and
/// private keys are never stored and therefore can never reach the feature or training dataset (TC-08).
/// </summary>
public static partial class EventSanitizer
{
    public const string Redacted = "[REDACTED]";
    public const string MaskedEmail = "[masked-email]";
    public const string MaskedIp = "[masked-ip]";

    /// <summary>Attribute keys that indicate sensitive material. Matching keys are removed entirely.</summary>
    [GeneratedRegex(
        @"pass(word|wd|phrase)?|pwd|secret|token|authori[sz]ation|auth[_\-]?header|cookie|session|api[_\-]?key|apikey|client[_\-]?secret|credential|private[_\-]?key|bearer|jwt|signature|credit[_\-]?card|card[_\-]?number|cvv",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveKeyRegex();

    /// <summary>Values that look like credentials regardless of the key they appear under.</summary>
    [GeneratedRegex(
        @"eyJ[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]{5,}\.[A-Za-z0-9_\-]*|\bbearer\s+[A-Za-z0-9\-._~+/]+=*|\bbasic\s+[A-Za-z0-9+/]{8,}=*|-----BEGIN [A-Z ]*PRIVATE KEY-----|\b(password|passwd|pwd|secret|token|api[_\-]?key|client[_\-]?secret|access[_\-]?token)\s*[=:]\s*\S+|\bAKIA[0-9A-Z]{16}\b|\bgh[pousr]_[A-Za-z0-9]{20,}\b|\bsk_(live|test)_[A-Za-z0-9]{10,}\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValueRegex();

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(?:(?:25[0-5]|2[0-4]\d|1?\d?\d)\.){3}(?:25[0-5]|2[0-4]\d|1?\d?\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Regex();

    [GeneratedRegex(@"\b(?:[0-9a-fA-F]{1,4}:){7}[0-9a-fA-F]{1,4}\b", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv6Regex();

    public static bool IsSensitiveKey(string key) => SensitiveKeyRegex().IsMatch(key);

    public static bool ContainsSecret(string? value) => value is not null && SecretValueRegex().IsMatch(value);

    /// <summary>Redacts secret-like substrings and masks e-mail and IP addresses.</summary>
    public static string MaskValue(string value)
    {
        var redacted = SecretValueRegex().Replace(value, Redacted);
        redacted = EmailRegex().Replace(redacted, MaskedEmail);
        redacted = Ipv4Regex().Replace(redacted, MaskedIp);
        return Ipv6Regex().Replace(redacted, MaskedIp);
    }

    public static SanitizationResult Sanitize(RawEventInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var removed = new List<string>();
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (key, value) in input.Attributes.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (IsSensitiveKey(key))
            {
                removed.Add(key);
                continue;
            }

            var safeKey = key.Length > 64 ? key[..64] : key;
            attributes[safeKey] = value is null ? null : Truncate(MaskValue(value), 512);
        }

        // Canonical fields must not carry credentials. If they do, the value is redacted and the event is
        // quarantined by the normalizer (the redacted copy is what gets stored in quarantine).
        var secretFields = new List<string>();
        string? Check(string field, string? value)
        {
            if (value is not null && ContainsSecret(value))
            {
                secretFields.Add(field);
                return MaskValue(value);
            }

            return value;
        }

        var sanitized = new RawEventInput
        {
            EventId = Check("eventId", input.EventId),
            EventTimestamp = Check("eventTimestamp", input.EventTimestamp),
            ServiceName = Check("serviceName", input.ServiceName),
            Environment = Check("environment", input.Environment),
            EventType = Check("eventType", input.EventType),
            // Query strings and fragments are never retained, so credentials passed in URLs are excluded here.
            EndpointGroup = Check("endpointGroup", StripQuery(input.EndpointGroup)),
            StatusCode = Check("statusCode", input.StatusCode),
            DurationMs = Check("durationMs", input.DurationMs),
            ErrorFlag = Check("errorFlag", input.ErrorFlag),
            AuthenticationResult = Check("authenticationResult", input.AuthenticationResult),
            DependencyName = Check("dependencyName", input.DependencyName),
            RetryCount = Check("retryCount", input.RetryCount),
            CorrelationId = Check("correlationId", input.CorrelationId),
            Attributes = attributes,
        };

        return new SanitizationResult(sanitized, removed, secretFields);
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private static string? StripQuery(string? endpoint)
    {
        if (endpoint is null)
        {
            return null;
        }

        var cut = endpoint.IndexOfAny(['?', '#']);
        return cut >= 0 ? endpoint[..cut] : endpoint;
    }
}
