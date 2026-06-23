using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / ADR-019) that implements Operations' write-direction
/// <see cref="IBatchPosting"/> port. Translates <c>Modules.Operations</c> intent DTOs into
/// <see cref="IAccountingEvents.PostAsync"/> calls.
/// <para>
/// The adapter sits in the host because it crosses the ADR-007 module boundary — it references
/// both <c>Modules.Operations.Contracts</c> (the port) and <c>Modules.Accounting.Contracts</c>
/// (the posting events). Modules.Operations must not reference Accounting types.
/// </para>
/// <para>
/// The <c>SourceRef</c> carried on each intent threads through to the <c>journal_entries</c>
/// row, allowing the existing <c>(org_id, source_ref)</c> partial unique index to deduplicate
/// repeat runs without any additional index (ADR-019).
/// </para>
/// </summary>
internal sealed class BatchPostingAdapter(IAccountingEvents events) : IBatchPosting
{
    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Guid>> PostRentChargesAsync(
        IReadOnlyList<RentChargeIntent> intents, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Guid>(intents.Count);
        foreach (var i in intents)
        {
            var entryId = await events.PostAsync(
                new RentCharged(
                    i.TenantId, i.PropertyId, i.OwnerId, i.UnitId,
                    new Money(i.Amount), i.Date, i.Description, i.SourceRef),
                ct);
            map[i.LeaseId] = entryId;
        }

        return map;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, Guid>> PostLateFeesAsync(
        IReadOnlyList<LateFeeIntent> intents, CancellationToken ct)
    {
        var map = new Dictionary<Guid, Guid>(intents.Count);
        foreach (var i in intents)
        {
            var entryId = await events.PostAsync(
                new FeeCharged(
                    i.TenantId, i.PropertyId, i.OwnerId, i.UnitId,
                    new Money(i.Amount), i.Date, FeeKind.Late, i.Description, i.SourceRef),
                ct);
            map[i.LeaseId] = entryId;
        }

        return map;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyDictionary<Guid, DisbursementPostingResult>> PostDisbursementsAsync(
        IReadOnlyList<DisbursementIntent> intents, CancellationToken ct)
    {
        var map = new Dictionary<Guid, DisbursementPostingResult>(intents.Count);
        foreach (var i in intents)
        {
            Guid? feeEntryId = null;

            // Post management-fee assessment first (when non-zero); this reduces owner equity before
            // the disbursement guard checks the reserve floor.
            if (i.MgmtFee > 0m)
            {
                feeEntryId = await events.PostAsync(
                    new ManagementFeeAssessed(
                        i.OwnerId, i.PropertyId,
                        new Money(i.MgmtFee), i.Date, i.OperatingBankId,
                        i.Description, i.FeeSourceRef),
                    ct);
            }

            var disbursementEntryId = await events.PostAsync(
                new OwnerDisbursed(
                    i.OwnerId,
                    new Money(i.DisburseAmount), i.Date, i.OperatingBankId,
                    i.Description, i.DisburseSourceRef,
                    Reserve: new Money(i.Reserve)),
                ct);

            map[i.OwnerId] = new DisbursementPostingResult(feeEntryId, disbursementEntryId);
        }

        return map;
    }
}
