using AnomalyDetection.Application.Audit;
using AnomalyDetection.Domain.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace AnomalyDetection.Api.Auth;

/// <summary>
/// Records an audit event whenever access to an Administrator-protected endpoint is denied (TC-07:
/// "Administrative action denied" with "Authorization audit event" as evidence).
/// </summary>
public sealed class AuditingAuthorizationResultHandler(ILogger<AuditingAuthorizationResultHandler> logger) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (!authorizeResult.Succeeded && RequiresAdministrator(context, policy))
        {
            try
            {
                var audit = context.RequestServices.GetRequiredService<AuditService>();
                var actor = context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name ?? "unknown" : "anonymous";
                await audit.RecordNowAsync(
                    AuditActions.AuthorizationDenied,
                    actor,
                    "endpoint",
                    $"{context.Request.Method} {context.Request.Path}",
                    AuditResults.Denied,
                    new { reason = authorizeResult.Forbidden ? "forbidden" : "unauthenticated", policy = Policies.Administrator },
                    context.RequestAborted);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to write authorization audit event.");
            }
        }

        await _default.HandleAsync(next, context, policy, authorizeResult);
    }

    private static bool RequiresAdministrator(HttpContext context, AuthorizationPolicy policy)
    {
        var endpoint = context.GetEndpoint();
        var data = endpoint?.Metadata.GetOrderedMetadata<IAuthorizeData>() ?? [];
        return data.Any(d => d.Policy == Policies.Administrator)
               || policy.Requirements.OfType<Microsoft.AspNetCore.Authorization.Infrastructure.RolesAuthorizationRequirement>()
                   .Any(r => r.AllowedRoles.Contains(Roles.Administrator) && !r.AllowedRoles.Contains(Roles.Engineer));
    }
}
