using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace AnomalyDetection.Api.Auth;

/// <summary>Requires the ingestion service credential (<c>X-Api-Key</c>) on event-producer endpoints.</summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class)]
public sealed class RequireIngestionKeyAttribute : TypeFilterAttribute
{
    public RequireIngestionKeyAttribute()
        : base(typeof(IngestionApiKeyFilter))
    {
    }
}

public sealed class IngestionApiKeyFilter(IOptions<IngestionOptions> options, ILogger<IngestionApiKeyFilter> logger) : IAuthorizationFilter
{
    public const string HeaderName = "X-Api-Key";

    public void OnAuthorization(AuthorizationFilterContext context)
    {
        var expected = options.Value.ApiKey;
        var provided = context.HttpContext.Request.Headers[HeaderName].ToString();
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(provided) || !DevelopmentTokenService.FixedTimeEquals(expected, provided))
        {
            // Never log the provided value.
            logger.LogWarning("Rejected event ingestion request without a valid {Header} from {RemoteIp}", HeaderName, context.HttpContext.Connection.RemoteIpAddress);
            context.Result = new UnauthorizedObjectResult(new ProblemDetails
            {
                Status = StatusCodes.Status401Unauthorized,
                Title = "Missing or invalid ingestion API key.",
            });
        }
    }
}
