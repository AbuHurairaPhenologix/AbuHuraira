using System.ComponentModel.DataAnnotations;
using AnomalyDetection.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AnomalyDetection.Api.Controllers;

public sealed class TokenRequest
{
    [Required]
    [MaxLength(64)]
    public string Username { get; set; } = string.Empty;

    [Required]
    [MaxLength(256)]
    public string Password { get; set; } = string.Empty;
}

/// <summary>Development/demo token endpoint. Disabled in OIDC mode.</summary>
[Route("api/v1/auth")]
public sealed class AuthController(DevelopmentTokenService tokens, ILogger<AuthController> logger) : ApiControllerBase
{
    [HttpPost("token")]
    [AllowAnonymous]
    [EnableRateLimiting("auth")]
    public IActionResult Token([FromBody] TokenRequest request)
    {
        var token = tokens.Authenticate(request.Username, request.Password);
        if (token is null)
        {
            logger.LogWarning("Failed sign-in for user {Username}", request.Username.Length <= 64 ? request.Username : "(too long)");
            return Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Invalid username or password.");
        }

        return Ok(token);
    }

    [HttpGet("me")]
    [Authorize]
    public IActionResult Me() => Ok(new
    {
        username = Actor,
        roles = User.Claims.Where(c => c.Type is "role" or "roles").Select(c => c.Value).Distinct(),
    });
}
