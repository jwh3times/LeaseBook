using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// Posts cutover opening positions (P27 / §C.3 BalanceForward). Deliberately separate from
/// <see cref="IAccountingEvents"/>' business-event catalog — it takes an arbitrary balanced line set
/// (every line basis <c>both</c>) and is consumed only by seed/import code (WP-08, M7), never by the
/// product's event flows.
/// </summary>
public interface IBalanceForward
{
    Task<Guid> PostAsync(BalanceForwardRequest request, CancellationToken ct);

    /// <summary>
    /// Posts ONE imported opening position (M7/ADR-020): the real-account leg plus an equal-and-opposite
    /// <c>migration_clearing</c> contra leg, both tagged <paramref name="req"/>.Basis, dated at cutover and
    /// keyed by <c>req.SourceRef</c> (re-import idempotent via the (org_id, source_ref) index). Unlike the
    /// batched method, the caller does NOT pre-balance a set — each position self-balances against clearing,
    /// so a non-tying import accumulates a clearing residual instead of failing to post.
    /// </summary>
    Task<Guid> PostOpeningPositionAsync(OpeningPositionRequest req, CancellationToken ct);
}

public sealed record OpeningPositionRequest(
    string AccountCode, Money? Debit, Money? Credit, EntryBasis Basis,
    DateOnly Cutover, string SourceRef,
    Guid? PropertyId = null, Guid? UnitId = null, Guid? OwnerId = null,
    Guid? TenantId = null, Guid? BankAccountId = null, string? Memo = null);

/// <param name="Date">Cutover date (the opening positions' entry date).</param>
/// <param name="Lines">The opening positions; must balance per basis (they are all <c>both</c>).</param>
/// <param name="Description">Ledger display text.</param>
/// <param name="SourceRef">Optional idempotency key.</param>
public sealed record BalanceForwardRequest(
    DateOnly Date,
    IReadOnlyList<BalanceForwardLine> Lines,
    string? Description = null,
    string? SourceRef = null);

/// <summary>One opening position. Exactly one of <see cref="Debit"/>/<see cref="Credit"/> is set.</summary>
public sealed record BalanceForwardLine(
    string AccountCode,
    Money? Debit,
    Money? Credit,
    Guid? PropertyId = null,
    Guid? UnitId = null,
    Guid? OwnerId = null,
    Guid? TenantId = null,
    Guid? BankAccountId = null,
    string? Memo = null);
