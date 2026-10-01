using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AnomalyDetection.Api.Auth;

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, string Username, IReadOnlyList<string> Roles);

/// <summary>
/// Development/demo token issuer. Two demo identities exist: <c>engineer</c> (Engineer) and <c>admin</c>
/// (Administrator + Engineer). Passwords come from configuration (environment variables); if a password is not
/// configured that identity is disabled. Production deployments use OIDC instead.
/// </summary>
public sealed class DevelopmentTokenService(IOptions<AuthOptions> options, TimeProvider clock)
{
    public TokenResponse? Authenticate(string? username, string? password)
    {
        var o = options.Value;
        if (o.IsOidc || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return null;
        }

        (string? Expected, string[] Roles) account = username switch
        {
            "engineer" => (o.DemoEngineerPassword, [Roles.Engineer]),
            "admin" => (o.DemoAdminPassword, [Roles.Administrator, Roles.Engineer]),
            _ => (null, []),
        };

        if (string.IsNullOrEmpty(account.Expected) || !FixedTimeEquals(account.Expected, password))
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new List<Claim> { new("sub", username), new("name", username) };
        claims.AddRange(account.Roles.Select(r => new Claim("role", r)));
        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = o.Issuer,
            Audience = o.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddMinutes(o.TokenLifetimeMinutes),
            SigningCredentials = new SigningCredentials(SigningKey(o), SecurityAlgorithms.HmacSha256),
        });
        return new TokenResponse(token, "Bearer", o.TokenLifetimeMinutes * 60, username, account.Roles);
    }

    public static SymmetricSecurityKey SigningKey(AuthOptions o)
    {
        var bytes = Encoding.UTF8.GetBytes(o.SigningKey);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Auth:SigningKey must be configured with at least 32 bytes (set Auth__SigningKey).");
        }

        return new SymmetricSecurityKey(bytes);
    }

    public static bool FixedTimeEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)),
            SHA256.HashData(Encoding.UTF8.GetBytes(actual)));
}
