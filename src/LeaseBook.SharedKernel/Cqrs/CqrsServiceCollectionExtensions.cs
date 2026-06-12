using System.Reflection;
using FluentValidation;
using LeaseBook.SharedKernel.Cqrs.Decorators;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.SharedKernel.Cqrs;

/// <summary>
/// Registers the CQRS spine: the <see cref="ISender"/> dispatcher, all handlers and validators
/// discovered in the given assemblies (Scrutor), and the decorator pipeline composed in pinned
/// order — telemetry (outermost) → validation → handler (P24).
/// </summary>
public static class CqrsServiceCollectionExtensions
{
    public static IServiceCollection AddLeaseBookCqrs(this IServiceCollection services, params Assembly[] assemblies)
    {
        services.AddScoped<ISender, Sender>();

        if (assemblies.Length == 0)
        {
            return services;
        }

        services.Scan(scan => scan
            .FromAssemblies(assemblies)
            .AddClasses(c => c.AssignableTo(typeof(ICommandHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IQueryHandler<,>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime()
            .AddClasses(c => c.AssignableTo(typeof(IValidator<>)), publicOnly: false)
                .AsImplementedInterfaces().WithScopedLifetime());

        // Decorate inner-first so the resolved chain is telemetry(validation(handler)).
        // TryDecorate is a no-op when no handlers of that shape are registered yet (M0 modules
        // ship the spine but no slices), so the host still boots.
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(ValidationCommandDecorator<,>));
        services.TryDecorate(typeof(ICommandHandler<,>), typeof(TelemetryCommandDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(ValidationQueryDecorator<,>));
        services.TryDecorate(typeof(IQueryHandler<,>), typeof(TelemetryQueryDecorator<,>));

        return services;
    }
}
