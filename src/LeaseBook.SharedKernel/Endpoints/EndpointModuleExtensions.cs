using System.Reflection;
using Microsoft.AspNetCore.Routing;

namespace LeaseBook.SharedKernel.Endpoints;

/// <summary>Host-side discovery of <see cref="IEndpointModule"/> implementations.</summary>
public static class EndpointModuleExtensions
{
    /// <summary>
    /// Finds every concrete <see cref="IEndpointModule"/> in the given assemblies, instantiates it,
    /// and lets it map its endpoints. Modules with no endpoints yet contribute nothing.
    /// </summary>
    public static IEndpointRouteBuilder MapModuleEndpoints(this IEndpointRouteBuilder app, params Assembly[] assemblies)
    {
        foreach (var module in DiscoverModules(assemblies))
        {
            module.MapEndpoints(app);
        }

        return app;
    }

    private static IEnumerable<IEndpointModule> DiscoverModules(Assembly[] assemblies) =>
        assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsAbstract: false, IsInterface: false } && typeof(IEndpointModule).IsAssignableFrom(t))
            .Select(Activator.CreateInstance)
            .OfType<IEndpointModule>();
}
