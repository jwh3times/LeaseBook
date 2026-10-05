using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Accounting.Features.Posting.Events;

// Processor fee differences (ADR-053). A tenant paying online is quoted a convenience fee that covers
// the processor's fee, and is credited the full amount they paid toward their ledger. When the
// processor keeps a different fee from the one quoted, the bank receives more or less than the
// receipt posted, and that difference is the PM's — never an owner's or a tenant's. Like the bank
// adjustments (ADR-014) it moves only the PM's own held funds in that bank.

/// <summary>Which way a processor's actual fee differed from the fee quoted to the tenant.</summary>
public enum FeeDifferenceDirection
{
    /// <summary>The processor kept more than was quoted: the bank received less than the receipt.</summary>
    Shortfall,

    /// <summary>The processor kept less than was quoted: the bank received more than the receipt.</summary>
    Surplus,
}

/// <summary>
/// The amount by which a processor's fee differed from the quoted fee, borne by or credited to the PM's
/// held funds in the bank that received the payout. A shortfall is guarded: it posts only while held
/// PM fees stay at or above zero (<c>pm_fees_insufficient</c>).
/// </summary>
public sealed record ProcessorFeeDifference(
    Money Amount, FeeDifferenceDirection Direction, DateOnly Date, Guid BankAccountId, string Description,
    string? SourceRef = null, string? InternalNote = null) : AccountingEvent;
