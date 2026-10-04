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
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Fingerprint { get; set; } = "";
    public DateTime CreatedAt { get; set; }
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
