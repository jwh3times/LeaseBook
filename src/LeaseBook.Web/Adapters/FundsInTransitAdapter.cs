using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Web.Calendar;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007) for the Operations module's <see cref="IFundsInTransit"/> port. Dispatches
/// the Payments <see cref="GetFundsInTransit"/> query on the ambient organization transaction and maps
/// it to Operations' own type. An organization with no online payments simply has no rows.
/// </summary>
internal sealed class FundsInTransitAdapter(ISender sender, HostOrgCalendar calendar) : IFundsInTransit
{
    public async Task<IReadOnlyDictionary<Guid, TenantFundsInTransit>> GetAsync(
        IReadOnlyList<Guid> tenantIds, CancellationToken ct)
    {
        var rows = await sender.Query(new GetFundsInTransit(tenantIds), ct);
        if (rows.Count == 0)
        {
            return new Dictionary<Guid, TenantFundsInTransit>();
        }

        // PaidAt is a UTC instant; the day it was paid is the organization's day for it (#642).
        var zone = await calendar.GetAsync(ct);
        return rows.ToDictionary(
            x => x.TenantId,
            x => new TenantFundsInTransit(
                x.Amount,
                DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(x.PaidAt, DateTimeKind.Utc), zone))));
    }
}
