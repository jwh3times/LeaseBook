using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Banking.Contracts;
using LeaseBook.SharedKernel.Cqrs;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / P68) for Banking's <see cref="IBankClearing"/> port. Delegates to Accounting's
/// <see cref="ApplyClearances"/> command via <see cref="ISender"/> (the sole clearance writer besides
/// reconcile finalize). Banking never writes <c>bank_line_status</c> directly.
/// </summary>
internal sealed class BankClearingAdapter(ISender sender) : IBankClearing
{
    public async Task ApplyClearancesAsync(IReadOnlyCollection<Guid> journalLineIds, CancellationToken ct)
    {
        if (journalLineIds.Count == 0)
        {
            return;
        }

        await sender.Send(new ApplyClearances(journalLineIds), ct);
    }
}
