using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using ProcureToPay.Domain.Modules.Approval;
using ProcureToPay.Domain.Modules.Budget;
using ProcureToPay.Domain.Modules.PurchaseRequests;
using ProcureToPay.Domain.Modules.Sourcing;
using ProcureToPay.Domain.Modules.Suppliers;
using ProcureToPay.Domain.SharedKernel;
using ProcureToPay.Infrastructure.Persistence.Policy;
using ProcureToPay.Infrastructure.Persistence.PurchaseRequests;

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
            BadHttpRequestException tooLargeException when tooLargeException.StatusCode == StatusCodes.Status413PayloadTooLarge => new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload too large",
                Type = "/problems/payload-too-large",
                Detail = tooLargeException.Message
            },
            // pi-lens-ignore: lsp:CS0246
            PolicyPayloadTooLargeException tooLargeException => new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload too large",
                Type = "/problems/payload-too-large",
                Detail = tooLargeException.Message
            },
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
            ApprovalPayloadTooLargeException approvalTooLargeException => new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload too large",
                Type = "/problems/payload-too-large",
                Detail = approvalTooLargeException.Message
            },
            // SPEC 06: the Purchase Requests boundary raises its own dependency and size conditions.
            PurchaseRequestPayloadTooLargeException purchaseRequestTooLargeException => new ProblemDetails
            {
                Status = StatusCodes.Status413PayloadTooLarge,
                Title = "Payload too large",
                Type = "/problems/payload-too-large",
                Detail = purchaseRequestTooLargeException.Message
            },
            // SPEC 08: the budget precheck of a REQUIRE_BUDGET_CHECK control is a business outcome,
            // not a dependency failure, and it keeps its audited evidence for a retry (REQ-06).
            PurchaseRequestBudgetInsufficientException budgetInsufficientException => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Budget insufficient",
                Type = "/problems/budget-insufficient",
                Detail = budgetInsufficientException.Message
            },
            PurchaseRequestDependencyUnavailableException purchaseRequestDependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Purchase request dependency unavailable",
                Type = "/problems/purchase-request-dependency-unavailable",
                Detail = purchaseRequestDependencyException.Message
            },
            ApprovalDependencyUnavailableException approvalDependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Approval dependency unavailable",
                Type = "/problems/approval-dependency-unavailable",
                Detail = approvalDependencyException.Message
            },
            // SPEC 08: an absent, ambiguous or corrupted budget owner, catalog, producer or payload
            // never enables Approval or a balance change (REQ-10, NFR-04).
            BudgetDependencyUnavailableException budgetDependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Budget dependency unavailable",
                Type = "/problems/budget-dependency-unavailable",
                Detail = budgetDependencyException.Message
            },
            BudgetInsufficientException budgetInsufficient => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Budget insufficient",
                Type = "/problems/budget-insufficient",
                Detail = budgetInsufficient.Message
            },
            BudgetReferenceInvalidException budgetReference => new ProblemDetails
            {
                Status = StatusCodes.Status422UnprocessableEntity,
                Title = "Budget reference invalid",
                Type = "/problems/budget-reference-invalid",
                Detail = budgetReference.Message
            },
            // SPEC 09 REQ-11: an absent, ambiguous or corrupted supplier owner, catalog, lookup,
            // key or evidence never enables a supplier, a reference or a signal.
            SupplierDependencyUnavailableException supplierDependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Supplier dependency unavailable",
                Type = "/problems/supplier-dependency-unavailable",
                Detail = supplierDependencyException.Message
            },
            // SPEC 10 REQ-14: an absent, ambiguous, corrupted or unavailable sourcing dependency
            // never produces a quotation, waiver, recommendation, signal or award.
            SourcingDependencyUnavailableException sourcingDependencyException => new ProblemDetails
            {
                Status = StatusCodes.Status503ServiceUnavailable,
                Title = "Sourcing dependency unavailable",
                Type = "/problems/sourcing-dependency-unavailable",
                Detail = sourcingDependencyException.Message
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

        if (exception is DomainConflictException or PolicyConfigurationUnavailableException or PolicyDependencyUnavailableException)        {            using var conflictActivity = PolicyTelemetry.Source.StartActivity("policy.conflict");
            var conflictStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            conflictActivity?.SetTag("policy.conflict_type", exception.GetType().Name);
            conflictActivity?.SetTag("policy.result", "CONFLICT");
            conflictActivity?.SetTag("policy.correlation_reference", httpContext.TraceIdentifier);
            conflictActivity?.SetTag("policy.duration_ms", System.Diagnostics.Stopwatch.GetElapsedTime(conflictStartedAt).TotalMilliseconds);
        }
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
