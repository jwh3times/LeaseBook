using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Features.Posting.Events;

// M4 bank-adjustment events (P65 / ADR-014). The bank-only lines a reconciliation needs: a service
// charge, interest, and a transfer between two of the org's bank accounts. All three are modeled as
// movements of the PM's <b>own held funds</b> (pm_income tagged to the bank), never owner or deposit
// money — so each affected bank's trust equation stays balanced and owners are structurally isolated.
// These are the only NEW posting templates in M4 (owner/vendor/fee-sweep runs are M6).

/// <summary>A bank service charge: the PM's held funds in that bank cover it (held fees ↓, bank ↓).</summary>
public sealed record BankFeeCharged(
    Money Amount, DateOnly Date, Guid BankAccountId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>Interest paid into a bank: accrues to the PM's held position (bank ↑, held fees ↑).</summary>
public sealed record InterestEarned(
    Money Amount, DateOnly Date, Guid BankAccountId, string Description, string? SourceRef = null) : AccountingEvent;

/// <summary>Moves the PM's own held funds between two of the org's bank accounts (cash + attribution move together).</summary>
public sealed record TrustTransfer(
    Money Amount, DateOnly Date, Guid FromBankId, Guid ToBankId, string Description, string? SourceRef = null) : AccountingEvent;
