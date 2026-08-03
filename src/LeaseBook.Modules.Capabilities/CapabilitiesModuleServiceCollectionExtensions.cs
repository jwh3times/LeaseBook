using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Resolution;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Capabilities;

/// <summary>
/// Registers the Capabilities module's services with the host container (ADR-028). The
/// <c>IPlatformScope</c> adapter is registered separately in the host, because its implementation is
/// the host-owned <c>PlatformScopedExecutor</c> (ADR-007).
/// </summary>
public static class CapabilitiesModuleServiceCollectionExtensions
{
    /// <param name="enableNotificationListener">
    /// False for the build-time OpenAPI generation pass (ADR-012), which runs this program with no
    /// database and no real configuration. The listener would spin its reconnect loop against nothing.
    /// </param>
    public static IServiceCollection AddCapabilitiesModule(
        this IServiceCollection services, bool enableNotificationListener = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Scoped: it reads through the ambient DbContext, in whichever plane its caller established.
        services.AddScoped<CapabilityStateReader>();

        // Singleton: per-replica state. It creates its own scope per refresh — see the lifetime note
        // on the class for why injecting the scoped executor or DbContext here would be a bug.
        services.AddSingleton<CapabilityCache>();

        if (enableNotificationListener)
        {
            // Registered by concrete type as well as hosted service, so diagnostics and the readiness
            // probe can read its counters. AddHostedService alone would make it unresolvable.
            services.AddSingleton<CapabilityNotificationListener>();
            services.AddHostedService(sp => sp.GetRequiredService<CapabilityNotificationListener>());
        }

        return services;
    }
}
