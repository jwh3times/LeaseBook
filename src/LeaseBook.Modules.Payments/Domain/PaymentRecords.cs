using LeaseBook.SharedKernel;

namespace LeaseBook.Modules.Payments.Domain;

// The operation is also the durable outbox/retry cursor. Immutable observations and effects are
// separate: updating a worker lease can never rewrite evidence of money movement.
public sealed class PaymentFixture : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid BankId { get; set; }
    public string Account { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

/// <summary>The online payment methods a fee rule exists for. The values are stored and sent on the wire.</summary>
public static class PaymentMethods
{
    public const string Card = "card";
    public const string Ach = "ach";
    public static readonly string[] All = [Card, Ach];
}

public sealed class PaymentOperation : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid TenantId { get; set; }
    public Guid UserId { get; set; }
    public Guid Key { get; set; }
    public Guid Generation { get; set; }
    public Guid BankId { get; set; }
    public string Account { get; set; } = "";
    // The ledger amount (ADR-053): what the tenant pays toward their ledger, and the only amount they
    // are credited. The convenience fee quoted at confirmation rides on top of it and is never posted.
    public decimal Amount { get; set; }
    public decimal QuotedFee { get; set; }
    /// <summary>What the tenant's card or bank is charged. Derived, so it cannot drift from its parts.</summary>
    public decimal ChargedAmount => Amount + QuotedFee;
    public string Method { get; set; } = PaymentMethods.Ach;
    public string Currency { get; set; } = "USD";
    public string Fingerprint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    /// <summary>When the processor reported the collection succeeded. The receipt is dated on the bank date instead.</summary>
    public DateTime? PaidAt { get; set; }
    public string Status { get; set; } = "Requested";
    public string? Reason { get; set; }
    public string? ProviderId { get; set; }
    public Guid? JournalId { get; set; }
    public int ProcessedCount { get; set; }
    public int Attempts { get; set; }
    public DateTime DueAt { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LeaseUntil { get; set; }
    public Guid? LeaseClaimId { get; set; }
    // A staff member closed the review without a posting (#490). An observation arriving later makes
    // the operation claimable again, and the reducer reopens the review; the note stays as history.
    public string? ReviewNote { get; set; }
    public DateTime? ReviewClosedAt { get; set; }
    public Guid? ReviewClosedBy { get; set; }
}

public sealed class PaymentObservation : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public string EventId { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public Guid Generation { get; set; }
    public string Account { get; set; } = "";
    public string Mode { get; set; } = "Simulation";
    public string Kind { get; set; } = "";
    public decimal Gross { get; set; }
    public decimal Fee { get; set; }
    public decimal Net { get; set; }
    public string Currency { get; set; } = "USD";
    public Guid BankId { get; set; }
    public DateOnly BankDate { get; set; }
    public string EvidenceId { get; set; } = "";
    public string PayoutId { get; set; } = "";
    public bool Complete { get; set; }
    public DateTime ObservedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Fingerprint { get; set; } = "";
}

public sealed class PaymentEffect : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid OperationId { get; set; }
    public Guid ObservationId { get; set; }
    public Guid JournalId { get; set; }
    // "Receipt" or "Return"; unique per operation, so each posts at most once.
    public string Kind { get; set; } = "Receipt";
    public DateTime CreatedAt { get; set; }
}

// Durable synthetic provider storage. This is not the application's dispatch result: it commits
// independently so a crash after provider acceptance can be recovered by Lookup.
public sealed class SimulatedCollection : IOrgScoped
{
    public Guid Id { get; set; }
    public Guid OrgId { get; set; }
    public Guid Generation { get; set; }
    public string Fingerprint { get; set; } = "";
    public string ProviderId { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}
