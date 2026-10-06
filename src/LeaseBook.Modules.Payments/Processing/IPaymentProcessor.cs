namespace LeaseBook.Modules.Payments.Processing;

public sealed record FixtureBinding(Guid OrgId, Guid Generation, Guid BankId, string Account);
public sealed record ProcessorRequest(FixtureBinding Binding, Guid OperationId, string Fingerprint);
public sealed record ProcessorResult(string Outcome, string? ProviderId);
public sealed record ProcessorObservation(string EventId, string ProviderId, string Account, string Mode,
    Guid Generation, string Kind, decimal Gross, decimal Fee, decimal Net, string Currency, Guid BankId,
    DateOnly BankDate, string EvidenceId, string PayoutId, bool Complete, DateTime ObservedAt);

public interface IPaymentProcessor
{
    Task<ProcessorResult> SubmitAsync(ProcessorRequest request, CancellationToken ct);
    Task<ProcessorResult> LookupAsync(ProcessorRequest request, CancellationToken ct);
    ProcessorObservation? VerifyAndNormalize(byte[] rawBody, string signature);

    /// <summary>Verifies payout evidence and returns it, or null when it cannot be authenticated or read.</summary>
    ProcessorSettlement? VerifyAndNormalizeSettlement(byte[] rawBody, string signature);
}
