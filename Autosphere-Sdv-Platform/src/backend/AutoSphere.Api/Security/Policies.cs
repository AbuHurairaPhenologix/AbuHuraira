using System.Security.Claims;
using AutoSphere.Application.Abstractions;
using AutoSphere.Application.Common;
using Microsoft.AspNetCore.Authorization;

namespace AutoSphere.Api.Security;

/// <summary>Authorization policies (role-based access control).</summary>
public static class Policies
{
    /// <summary>Read-only monitoring: Viewer, Engineer, Administrator.</summary>
    public const string ViewVehicles = nameof(ViewVehicles);

    /// <summary>Diagnostics and ECU operations: Engineer, Administrator.</summary>
    public const string OperateDiagnostics = nameof(OperateDiagnostics);

    public const string ManageVehicles = nameof(ManageVehicles);

    public const string ManageOta = nameof(ManageOta);

    public const string InjectFaults = nameof(InjectFaults);

    public const string ManageUsers = nameof(ManageUsers);

    public static AuthorizationBuilder AddAutoSpherePolicies(this AuthorizationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Secure by default: every endpoint requires an authenticated user unless marked [AllowAnonymous].
        builder.SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        builder.AddPolicy(ViewVehicles, p => p.RequireRole(Roles.Viewer, Roles.Engineer, Roles.Administrator));
        builder.AddPolicy(OperateDiagnostics, p => p.RequireRole(Roles.Engineer, Roles.Administrator));
        builder.AddPolicy(ManageVehicles, p => p.RequireRole(Roles.Administrator));
        builder.AddPolicy(ManageOta, p => p.RequireRole(Roles.Administrator));
        builder.AddPolicy(InjectFaults, p => p.RequireRole(Roles.Administrator));
        builder.AddPolicy(ManageUsers, p => p.RequireRole(Roles.Administrator));
        return builder;
    }
}

/// <summary>Current user from the HTTP context; background work runs as "system".</summary>
public sealed class HttpContextCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserName => accessor.HttpContext?.User.FindFirstValue(ClaimTypes.Name) ?? "system";
}
