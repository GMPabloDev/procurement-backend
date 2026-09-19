using Microsoft.AspNetCore.Mvc;
using ProcureToPay.Infrastructure.Persistence.PurchaseOrders;

namespace ProcureToPay.Api.Health;

/// <summary>
/// Fail-closed gate of the Purchase Orders commands (SPEC 11 REQ-10/REQ-12, REQ-11). While the
/// takeover preflight of the deployment is not satisfied, every state-changing command of the module
/// answers the contractual <c>503 /problems/purchase-order-dependency-unavailable</c> instead of
/// running against a fence that may not be preserved. Reads stay available.
/// </summary>
public sealed class PurchaseOrderCommandGateMiddleware(
    RequestDelegate next,
    PurchaseOrderCommandGate gate)
{
    private static readonly string[] CommandPrefixes =
    [
        "/api/v1/purchase-orders",
        "/api/v1/direct-purchases",
        "/api/v1/supporting-documents"
    ];

    public async Task InvokeAsync(HttpContext context)
    {
        if (!gate.IsOpen && IsCommand(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(
                new ProblemDetails
                {
                    Status = StatusCodes.Status503ServiceUnavailable,
                    Title = "Purchase order dependency unavailable",
                    Type = "/problems/purchase-order-dependency-unavailable",
                    Detail = "The purchase order commands are not enabled in this deployment yet."
                },
                context.RequestAborted);
            return;
        }

        await next(context);
    }

    private static bool IsCommand(HttpRequest request) =>
        request.Method is "POST" or "PUT" or "PATCH" or "DELETE" &&
        CommandPrefixes.Any(prefix => request.Path.StartsWithSegments(prefix, StringComparison.Ordinal));
}
