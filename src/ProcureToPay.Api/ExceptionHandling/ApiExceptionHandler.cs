using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;

namespace ProcureToPay.Api.ExceptionHandling;

public sealed class ApiExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problemDetails = exception switch
        {
            ValidationException validationException => CreateValidationProblemDetails(validationException),
            DomainValidationException validationException => new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Validation failed",
                Type = "/problems/validation",
                Detail = validationException.Message
            },
            DbUpdateConcurrencyException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Concurrency conflict",
                Type = "/problems/conflict",
                Detail = "The resource was modified by another request."
            },
            DbUpdateException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Type = "/problems/conflict",
                Detail = "The requested change conflicts with existing data."
            },
            DomainForbiddenException forbiddenException => new ProblemDetails
            {
                Status = StatusCodes.Status403Forbidden,
                Title = "Forbidden",
                Type = "/problems/forbidden",
                Detail = forbiddenException.Message
            },
            DomainNotFoundException notFoundException => new ProblemDetails
            {
                Status = StatusCodes.Status404NotFound,
                Title = "Not found",
                Type = "/problems/not-found",
                Detail = notFoundException.Message
            },
            // pi-lens-ignore: lsp:CS0246, CS0246
            PolicyConfigurationUnavailableException configurationException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Policy configuration unavailable",
                Type = "/problems/policy-configuration-unavailable",
                Detail = configurationException.Message
            },
            PolicyDependencyUnavailableException dependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Policy dependency unavailable",
                Type = "/problems/policy-dependency-unavailable",
                Detail = dependencyException.Message
            },
            DomainConflictException conflictException => new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "Conflict",
                Type = "/problems/conflict",
                Detail = conflictException.Message
            },
            DomainException domainException => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Domain rule violation",
                Type = "/problems/domain-rule-violation",
                Detail = domainException.Message
            },
            _ => new ProblemDetails
            {
                Status = StatusCodes.Status500InternalServerError,
                Title = "An unexpected error occurred.",
                Type = "/problems/unexpected-error"
            }
        };

        if (exception is ValidationException or DomainException or DbUpdateException)
        {
            logger.LogWarning(exception, "Handled request exception.");
        }
        else
        {
            logger.LogError(exception, "Unhandled request exception.");
        }

        problemDetails.Extensions["traceId"] = httpContext.TraceIdentifier;
        httpContext.Response.StatusCode = problemDetails.Status ?? StatusCodes.Status500InternalServerError;

        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails
        });

        return true;
    }

    private static ValidationProblemDetails CreateValidationProblemDetails(
        ValidationException validationException)
    {
        var errors = validationException.Errors
            .GroupBy(error => error.PropertyName)
            .ToDictionary(
                group => group.Key,
                group => group.Select(error => error.ErrorMessage).ToArray());

        return new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Type = "/problems/validation"
        };
    }
}
