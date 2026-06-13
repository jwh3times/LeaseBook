using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Contracts;

/// <summary>
/// A request to post one balanced journal entry (§C.4). Built by posting templates (WP-05), the
/// reversal service, and the seeder — never bound directly from HTTP in M1. Lines reference accounts by
/// stable <see cref="PostLineRequest.AccountCode"/>; the posting service resolves them through the
/// org-filtered context (so a cross-org code is invisible, not an FK that bypasses RLS — pitfall M-E5)
/// and denormalizes each account's class onto the line itself.
/// </summary>
public sealed record PostEntryRequest(
    DateOnly EntryDate,
    string EventType,
    string? EventSubtype,
    string? Description,
    string? SourceRef,
    IReadOnlyList<PostLineRequest> Lines,
    Guid? ReversesEntryId = null);

/// <summary>
/// One requested line. Exactly one of <see cref="Debit"/>/<see cref="Credit"/> must be set and
/// strictly positive. <c>account_class</c> is intentionally absent — the service sets it from the
/// resolved account, never from the caller (pitfall M-E4).
/// </summary>
public sealed record PostLineRequest(
    string AccountCode,
    Money? Debit,
    Money? Credit,
    EntryBasis Basis,
    Guid? PropertyId = null,
    Guid? UnitId = null,
    Guid? OwnerId = null,
    Guid? TenantId = null,
    Guid? BankAccountId = null,
    string? Memo = null);
