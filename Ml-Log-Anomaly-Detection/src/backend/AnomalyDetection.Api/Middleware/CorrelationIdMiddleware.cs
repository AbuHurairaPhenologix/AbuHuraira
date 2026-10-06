using System.Diagnostics;
using System.Text.RegularExpressions;

namespace AnomalyDetection.Api.Middleware;

/// <summary>
/// Accepts a well-formed <c>X-Correlation-ID</c> from the caller or derives one from the W3C trace, exposes it on the
/// response, and adds it to the logging scope so related events can be connected across services (report §4.2).
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    public const string ItemKey = "CorrelationId";

    [GeneratedRegex(@"^[A-Za-z0-9\-_.:|/]{1,128}$")]
    private static partial Regex ValidId();

    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers[HeaderName].ToString();
        var correlationId = !string.IsNullOrEmpty(incoming) && ValidId().IsMatch(incoming)
            ? incoming
            : Activity.Current?.TraceId.ToHexString() ?? context.TraceIdentifier;

        context.Items[ItemKey] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(context);
        }
    }

    public static string Get(HttpContext context) =>
        context.Items.TryGetValue(ItemKey, out var value) && value is string id ? id : context.TraceIdentifier;
}
