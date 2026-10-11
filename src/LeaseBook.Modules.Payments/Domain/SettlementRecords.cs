using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Payments.Domain;

// A settlement is one processor payout and the bank evidence for it (ADR-053). Its items are the
// processor's own account of what the payout contains. Evidence is immutable once stored: status,
// reason and the journal links are the only things a later step writes. One exception, made once: a
// payout the processor reported apart from any bank (ADR-054) is stored with its lines and no bank
// evidence, and takes the bank's amount, date and reference when they arrive. A processor's later
// word that the payout failed is also written, once.
public sealed class PaymentSettlement : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid Generation { get; set; }
    public string Account { get; set; } = "";
    /// <summary>The mode of the host that stored it. A payout is found by account, mode and reference together.</summary>
    public string Mode { get; set; } = "Simulation";
    public string PayoutId { get; set; } = "";
    public string PayoutType { get; set; } = SettlementPayoutTypes.Standard;
    public Guid BankId { get; set; }
    public DateOnly BankDate { get; set; }
    /// <summary>What the bank shows for the payout. Negative when the payout is a debit. Zero until a bank has spoken.</summary>
    public decimal BankAmount { get; set; }
    /// <summary>
    /// What the processor says the payout comes to, when it said so apart from any bank evidence. Null
    /// for a payout that arrived with its bank evidence, where the bank amount is the only figure.
    /// </summary>
    public decimal? ProcessorAmount { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>The bank evidence's own reference. Empty while the payout has none: see <see cref="Banked"/>.</summary>
    public string EvidenceId { get; set; } = "";
    /// <summary>Whether a bank has said what it received for this payout. Nothing posts for one where it has not.</summary>
    public bool Banked => EvidenceId.Length > 0;
    /// <summary>When the processor said the payout failed, if it ever did. Kept whatever the status becomes.</summary>
    public DateTime? FailedAt { get; set; }
    public string Fingerprint { get; set; } = "";
    public string Status { get; set; } = SettlementStatuses.Received;
    public string? Reason { get; set; }
    /// <summary>The item an accounting rule refused on, when the reason came from one.</summary>
    public string? ReasonItem { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? PostedAt { get; set; }
    /// <summary>The administrator who posted a payout that needed a person; null when it posted by itself.</summary>
    public Guid? PostedBy { get; set; }
    public string? ReviewNote { get; set; }
    public DateTime? ReviewClosedAt { get; set; }
    public Guid? ReviewClosedBy { get; set; }
}

public sealed class PaymentSettlementItem : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid SettlementId { get; set; }
    /// <summary>The processor's reference for this line within the payout.</summary>
    public string Item { get; set; } = "";
    public string Kind { get; set; } = "";
    /// <summary>The payment this line is about, by its provider reference. Empty for a standalone fee.</summary>
    public string ProviderId { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal Fee { get; set; }
    /// <summary>What this line contributes to the bank amount. Negative for a return or a fee.</summary>
    public decimal Net { get; set; }
    public string Currency { get; set; } = "USD";
    /// <summary>The receipt or reversal this line posted.</summary>
    public Guid? EntryId { get; set; }
    /// <summary>The fee-difference entry this line posted, if the processor's fee was not the quoted fee.</summary>
    public Guid? FeeEntryId { get; set; }
}

public static class SettlementStatuses
{
    public const string Received = "Received";
    /// <summary>The processor's lines are stored and no bank has spoken. Nothing has posted and nothing can.</summary>
    public const string AwaitingBank = "AwaitingBank";
    public const string NeedsReview = "NeedsReview";
    public const string Posted = "Posted";
    public const string Closed = "Closed";
}

public static class SettlementPayoutTypes
{
    public const string Standard = "standard";
    /// <summary>A payout a person made at the processor. It has no list of what it covers, so it is never posted.</summary>
    public const string Manual = "manual";
}

/// <summary>The line kinds a payout can post. Any other kind is unsupported and sends the payout to review.</summary>
public static class SettlementItemKinds
{
    public const string Payment = "Payment";
    public const string Return = "Return";
    public const string Fee = "Fee";

    // Kinds an adapter stores for a person to read. None is among the three above, so none posts.
    public const string Dispute = "Dispute";
    public const string Refund = "Refund";
    public const string FailedDebit = "FailedDebit";
    public const string Unknown = "Unknown";
}

/// <summary>Why a payout waits, where the reason is the payout's own and not a line's or the ledger's.</summary>
public static class SettlementReasons
{
    /// <summary>The bank received another amount than the processor says it paid.</summary>
    public const string BankAmountMismatch = "bank_amount_mismatch";
    /// <summary>The processor says the payout failed. Nothing is reversed for it; a person decides.</summary>
    public const string PayoutFailed = "payout_failed";
    /// <summary>Asked to post, and no bank has said what it received.</summary>
    public const string BankEvidenceMissing = "bank_evidence_missing";
}
