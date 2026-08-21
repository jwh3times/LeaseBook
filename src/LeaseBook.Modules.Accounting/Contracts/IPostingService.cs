namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// The <b>only</b> write path to the journal (CLAUDE.md). Validates organization context, account existence,
/// per-line shape, per-basis balance, the PM-income dimension rule, open-period, and source_ref
/// idempotency — each failure a typed <see cref="AccountingDomainException"/>, never a silent fix-up —
/// then persists the header + lines atomically inside the ambient org transaction. Returns the new
/// entry id.
/// <para>
/// <b>Internal.</b> Raw line-level posting is not part of Accounting's contract — the module's public
/// write surface is <see cref="IAccountingEvents"/> (business events), <see cref="IReversalService"/>
/// (corrections) and <see cref="IBalanceForward"/> (cutover), each of which routes here after applying
/// its own guards. Posting arbitrary lines from another module would bypass the posting-template
/// catalog (ADR-006). Tests reach it through <c>InternalsVisibleTo</c>, which is the point: the
/// validator's rejections can only be exercised with inputs no template would ever emit.
/// </para>
/// </summary>
internal interface IPostingService
{
    Task<Guid> PostAsync(PostEntryRequest request, CancellationToken ct);
}
