using System.Text.Json.Serialization;
using LeaseBook.Modules.Payments.Domain;

namespace LeaseBook.Modules.Payments.Processing;

/// <summary>The payment modes a host can run in. The value is stored on every notice kept.</summary>
public static class PaymentModes
{
    public const string Disabled = "Disabled";
    public const string Simulation = "Simulation";
    public const string StripeSandbox = "StripeSandbox";
}

public sealed record FixtureBinding(Guid OrgId, Guid Generation, Guid BankId, string Account)
{
    /// <summary>
    /// The mode of the host that holds this binding. A notice is for this binding only when it says the
    /// same mode. The host sets it from its own settings; it is not part of a fixture manifest.
    /// </summary>
    [JsonIgnore]
    public string Mode { get; init; } = PaymentModes.Simulation;

    /// <summary>
    /// The processor's test payment method a card payment is collected with, and the one for ACH. Only
    /// a sandbox host reads or admits them, and only in the shape of a documented test token, which a
    /// payer's saved method can never have. Not written to a manifest: the defaults are the ones that succeed.
    /// </summary>
    [JsonIgnore]
    public string CardPaymentMethod { get; init; } = "pm_card_visa";
    [JsonIgnore]
    public string AchPaymentMethod { get; init; } = "pm_usBankAccount_success";
}

/// <summary>
/// What a processor needs to collect one payment, and to find it again. <see cref="Amount"/> is the
/// charge: the ledger amount plus the fee the tenant was quoted. <see cref="CreatedAt"/> bounds a
/// search for the collection when the processor no longer remembers the request.
/// <see cref="ProviderId"/> is the processor's own reference, once LeaseBook has stored one: a lookup
/// then asks for that payment and searches for nothing. An adapter that finds its collections by the
/// operation alone may ignore it. Nothing here identifies the tenant.
/// </summary>
public sealed record ProcessorRequest(FixtureBinding Binding, Guid OperationId, string Fingerprint,
    decimal Amount, string Currency, string Method, DateTime CreatedAt, string? ProviderId = null)
{
    public static ProcessorRequest For(FixtureBinding binding, PaymentOperation operation) => new(
        binding, operation.Id, operation.Fingerprint, operation.ChargedAmount, operation.Currency, operation.Method,
        operation.CreatedAt, operation.ProviderId);
}

public sealed record ProcessorResult(string Outcome, string? ProviderId);

/// <summary>
/// A callback body that proved authentic. Nothing in it has been read yet. Only an adapter's
/// <see cref="IPaymentProcessor.Authenticate"/> creates one, which an architecture test enforces; it
/// is a class and not a record so that no copy can be made with another body.
/// </summary>
public sealed class ProcessorNotice(byte[] body)
{
    public byte[] Body { get; } = body;
}

/// <summary>
/// What reading a notice came to. A <see cref="Value"/>; or <see cref="Ignored"/>, for a notice that is
/// authentic but not this host's to act on, which is acknowledged and dropped; or neither, for one
/// that cannot be read, which is refused so that the sender does not count it as delivered.
/// </summary>
public readonly record struct ProcessorRead<T>(T? Value, bool Ignored) where T : class
{
    public static ProcessorRead<T> NotOurs => new(null, true);

    /// <summary>The content read, or unreadable when there is none.</summary>
    public static ProcessorRead<T> Of(T? value) => new(value, false);
}

public sealed record ProcessorObservation(string EventId, string ProviderId, string Account, string Mode,
    Guid Generation, string Kind, decimal Gross, decimal Fee, decimal Net, string Currency, Guid BankId,
    DateOnly BankDate, string EvidenceId, string PayoutId, bool Complete, DateTime ObservedAt);

/// <summary>
/// What asking a processor again came to: the observations to keep, how many events it listed, and
/// how many of those could not be read.
/// </summary>
public sealed record ProcessorRecovery(IReadOnlyList<ProcessorObservation> Observations, int Listed, int Unreadable)
{
    public static ProcessorRecovery Nothing { get; } = new([], 0, 0);
}

public interface IPaymentProcessor
{
    Task<ProcessorResult> SubmitAsync(ProcessorRequest request, CancellationToken ct);
    Task<ProcessorResult> LookupAsync(ProcessorRequest request, CancellationToken ct);

    /// <summary>
    /// Proves a callback body came from the processor, or returns null. Does no I/O and runs outside
    /// any organization transaction. Nothing in the body is trusted before this passes.
    /// </summary>
    ProcessorNotice? Authenticate(byte[] rawBody, string signature);

    /// <summary>
    /// Reads an authenticated notice about one payment, fetching from the processor whatever the notice
    /// does not carry. It may therefore do I/O, and like <see cref="Authenticate"/> it is called outside
    /// any organization transaction. The generation and bank on the result are the adapter's to supply:
    /// an adapter whose notices do not carry them takes them from its own binding for the notice's
    /// account, never from the notice, and answers <see cref="ProcessorRead{T}.NotOurs"/> when it has
    /// no binding for that account or the notice is of another kind.
    /// </summary>
    Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct);

    /// <summary>Reads an authenticated notice as payout evidence, on the same terms.</summary>
    Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct);

    /// <summary>
    /// Asks the processor what it has said about one binding's payments since a time, for a notice
    /// that was never delivered. It does I/O and is called outside any organization transaction. What
    /// it returns is read as a delivered notice is read and is kept the same way, so one the host
    /// already holds is dropped there. Whatever cannot be answered in full is thrown, never cut
    /// short: a caller takes a result to mean that everything since that time was asked for. An
    /// adapter whose notices are always delivered returns <see cref="ProcessorRecovery.Nothing"/>.
    /// </summary>
    Task<ProcessorRecovery> RecoverObservationsAsync(FixtureBinding binding, DateTime since, CancellationToken ct);
}
