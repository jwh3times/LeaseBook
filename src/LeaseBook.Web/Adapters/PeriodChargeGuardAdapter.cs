using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for the Operations module's <see cref="IPeriodChargeGuard"/> port.
/// Dispatches the Accounting <see cref="GetTenantsChargedInPeriod"/> query via
/// <see cref="ISender"/> — Accounting reads its own <c>journal_entries</c>/<c>journal_lines</c>
/// tables; no cross-module SQL from Operations.
/// </summary>
internal sealed class PeriodChargeGuardAdapter(ISender sender) : IPeriodChargeGuard
{
    public async Task<IReadOnlySet<Guid>> GetChargedTenantsAsync(
        string eventType,
        string? eventSubtype,
        int year,
        int month,
        IReadOnlyList<Guid> tenantIds,
        CancellationToken ct)
    {
        var response = await sender.Query(
            new GetTenantsChargedInPeriod(eventType, eventSubtype, year, month, tenantIds), ct);
        return response.TenantIds;
    }
}
