using AutoSphere.Application.Common;
using AutoSphere.Domain.Common;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace AutoSphere.Api.Middleware;

/// <summary>
/// Maps exceptions to RFC 9457 ProblemDetails. Stack traces and internal messages are never returned
/// outside Development.
/// </summary>
public sealed class GlobalExceptionHandler(IProblemDetailsService problemDetails, IHostEnvironment environment, ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ProblemDetails problem;
        switch (exception)
        {
            case ValidationException validation:
                problem = new ValidationProblemDetails(validation.Errors
                    .GroupBy(e => string.IsNullOrEmpty(e.PropertyName) ? "request" : e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()))
                {
                    Status = StatusCodes.Status400BadRequest,
                    Title = "One or more validation errors occurred.",
                };
                break;
            case NotFoundException notFound:
                problem = Problem(StatusCodes.Status404NotFound, "Resource not found", notFound.Message);
                break;
            case DomainException domain:
                problem = Problem(StatusCodes.Status409Conflict, "The request conflicts with the current state", domain.Message);
                break;
            case ServiceUnavailableException unavailable:
                problem = Problem(StatusCodes.Status503ServiceUnavailable, "Service unavailable", unavailable.Message);
                break;
            case OperationCanceledException when httpContext.RequestAborted.IsCancellationRequested:
                return true; // client went away
            default:
                logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
                problem = Problem(StatusCodes.Status500InternalServerError, "An unexpected error occurred",
                    environment.IsDevelopment() ? exception.Message : "The error has been logged. Use the correlation id when reporting it.");
                break;
        }

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = environment.IsDevelopment() ? exception : null,
        });
    }

    private static ProblemDetails Problem(int status, string title, string detail) => new() { Status = status, Title = title, Detail = detail };
}
