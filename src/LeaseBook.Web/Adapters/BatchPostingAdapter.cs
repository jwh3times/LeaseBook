using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Operations.Contracts;
using LeaseBook.SharedKernel;

namespace LeaseBook.Web.Adapters;

/// <summary>
/// Host adapter (ADR-007 / ADR-019) implementing Operations' write-direction
/// <see cref="IBatchPosting"/> port. Translates intent DTOs into
/// <see cref="IAccountingEvents.PostAsync"/> calls, and Accounting's per-item exceptions into a
/// <see cref="PostOutcome"/>.
/// <para>
/// The adapter sits in the host because it crosses the ADR-007 module boundary — it references both
/// <c>Modules.Operations.Contracts</c> (the port) and <c>Modules.Accounting.Contracts</c> (the events
/// and their exceptions). Modules.Operations must not reference Accounting types.
/// </para>
/// <para>
/// <b>Exception translation belongs here, and only here.</b> This is the one type in the codebase
/// that can name both an Accounting exception and an Operations outcome. Doing it anywhere else
/// means matching on <c>ex.GetType().Name</c> against a string, which is what the run strategies used
/// to do in ten separate helpers — a link between two assemblies that a rename would have broken
/// silently, turning a classified money-path refusal into an unhandled failure that aborts the run.
/// </para>
/// <para>
/// The <c>SourceRef</c> carried on each intent threads through to the <c>journal_entries</c> row,
/// allowing the existing <c>(org_id, source_ref)</c> partial unique index to deduplicate repeat runs
/// without any additional index (ADR-019).
/// </para>
/// </summary>
internal sealed class BatchPostingAdapter(IAccountingEvents events) : IBatchPosting
{
    public async Task<PostOutcome> PostAsync(RunIntent intent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(intent);

        try
        {
            return intent switch
            {
                RentChargeIntent rent => await PostRentAsync(rent, ct),
                LateFeeIntent fee => await PostLateFeeAsync(fee, ct),
                DisbursementIntent disbursement => await PostDisbursementAsync(disbursement, ct),

                // Not exhaustiveness-checked by the compiler, so it fails loudly and immediately
                // rather than mis-routing: a new intent shape reaching here is a wiring bug, and the
                // first run that hits it says so by name.
                _ => throw new NotSupportedException(
                    $"No posting translation for intent type {intent.GetType().Name}. Add a branch " +
                    "here when a run type introduces a new intent shape."),
            };
        }
        catch (DuplicateSourceRefException)
        {
            // The run is being repeated. Idempotency is the (org_id, source_ref) partial unique
            // index, so this is the normal signal for "already done", not an error.
            return PostOutcome.Refused(PostStatus.DuplicateSourceRef);
        }
        catch (AccountPeriodLockedException)
        {
            return PostOutcome.Refused(PostStatus.PeriodLocked);
        }
        catch (PeriodClosedException)
        {
            return PostOutcome.Refused(PostStatus.PeriodClosed);
        }
        catch (ReserveFloorException)
        {
            return PostOutcome.Refused(PostStatus.ReserveFloor);
        }
    }

    private async Task<PostOutcome> PostRentAsync(RentChargeIntent i, CancellationToken ct) =>
        PostOutcome.Posted(await events.PostAsync(
            new RentCharged(
                i.TenantId, i.PropertyId, i.OwnerId, i.UnitId,
                new Money(i.Amount), i.Date, i.Description, i.SourceRef),
            ct));

    private async Task<PostOutcome> PostLateFeeAsync(LateFeeIntent i, CancellationToken ct) =>
        PostOutcome.Posted(await events.PostAsync(
            new FeeCharged(
                i.TenantId, i.PropertyId, i.OwnerId, i.UnitId,
                new Money(i.Amount), i.Date, FeeKind.Late, i.Description, i.SourceRef),
            ct));

    /// <summary>
    /// The management-fee assessment posts FIRST when non-zero: it reduces owner equity before the
    /// disbursement's reserve-floor guard runs, so the guard sees the balance the owner will actually
    /// be left with. A refusal from either leaves neither — both run inside the run's transaction.
    /// </summary>
    private async Task<PostOutcome> PostDisbursementAsync(DisbursementIntent i, CancellationToken ct)
    {
        Guid? feeEntryId = null;

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

        return PostOutcome.Posted(disbursementEntryId, feeEntryId);
    }
}
