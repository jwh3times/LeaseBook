using LeaseBook.Modules.Directory.Contracts;
using LeaseBook.Modules.Directory.Features.ManagementFee;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Directory;

/// <summary>
/// Registers the Directory module's services with the host container. Called once from
/// <c>Program.cs</c>. CQRS command/query handlers and validators are discovered automatically by
/// <c>AddLeaseBookCqrs</c> (Scrutor) — only non-CQRS services register here. Services depend on the
/// ambient <c>DbContext</c> (the host registers it as the base type) + <c>ITenantContext</c>. The
/// cross-module <c>IChartProvisioner</c> port is implemented by a <b>host</b> adapter, so it is
/// registered in <c>Program.cs</c>, not here (Directory cannot reference the host).
/// </summary>
public static class DirectoryModuleServiceCollectionExtensions
{
    public static IServiceCollection AddDirectoryModule(this IServiceCollection services)
    {
        services.AddScoped<IManagementFeeConfig, ManagementFeeConfig>();
        return services;
    }
}
