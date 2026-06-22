using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Modules.Reporting;

/// <summary>
/// Registers the Reporting module's services with the host container. Called once from
/// <c>Program.cs</c>. CQRS handlers and validators are auto-discovered via Scrutor.
/// Cross-module ports (<see cref="Contracts.IStatementNames"/>, <see cref="Contracts.IPmBranding"/>,
/// <see cref="Contracts.IReconciliationSnapshots"/>, and the pre-existing Accounting
/// <c>IOwnerStatementData</c>) are implemented by host adapters registered in <c>Program.cs</c>.
/// The composition services (<c>StatementAssembler</c>, <c>ReportPreviewService</c>) live in the
/// host so they can cross module boundaries via <c>ISender</c>.
/// </summary>
public static class ReportingModuleServiceCollectionExtensions
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        // No module-level services to register yet — CQRS handlers are auto-discovered.
        return services;
    }
}
