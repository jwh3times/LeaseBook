using LeaseBook.Modules.Operations.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Operations;

/// <summary>
/// Registers the Operations module's services with the host container. Called once from
/// <c>Program.cs</c>. The <see cref="RunEngine"/> and its strategy set are wired here;
/// the <see cref="Contracts.IBatchPosting"/> adapter is registered separately in the host
/// (because it crosses the module boundary into Accounting — ADR-007 / ADR-019).
/// </summary>
public static class OperationsModuleServiceCollectionExtensions
{
    public static IServiceCollection AddOperationsModule(this IServiceCollection services)
    {
        // Core run engine: resolves strategies keyed by RunType.
        services.AddScoped<RunEngine>();

        // WP-2: Rent charge run + proration (ADR-017 / ADR-019).
        services.AddScoped<IRunStrategy, RentRunStrategy>();

        // WP-3: Late-fee run (NC §42-46 clamp / ADR-019).
        services.AddScoped<IRunStrategy, LateFeeRunStrategy>();

        // WP-4: Owner disbursement run + folded management fee (ADR-018 / ADR-019).
        services.AddScoped<IRunStrategy, DisbursementRunStrategy>();

        return services;
    }
}
