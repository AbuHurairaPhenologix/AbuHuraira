using System.Security.Claims;
using AutoSphere.Api.Configuration;
using AutoSphere.Api.Security;
using AutoSphere.Application.Users;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AutoSphere.Api.Controllers;

[ApiController]
[Route("api/auth")]
public sealed class AuthController(IIdentityService identity) : ControllerBase
{
    /// <summary>Exchanges credentials for a JWT access token.</summary>
    [HttpPost("login")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.Login)]
    [ProducesResponseType<AuthResult>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<ActionResult<AuthResult>> Login(LoginRequest request, CancellationToken cancellationToken)
    {
        var result = await identity.LoginAsync(request, cancellationToken);
        return result is null
            ? Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid credentials", detail: "User name or password is incorrect, or the account is locked.")
            : Ok(result);
    }

    /// <summary>Returns the authenticated user's identity and roles.</summary>
    [HttpGet("me")]
    public ActionResult<object> Me() => Ok(new
    {
        UserName = User.FindFirstValue(ClaimTypes.Name),
        Roles = User.FindAll(ClaimTypes.Role).Select(c => c.Value).ToList(),
    });
}

[ApiController]
[Route("api/users")]
[Authorize(Policy = Policies.ManageUsers)]
public sealed class UsersController(IIdentityService identity, FluentValidation.IValidator<ChangeRoleRequest> roleValidator) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken cancellationToken) => identity.ListUsersAsync(cancellationToken);

    [HttpPost]
    [ProducesResponseType<UserDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<UserDto>> Create(CreateUserRequest request, CancellationToken cancellationToken)
    {
        var user = await identity.CreateUserAsync(request, cancellationToken);
        return Created($"/api/users/{user.Id}", user);
    }

    [HttpPut("{userId}/role")]
    public async Task<UserDto> ChangeRole(string userId, ChangeRoleRequest request, CancellationToken cancellationToken)
    {
        await FluentValidation.DefaultValidatorExtensions.ValidateAndThrowAsync(roleValidator, request, cancellationToken);
        return await identity.ChangeRoleAsync(userId, request.Role, cancellationToken);
    }

    [HttpDelete("{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Delete(string userId, CancellationToken cancellationToken)
    {
        await identity.DeleteUserAsync(userId, cancellationToken);
        return NoContent();
    }
}
