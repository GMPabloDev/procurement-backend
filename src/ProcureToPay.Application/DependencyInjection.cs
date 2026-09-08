using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ProcureToPay.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        // Command handlers resolve IValidator<TCommand> and call ValidateAsync explicitly.
        // No MVC filter or automatic FluentValidation integration is registered.
        services.AddValidatorsFromAssembly(typeof(DependencyInjection).Assembly);

        return services;
    }
}
