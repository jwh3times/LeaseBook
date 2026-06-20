using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.Modules.Accounting.Provisioning;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Accounting;

/// <summary>
/// Registers the Accounting module's services with the host container. Called once from
/// <c>Program.cs</c>. Services depend on the ambient <c>DbContext</c> (the host registers it as the
/// base type) + <c>ITenantContext</c>; later WPs append the posting engine and event catalog here.
/// </summary>
public static class AccountingModuleServiceCollectionExtensions
{
    public static IServiceCollection AddAccountingModule(this IServiceCollection services)
    {
        services.AddScoped<IChartOfAccounts, ChartOfAccounts>();
        services.AddScoped<IAccountingPeriods, AccountingPeriods>();
        services.AddScoped<IPostingService, PostingService>();
        services.AddScoped<IReversalService, ReversalService>();
        services.AddScoped<IPostingLock, PostingLock>();
        services.AddScoped<IReconciliationLock, ReconciliationLock>();

        // One event-template instance behind both surfaces (the catalog and the cutover-only contract).
        services.AddScoped<AccountingEventService>();
        services.AddScoped<IAccountingEvents>(sp => sp.GetRequiredService<AccountingEventService>());
        services.AddScoped<IBalanceForward>(sp => sp.GetRequiredService<AccountingEventService>());

        services.AddScoped<IInvariantChecks, InvariantChecks>();
        return services;
    }
}
