using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ThermoTwin.Domain;
using ThermoTwin.Infrastructure.Persistence;

namespace ThermoTwin.Api.Infrastructure;

/// <summary>Maps exceptions to RFC 7807 problem details; never leaks stack traces to clients.</summary>
public sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(IProblemDetailsService problemDetails, ILogger<GlobalExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title) = exception switch
        {
            ValidationException => (StatusCodes.Status400BadRequest, "Validation failed"),
            ArgumentException => (StatusCodes.Status400BadRequest, "Invalid argument"),
            DomainException => (StatusCodes.Status409Conflict, "Operation not allowed in the current state"),
            KeyNotFoundException => (StatusCodes.Status404NotFound, "Resource not found"),
            InvalidOperationException => (StatusCodes.Status422UnprocessableEntity, "Numerical operation failed"),
            _ => (StatusCodes.Status500InternalServerError, "Unexpected server error"),
        };

        if (status >= 500)
        {
            _logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }
        else
        {
            _logger.LogWarning("{Title}: {Message}", title, exception.Message);
        }

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = status >= 500 ? "The server could not complete the request." : exception.Message,
            Instance = httpContext.Request.Path,
        };

        if (exception is ValidationException validation)
        {
            problem.Extensions["errors"] = validation.Errors
                .GroupBy(e => e.PropertyName)
                .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray());
        }

        httpContext.Response.StatusCode = status;
        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception,
        });
    }
}

public sealed class DatabaseHealthCheck : IHealthCheck
{
    private readonly ThermoTwinDbContext _db;

    public DatabaseHealthCheck(ThermoTwinDbContext db) => _db = db;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        await _db.Database.CanConnectAsync(cancellationToken)
            ? HealthCheckResult.Healthy("SQLite reachable")
            : HealthCheckResult.Unhealthy("SQLite unreachable");
}
