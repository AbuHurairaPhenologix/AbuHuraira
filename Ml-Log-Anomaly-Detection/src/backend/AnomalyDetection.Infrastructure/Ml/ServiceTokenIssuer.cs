using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AnomalyDetection.Infrastructure.Ml;

/// <summary>Permission scopes for backend → ML calls (report §4.8: dedicated audience and permission scope).</summary>
public static class MlScopes
{
    public const string Score = "anomaly.score";
    public const string ModelsRead = "models.read";
    public const string ModelsAdmin = "models.admin";
    public const string TrainingRun = "training.run";
}

/// <summary>
/// Issues short-lived service-to-service JWTs (HS256) with a dedicated audience and a single scope per call.
/// The shape (iss/aud/sub/scope/exp) is OIDC client-credentials compatible, so production can swap in workload identity.
/// </summary>
public sealed class ServiceTokenIssuer(IOptions<MlServiceOptions> options, TimeProvider clock)
{
    private readonly JsonWebTokenHandler _handler = new();

    public string Issue(string scope)
    {
        var o = options.Value;
        var key = Encoding.UTF8.GetBytes(o.ServiceTokenSigningKey);
        if (key.Length < 32)
        {
            throw new InvalidOperationException("MlService:ServiceTokenSigningKey must be configured with at least 32 bytes.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = o.ServiceTokenIssuer,
            Audience = o.ServiceTokenAudience,
            Subject = new ClaimsIdentity([new Claim("sub", o.ServiceTokenIssuer), new Claim("scope", scope)]),
            IssuedAt = now,
            NotBefore = now.AddSeconds(-5),
            Expires = now.AddSeconds(o.ServiceTokenLifetimeSeconds),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256),
            Claims = new Dictionary<string, object> { ["jti"] = Guid.NewGuid().ToString("N") },
        };
        return _handler.CreateToken(descriptor);
    }
}
