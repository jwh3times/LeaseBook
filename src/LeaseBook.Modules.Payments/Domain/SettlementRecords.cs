using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Payments.Domain;

// A settlement is one processor payout and the bank evidence for it (ADR-053). Its items are the
// processor's own account of what the payout contains. Evidence is immutable once stored: status,
// reason and the journal links are the only things a later step writes.
public sealed class PaymentSettlement : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid Generation { get; set; }
    public string Account { get; set; } = "";
    public string PayoutId { get; set; } = "";
    public string PayoutType { get; set; } = SettlementPayoutTypes.Standard;
    public Guid BankId { get; set; }
    public DateOnly BankDate { get; set; }
    /// <summary>What the bank shows for the payout. Negative when the payout is a debit.</summary>
    public decimal BankAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string EvidenceId { get; set; } = "";
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
    public const string NeedsReview = "NeedsReview";
    public const string Posted = "Posted";
    public const string Closed = "Closed";
}

public static class SettlementPayoutTypes
{
    public const string Standard = "standard";
}

/// <summary>The line kinds a payout can post. Any other kind is unsupported and sends the payout to review.</summary>
public static class SettlementItemKinds
{
    public const string Payment = "Payment";
    public const string Return = "Return";
    public const string Fee = "Fee";
}
