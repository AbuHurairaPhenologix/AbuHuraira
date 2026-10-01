using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using AnomalyDetection.Application.Abstractions;
using AnomalyDetection.Application.Common;
using AnomalyDetection.Application.Ingestion;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Api.Middleware;

/// <summary>
/// Structured logging at the source (report §4.2): records duration, status, endpoint group, correlation ID and
/// exception outcome for the monitored workload and hands the event to a bounded queue. The request never waits for
/// persistence, OpenSearch or the ML service. No request bodies, headers or credentials are captured.
/// </summary>
public sealed partial class OperationalEventMiddleware(
    RequestDelegate next,
    IEventQueue queue,
    IOptions<PipelineOptions> options,
    ILogger<OperationalEventMiddleware> logger)
{
    public const string AuthResultItemKey = "OperationalEvent.AuthenticationResult";
    public const string CapturedPathPrefix = "/demo";

    /// <summary>Removes route constraints so "{id:int}" becomes "{id}" (stable, low-cardinality endpoint groups).</summary>
    [GeneratedRegex(@"\{([A-Za-z0-9_]+)(:[^}]*)?\}")]
    private static partial Regex RouteConstraint();

    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments(CapturedPathPrefix) || context.Request.Path.StartsWithSegments("/demo/scenarios"))
        {
            await next(context);
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var failed = false;
        try
        {
            await next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var durationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var status = failed ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;
            var template = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? context.Request.Path.Value ?? "unknown";
            var endpoint = RouteConstraint().Replace(template, "{$1}");
            var raw = new RawEventInput
            {
                EventId = Guid.NewGuid().ToString("N"),
                EventTimestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                ServiceName = options.Value.DemoServiceName,
                Environment = options.Value.DemoEnvironment,
                EventType = "http_request",
                EndpointGroup = "/" + endpoint.TrimStart('/'),
                StatusCode = status.ToString(CultureInfo.InvariantCulture),
                DurationMs = RawEventInput.FormatNumber(Math.Round(durationMs, 3)),
                ErrorFlag = (failed || status >= 500) ? "true" : "false",
                AuthenticationResult = context.Items.TryGetValue(AuthResultItemKey, out var auth) ? auth as string : null,
                CorrelationId = CorrelationIdMiddleware.Get(context),
            };

            if (!queue.TryEnqueue(raw))
            {
                logger.LogWarning("Operational event queue is full; event for {Endpoint} was not captured.", raw.EndpointGroup);
            }

            logger.LogInformation(
                "RequestCompleted {@OperationalEvent}",
                new { raw.CorrelationId, Endpoint = raw.EndpointGroup, StatusCode = status, DurationMs = durationMs });
        }
    }
}
