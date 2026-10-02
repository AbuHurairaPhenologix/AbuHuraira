using AutoSphere.Application.Common;
using FluentValidation;

namespace AutoSphere.Application.Users;

public sealed record LoginRequest(string UserName, string Password);

public sealed record AuthResult(string AccessToken, DateTimeOffset ExpiresAt, string UserName, IReadOnlyList<string> Roles);

public sealed record UserDto(string Id, string UserName, string? Email, IReadOnlyList<string> Roles, bool IsLockedOut);

public sealed record CreateUserRequest(string UserName, string Email, string Password, string Role);

public sealed record ChangeRoleRequest(string Role);

/// <summary>Authentication and user management (implemented with ASP.NET Core Identity).</summary>
public interface IIdentityService
{
    /// <summary>Returns <c>null</c> for invalid credentials or locked-out users.</summary>
    Task<AuthResult?> LoginAsync(LoginRequest request, CancellationToken cancellationToken);

    Task<IReadOnlyList<UserDto>> ListUsersAsync(CancellationToken cancellationToken);

    Task<UserDto> CreateUserAsync(CreateUserRequest request, CancellationToken cancellationToken);

    Task<UserDto> ChangeRoleAsync(string userId, string role, CancellationToken cancellationToken);

    Task DeleteUserAsync(string userId, CancellationToken cancellationToken);
}

public sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().MaximumLength(64);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(128);
    }
}

public sealed class CreateUserRequestValidator : AbstractValidator<CreateUserRequest>
{
    public CreateUserRequestValidator()
    {
        RuleFor(r => r.UserName).NotEmpty().MaximumLength(64).Matches("^[a-zA-Z0-9._-]+$");
        RuleFor(r => r.Email).NotEmpty().EmailAddress();
        RuleFor(r => r.Password).NotEmpty().MinimumLength(12).MaximumLength(128);
        RuleFor(r => r.Role).Must(Roles.All.Contains).WithMessage($"Role must be one of: {string.Join(", ", Roles.All)}.");
    }
}

public sealed class ChangeRoleRequestValidator : AbstractValidator<ChangeRoleRequest>
{
    public ChangeRoleRequestValidator() =>
        RuleFor(r => r.Role).Must(Roles.All.Contains).WithMessage($"Role must be one of: {string.Join(", ", Roles.All)}.");
}
