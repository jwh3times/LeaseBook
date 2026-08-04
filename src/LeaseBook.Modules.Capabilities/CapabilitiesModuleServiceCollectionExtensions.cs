using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Capabilities;

/// <summary>
/// Registers the Capabilities module's services with the host container (ADR-028).
/// <para>
/// Two things are deliberately NOT here, both because they are composition-root concerns the module
/// must not own: the <c>IPlatformScope</c> adapter (its implementation is the host-owned
/// <c>PlatformScopedExecutor</c> — ADR-007), and the hosted services, which are lifecycle decisions
/// the host gates on its own configuration.
/// </para>
/// </summary>
public static class CapabilitiesModuleServiceCollectionExtensions
{
    public static IServiceCollection AddCapabilitiesModule(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: it reads through the ambient DbContext, in whichever plane its caller established.
        services.AddScoped<CapabilityStateReader>();

        // Singleton: per-replica state. It creates its own scope per refresh — see the lifetime note
        // on the class for why injecting the scoped executor or DbContext here would be a bug.
        services.AddSingleton<CapabilityCache>();

        // Scoped: the seam every caller uses. It binds the singleton cache and the scoped reader to
        // the ambient (org, user), so it can only live as long as that context does.
        services.AddScoped<ICapabilityGate, CapabilityGate>();

        return services;
    }
}
