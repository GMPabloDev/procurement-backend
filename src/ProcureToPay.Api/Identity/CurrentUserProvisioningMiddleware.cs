using ProcureToPay.Infrastructure.Persistence.Organization;

namespace ProcureToPay.Api.Identity;

public sealed class CurrentUserProvisioningMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        CurrentUserProvisioningService provisioningService)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await provisioningService.EnsureProfileAsync(
                context.User,
                context.RequestAborted);
        }

        await next(context);
    }
}
