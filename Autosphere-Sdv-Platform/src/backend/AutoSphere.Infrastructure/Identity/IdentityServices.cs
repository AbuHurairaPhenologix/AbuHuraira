using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AutoSphere.Application.Common;
using AutoSphere.Application.Users;
using AutoSphere.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutoSphere.Infrastructure.Identity;

/// <summary>Platform user. Passwords are hashed by ASP.NET Core Identity (PBKDF2), never stored in plain text.</summary>
public sealed class ApplicationUser : IdentityUser;

/// <summary>Configuration section <c>Jwt</c>.</summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    public string Issuer { get; set; } = "autosphere";

    public string Audience { get; set; } = "autosphere-clients";

    /// <summary>
    /// HMAC-SHA256 signing key (≥ 32 bytes). Supply via environment variable <c>Jwt__SigningKey</c>;
    /// when empty in Development an ephemeral random key is generated at start-up.
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    public int AccessTokenMinutes { get; set; } = 120;
}

/// <summary>Resolves the effective signing key exactly once per process.</summary>
public sealed class JwtSigningKeyProvider
{
    public JwtSigningKeyProvider(IOptions<JwtOptions> options, IHostEnvironment environment, ILogger<JwtSigningKeyProvider> logger)
    {
        var configured = options.Value.SigningKey;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException("Jwt:SigningKey must be configured outside Development (see .env.example).");
            }

            logger.LogWarning("No Jwt:SigningKey configured; using an ephemeral development key (tokens become invalid on restart)");
            Key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64));
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(configured);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes long.");
        }

        Key = new SymmetricSecurityKey(bytes);
    }

    public SymmetricSecurityKey Key { get; }
}

public sealed class JwtTokenService(JwtSigningKeyProvider keyProvider, IOptions<JwtOptions> options, TimeProvider timeProvider)
{
    public AuthResult Create(ApplicationUser user, IReadOnlyList<string> roles)
    {
        ArgumentNullException.ThrowIfNull(user);
        var jwt = options.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(jwt.AccessTokenMinutes);
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.UniqueName, user.UserName!),
            new(ClaimTypes.Name, user.UserName!),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = jwt.Issuer,
            Audience = jwt.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(keyProvider.Key, SecurityAlgorithms.HmacSha256),
        });
        return new AuthResult(token, expires, user.UserName!, roles);
    }
}

public sealed class IdentityService(
    UserManager<ApplicationUser> users,
    JwtTokenService tokens,
    IValidator<LoginRequest> loginValidator,
    IValidator<CreateUserRequest> createValidator,
    ILogger<IdentityService> logger) : IIdentityService
{
    public async Task<AuthResult?> LoginAsync(LoginRequest request, CancellationToken cancellationToken)
    {
        await loginValidator.ValidateAndThrowAsync(request, cancellationToken);
        var user = await users.FindByNameAsync(request.UserName);
        if (user is null)
        {
            logger.LogWarning("Failed login for unknown user {UserName}", request.UserName);
            return null;
        }

        if (await users.IsLockedOutAsync(user))
        {
            logger.LogWarning("Login attempt for locked-out user {UserName}", request.UserName);
            return null;
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            logger.LogWarning("Failed login for user {UserName}", request.UserName);
            return null;
        }

        await users.ResetAccessFailedCountAsync(user);
        var roles = (await users.GetRolesAsync(user)).ToList();
        logger.LogInformation("User {UserName} signed in with roles {Roles}", user.UserName, string.Join(",", roles));
        return tokens.Create(user, roles);
    }

    public async Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken cancellationToken)
    {
        var all = await users.Users.OrderBy(u => u.UserName).ToListAsync(cancellationToken);
        var result = new List<UserDto>();
        foreach (var user in all)
        {
            result.Add(await ToDtoAsync(user));
        }

        return result;
    }

    public async Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        await createValidator.ValidateAndThrowAsync(request, cancellationToken);
        var user = new ApplicationUser { UserName = request.UserName, Email = request.Email, EmailConfirmed = true };
        Check(await users.CreateAsync(user, request.Password));
        Check(await users.AddToRoleAsync(user, request.Role));
        logger.LogInformation("User {UserName} created with role {Role}", request.UserName, request.Role);
        return await ToDtoAsync(user);
    }

    public async Task<UserDto> ChangeRoleAsync(string userId, string role, CancellationToken cancellationToken)
    {
        if (!Roles.All.Contains(role))
        {
            throw new DomainException($"Unknown role '{role}'.");
        }

        var user = await users.FindByIdAsync(userId) ?? throw NotFoundException.For("User", userId);
        Check(await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user)));
        Check(await users.AddToRoleAsync(user, role));
        logger.LogInformation("User {UserName} role changed to {Role}", user.UserName, role);
        return await ToDtoAsync(user);
    }

    public async Task DeleteUserAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await users.FindByIdAsync(userId) ?? throw NotFoundException.For("User", userId);
        if ((await users.GetRolesAsync(user)).Contains(Roles.Administrator) && (await users.GetUsersInRoleAsync(Roles.Administrator)).Count == 1)
        {
            throw new DomainException("The last administrator cannot be deleted.");
        }

        Check(await users.DeleteAsync(user));
        logger.LogWarning("User {UserName} deleted", user.UserName);
    }

    private async Task<UserDto> ToDtoAsync(ApplicationUser user) =>
        new(user.Id, user.UserName!, user.Email, (await users.GetRolesAsync(user)).ToList(), await users.IsLockedOutAsync(user));

    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new DomainException(string.Join(" ", result.Errors.Select(e => e.Description)));
        }
    }
}
