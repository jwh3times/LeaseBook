using LeaseBook.Modules.Banking.Import;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Banking;

/// <summary>
/// Registers the Banking module's services with the host container. Called once from <c>Program.cs</c>.
/// CQRS command/query handlers and validators are discovered automatically by <c>AddLeaseBookCqrs</c>
/// (Scrutor) — only non-CQRS services register here. The cross-module <c>IBankRegister</c>/<c>IBankClearing</c>
/// ports (ADR-007 / P68) are implemented by <b>host</b> adapters, so they register in <c>Program.cs</c>,
/// not here (Banking cannot reference the host).
/// </summary>
public static class BankingModuleServiceCollectionExtensions
{
    public static IServiceCollection AddBankingModule(this IServiceCollection services)
    {
        // The CSV parser behind the OFX/QFX-extensible seam (P66). Stateless — singleton is fine.
        services.AddSingleton<IStatementParser, CsvStatementParser>();
        return services;
    }
}
