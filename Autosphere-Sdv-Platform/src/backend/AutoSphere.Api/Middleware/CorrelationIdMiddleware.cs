using Serilog.Context;

namespace AutoSphere.Api.Middleware;

/// <summary>
/// Accepts an incoming <c>X-Correlation-Id</c> (or creates one), echoes it in the response and attaches it
/// to every log event of the request.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Correlation-Id";

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlationId = context.Request.Headers.TryGetValue(HeaderName, out var value)
                            && Guid.TryParse(value.ToString(), out var parsed)
            ? parsed.ToString("D")
            : Guid.NewGuid().ToString("D");

        context.Items[HeaderName] = correlationId;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlationId))
        {
            await next(context);
        }
    }
}
