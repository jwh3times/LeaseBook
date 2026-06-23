namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Read-direction cross-module port (ADR-007 / ADR-019). Operations declares the interface;
/// the host adapter (<c>PostedSourceRefsAdapter</c>) implements it by dispatching an Accounting
/// query via <see cref="LeaseBook.SharedKernel.Cqrs.ISender"/>.
/// <para>
/// Used by <see cref="RentRunStrategy.PreviewAsync"/> to flag which leases already have a
/// <c>RentCharged</c> entry for the period (<c>AlreadyDone</c> flag) without reading Accounting's
/// <c>journal_entries</c> table directly from the Operations module (ADR-007 boundary rule).
/// </para>
/// </summary>
public interface IPostedSourceRefs
{
    /// <summary>
    /// Returns the subset of <paramref name="candidateKeys"/> that already exist as
    /// <c>source_ref</c> values in <c>journal_entries</c>.
    /// </summary>
    Task<IReadOnlySet<string>> GetExistingAsync(
        IReadOnlyList<string> candidateKeys, CancellationToken ct);
}
