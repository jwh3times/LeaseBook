using System.Globalization;
using System.Linq.Expressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Processing;

public sealed class PaymentConflictException : Exception;
public sealed class PaymentUnavailableException : Exception;
/// <summary>The fee the tenant was shown is not the fee the organization's rule gives now.</summary>
public sealed class PaymentFeeQuoteChangedException : Exception;
public sealed record PaymentReturnDecision(PaymentOperation Operation, string? Refusal);
public sealed record WaitingPayment(Guid Id, DateTime CreatedAt);

/// <summary>All methods run on the caller's org transaction. No provider I/O occurs here.</summary>
public sealed class PaymentEngine(DbContext db, IOrgContext org, TimeProvider clock,
    IPaymentLedger ledger, IPaymentEligibility eligibility, IPaymentFeeRules feeRules)
{
    public static readonly int[] RetrySeconds = [1, 5, 30, 120, 600];
    public static readonly string[] EventKinds = ["Processing", "Succeeded", "Available", "PayoutPending",
        "PayoutPaid", "PayoutFailed", "Failed", "BankCredit", "Return", "Refund", "Dispute"];
    /// <summary>
    /// The kind of a fact that disagrees with what LeaseBook holds. The engine gives it to a notice
    /// whose event id it already holds with other content. The Stripe sandbox adapter may also report
    /// it, for an event about its own payment that names other money than was charged. Not among
    /// <see cref="EventKinds"/>: it says nothing of how a payment went, and no driver emits it.
    /// </summary>
    public const string ConflictKind = "Conflict";
    /// <summary>How long a payment may wait for its outcome before a person is asked to look.</summary>
    public static readonly TimeSpan OutcomeOverdueAfter = TimeSpan.FromDays(7);
    private static readonly Expression<Func<PaymentOperation, bool>> Waiting =
        x => x.Status == "Processing" && x.ProviderId != null && x.PaidAt == null;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task RequireFixtureAsync(FixtureBinding binding, CancellationToken ct)
    {
        if (org.OrgId != binding.OrgId || db.Database.CurrentTransaction is null
            || !await db.Set<PaymentFixture>().AnyAsync(f => f.Id == binding.Generation
                && f.BankId == binding.BankId && f.Account == binding.Account, ct))
        { throw new PaymentUnavailableException(); }
    }

    // Lock names include org identity. The hash is only a mutex, never an authorization decision.
    public Task LockAsync(string key, CancellationToken ct)
    {
        if (org.OrgId is null || db.Database.CurrentTransaction is null)
        { throw new InvalidOperationException("Payment work requires an organization transaction."); }
        return db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({org.OrgId + ":payment:" + key}, 0))", ct);
    }

    /// <summary>The fee and total a tenant would be charged for a ledger amount, under the organization's rule today.</summary>
    public async Task<FeeQuote> QuoteAsync(FixtureBinding binding, decimal amount, string method, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        return (await feeRules.ReadAsync(ct)).TryGetValue(method, out var rule)
            ? rule.Quote(amount) : throw new PaymentUnavailableException();
    }

    public async Task<PaymentOperation> SubmitAsync(FixtureBinding binding, Guid tenantId, Guid userId,
        Guid key, decimal amount, string currency, string method, decimal quotedFee, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync("request:" + userId + ":" + key, ct);
        // The fee in the fingerprint is the one the tenant confirmed, not one recomputed here: a replay
        // of the same request must find itself even if the organization's rule has changed since.
        var fingerprint = Hash(string.Join('|', tenantId, amount.ToString("0.00", CultureInfo.InvariantCulture), currency,
            binding.Generation, binding.BankId, binding.Account, method,
            quotedFee.ToString("0.00", CultureInfo.InvariantCulture)));
        var existing = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.UserId == userId && x.Key == key, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint) { throw new PaymentConflictException(); }
            return existing;
        }
        // The tenant is charged what they were shown or nothing at all. If the rule moved between the
        // quote and the confirmation, they must see the new fee before anything is requested.
        if ((await QuoteAsync(binding, amount, method, ct)).Fee != quotedFee) { throw new PaymentFeeQuoteChangedException(); }
        var request = new PaymentEligibilityRequest(tenantId, binding.BankId, DateOnly.FromDateTime(Now));
        if (!(await eligibility.ReadAsync([request], ct)).GetValueOrDefault(request))
        { throw new PaymentUnavailableException(); }
        var operation = new PaymentOperation
        {
            Id = UuidV7.NewId(),
            TenantId = tenantId,
            UserId = userId,
            Key = key,
            Generation = binding.Generation,
            BankId = binding.BankId,
            Account = binding.Account,
            Amount = amount,
            QuotedFee = quotedFee,
            Method = method,
            Currency = currency,
            Fingerprint = fingerprint,
            DueAt = Now,
        };
        db.Add(operation);
        await db.SaveChangesAsync(ct);
        return operation;
    }

    public async Task ReceiveAsync(FixtureBinding binding, ProcessorObservation observation, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        if (observation.Generation != binding.Generation || observation.Account != binding.Account
            || observation.Mode != binding.Mode) { throw new PaymentUnavailableException(); }
        await LockAsync("event:" + observation.EventId, ct);
        var fingerprint = Hash(JsonSerializer.Serialize(observation));
        var existing = await db.Set<PaymentObservation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Account == binding.Account && x.Mode == binding.Mode && x.EventId == observation.EventId, ct);
        if (existing?.Fingerprint == fingerprint) { return; }
        // Shared with CompleteAsync, which is where an operation first takes this reference. Without
        // it the two can pass each other: this finds no operation to wake while that finds no fact to
        // judge, and the fact waits with nothing left to wake the operation. Whichever takes the lock
        // second reads after the first has committed. Order: event, reference, then operation.
        var providerId = existing?.ProviderId ?? observation.ProviderId;
        await LockAsync("reference:" + providerId, ct);
        // Preserve the original event. Authenticated reuse with altered content is separate evidence.
        var eventId = existing is null ? observation.EventId : "conflict:" + fingerprint;
        if (await db.Set<PaymentObservation>().AnyAsync(x => x.EventId == eventId && x.Account == binding.Account, ct)) { return; }
        db.Add(new PaymentObservation
        {
            Id = UuidV7.NewId(),
            EventId = eventId,
            ProviderId = providerId,
            Generation = observation.Generation,
            Account = observation.Account,
            Mode = observation.Mode,
            Kind = existing is null ? observation.Kind : ConflictKind,
            Gross = observation.Gross,
            Fee = observation.Fee,
            Net = observation.Net,
            Currency = observation.Currency,
            BankId = observation.BankId,
            BankDate = observation.BankDate,
            EvidenceId = observation.EvidenceId,
            PayoutId = observation.PayoutId,
            Complete = observation.Complete,
            ObservedAt = observation.ObservedAt,
            Fingerprint = fingerprint,
        });
        await db.SaveChangesAsync(ct);
        var operationId = await db.Set<PaymentOperation>().Where(x => x.ProviderId == providerId)
            .Select(x => (Guid?)x.Id).SingleOrDefaultAsync(ct);
        if (operationId is { } id)
        {
            await LockAsync(id.ToString(), ct);
            var operation = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == id, ct);
            operation.DueAt = Now;
            await db.SaveChangesAsync(ct);
        }
    }

    public async Task<PaymentOperation?> ClaimAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null || op.DueAt > Now || op.LeaseUntil > Now) { return null; }
        MatchBinding(op, binding);
        var count = op.ProviderId is null ? 0 : await db.Set<PaymentObservation>().CountAsync(x => x.ProviderId == op.ProviderId, ct);
        if (op.Status is "Settled" or "Failed" or "NeedsReview" or "Returned" or "ReviewClosed"
            && count == op.ProcessedCount) { return null; }
        op.LeaseClaimId = UuidV7.NewId(); op.LeaseUntil = Now.AddSeconds(30); op.LastAttemptAt = Now;
        await db.SaveChangesAsync(ct);
        return op;
    }

    public async Task CompleteAsync(FixtureBinding binding, Guid id, Guid leaseClaimId, ProcessorResult provider, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        // Shared with ReceiveAsync, and taken before the operation's own lock as it is there. A fact
        // for this reference is then either committed before the facts are read below, or stored
        // after this commits, when its receipt finds the operation by the reference and wakes it.
        if (!string.IsNullOrWhiteSpace(provider.ProviderId)) { await LockAsync("reference:" + provider.ProviderId, ct); }
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == id, ct);
        MatchBinding(op, binding);
        if (op.LeaseClaimId != leaseClaimId) { return; }
        if (provider.Outcome != "Accepted" || string.IsNullOrWhiteSpace(provider.ProviderId))
        { throw new IOException("The simulated provider outcome is uncertain."); }
        if (op.ProviderId is not null && op.ProviderId != provider.ProviderId) { throw new PaymentConflictException(); }
        op.ProviderId = provider.ProviderId;
        var facts = await db.Set<PaymentObservation>().AsNoTracking().Where(x => x.ProviderId == op.ProviderId).ToListAsync(ct);
        var effects = await db.Set<PaymentEffect>().AsNoTracking().Where(x => x.OperationId == op.Id).ToListAsync(ct);
        // A payout batch may have receipted this payment with a fee difference, which is what a batch is
        // for. Single-payment credit evidence for it is then the same money seen again, not a settlement
        // to judge by the clean-item rule: it neither posts nor sends a correctly settled payment to review.
        var credits = effects.Any(x => x.Kind == "Receipt" && x.SettlementId is not null)
            ? [] : facts.Where(x => x.Kind == "BankCredit").ToArray();
        string? reason = null;
        if (facts.Any(x => x.Kind == ConflictKind)) { reason = "conflicting_evidence"; }
        else if (facts.Any(x => x.Kind is "Return" or "Refund" or "Dispute")) { reason = "return_requires_review"; }
        // A collection that failed cannot also have succeeded, been credited or been receipted. In
        // whichever order the two were reported, neither is believed over the other.
        else if (facts.Any(x => x.Kind == "Failed")
            && (credits.Length > 0 || op.JournalId is not null || facts.Any(x => x.Kind == "Succeeded"))) { reason = "conflicting_evidence"; }
        else if (facts.Any(x => x.Kind == "PayoutFailed" && credits.Any(c => c.PayoutId == x.PayoutId)))
        { reason = "conflicting_evidence"; }
        // A clean item only (ADR-053): the processor kept exactly the fee quoted, so the bank received
        // the ledger amount. A fee difference in either direction is not posted from single-payment
        // evidence; it needs the batch.
        else if (credits.Any(x => x.Gross != op.ChargedAmount || x.Fee != op.QuotedFee || x.Net != op.Amount
            || x.Currency != op.Currency || x.BankId != op.BankId || x.Generation != op.Generation
            || string.IsNullOrWhiteSpace(x.EvidenceId) || string.IsNullOrWhiteSpace(x.PayoutId)
            || x.BankDate == default)) { reason = "unsupported_settlement"; }
        else if (credits.Select(x => (x.EvidenceId, x.PayoutId, x.BankDate)).Distinct().Count() > 1)
        { reason = "conflicting_evidence"; }

        // A posted return answers the evidence it was posted from, and nothing else: a refund, a dispute
        // or a return that disagrees with it arriving afterwards still needs a person.
        var posted = effects.SingleOrDefault(x => x.Kind == "Return");
        if (reason == "return_requires_review" && posted is not null)
        {
            // The posted return answers the return evidence it was posted from. One posted from a payout
            // batch answers return notices that agree with each other: the notice and the payout are
            // the same event seen twice, in either order. A refund or a dispute is never answered by it.
            var returns = facts.Where(x => x.Kind == "Return").Select(x => (x.EvidenceId, x.BankDate, x.Gross)).Distinct().ToArray();
            var answered = facts.SingleOrDefault(x => x.Id == posted.ObservationId);
            var consistent = facts.All(x => x.Kind is not ("Refund" or "Dispute")) && (posted.SettlementId is not null
                ? returns.Length <= 1
                : answered is not null && returns.All(x => x == (answered.EvidenceId, answered.BankDate, answered.Gross)));
            if (consistent) { op.Status = "Returned"; op.Reason = null; }
            else { op.Status = "NeedsReview"; op.Reason = "evidence_after_return"; }
        }
        // Returned by a payout batch and nothing here says otherwise: later progress must not read as settled.
        else if (reason is null && posted is not null) { op.Status = "Returned"; op.Reason = null; }
        else if (reason is not null) { op.Status = "NeedsReview"; op.Reason = reason; }
        else if (op.JournalId is not null) { op.Status = "Settled"; op.Reason = null; }
        else if (facts.Any(x => x.Kind == "Failed")) { op.Status = "Failed"; op.Reason = "collection_failed"; }
        else if (op.Reason is not null && op.Reason != "technical_failure") { op.Status = "NeedsReview"; }
        else if (credits.FirstOrDefault(x => x.Complete) is { } credit)
        {
            var request = new PaymentEligibilityRequest(op.TenantId, op.BankId, credit.BankDate);
            if (!(await eligibility.ReadAsync([request], ct)).GetValueOrDefault(request))
            {
                op.Status = "NeedsReview"; op.Reason = "attribution_unavailable";
            }
            else
            {
                // Receipt, immutable effect, summary and consumed inbox count commit in this ONE transaction.
                var journalId = await ledger.RecordSettledReceiptAsync(op.TenantId, op.BankId, op.Amount, credit.BankDate,
                    op.Method, $"sim-payment:{op.Id:N}:receipt", ct);
                db.Add(new PaymentEffect { Id = UuidV7.NewId(), OperationId = op.Id, ObservationId = credit.Id, JournalId = journalId });
                op.JournalId = journalId; op.Status = "Settled"; op.Reason = null;
            }
        }
        else { op.Status = "Processing"; op.Reason = null; }
        // The paid date is the processor's, and the earliest it reported: a redelivered success must not move it.
        if (facts.Where(x => x.Kind == "Succeeded").Select(x => (DateTime?)x.ObservedAt).Min() is { } paidAt)
        { op.PaidAt = paidAt; }
        op.ProcessedCount = facts.Count; op.LeaseUntil = null; op.LeaseClaimId = null;
        // Acceptance is durable. Future signed observations wake this operation; idle processing
        // needs no repeated provider call or audit write every polling interval.
        op.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        op.Attempts = 0;
        await db.SaveChangesAsync(ct);
    }

    public async Task FailAsync(FixtureBinding binding, Guid id, Guid token, string reason, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == id, ct);
        if (op.LeaseClaimId != token || op.JournalId is not null) { return; }
        // A payment a person holds, or has closed, under a reason of its own stays exactly there
        // whatever fails while a late fact is judged. Made "Processing" with a technical failure it
        // would lose that reason, and the retry would judge it as a payment nobody had looked at: a
        // complete bank credit would then post. It is still tried again on the same schedule, so
        // that the fact is judged, and CompleteAsync keeps it with a person.
        var held = op.Status is "NeedsReview" or "ReviewClosed" && op.Reason is not (null or "technical_failure");
        op.LeaseUntil = null; op.LeaseClaimId = null; op.Attempts++;
        var again = reason == "technical_failure" && op.Attempts <= RetrySeconds.Length;
        op.DueAt = again ? Now.AddSeconds(RetrySeconds[op.Attempts - 1]) : DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
        if (!held) { op.Reason = reason; op.Status = again ? "Processing" : "NeedsReview"; }
        // Out of tries, a held payment rests with the fact unjudged. Nobody can retry it, because its
        // reason is not a technical one, so the count starts again for the next fact that wakes it.
        else if (!again) { op.Attempts = 0; }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RetryAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null) { return false; }
        if (op.JournalId is not null) { return true; }
        if (op.Status != "NeedsReview" || op.Reason != "technical_failure") { throw new PaymentConflictException(); }
        op.Status = "Processing"; op.Reason = null; op.DueAt = Now; op.Attempts = 0;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Posts a full bank return as the receipt's linked reversal (#490, ADR-052). Only a person starts
    /// this; return evidence alone never posts. A refusal leaves the payment in review under a
    /// <c>return_*</c> reason naming what stopped it, and posts nothing.
    /// </summary>
    public async Task<PaymentReturnDecision?> PostReturnAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null) { return null; }
        MatchBinding(op, binding);
        // The effect is the idempotency record: a repeat finds it and posts nothing.
        if (op.Status == "Returned") { return new(op, null); }
        if (op.Status != "NeedsReview" || op.JournalId is null || op.ProviderId is null
            || op.Reason?.StartsWith("return_", StringComparison.Ordinal) != true)
        { throw new PaymentConflictException(); }

        var facts = await db.Set<PaymentObservation>().AsNoTracking().Where(x => x.ProviderId == op.ProviderId).ToListAsync(ct);
        var returns = facts.Where(x => x.Kind == "Return").ToArray();
        string? refusal = null;
        if (facts.Any(x => x.Kind == ConflictKind)
            || returns.Select(x => (x.EvidenceId, x.BankDate, x.Gross)).Distinct().Count() > 1)
        { refusal = "return_conflicting_evidence"; }
        else if (returns.Length == 0 || facts.Any(x => x.Kind is "Refund" or "Dispute"))
        { refusal = "return_kind_unsupported"; }
        else if (returns.Any(x => x.Gross != op.Amount || x.Fee != 0 || x.Net != op.Amount || !x.Complete
            || x.Currency != op.Currency || x.BankId != op.BankId || x.Generation != op.Generation
            || x.BankDate == default))
        { refusal = "return_partial_unsupported"; }
        else
        {
            var evidence = returns[0];
            var outcome = await ledger.ReturnSettledReceiptAsync(op.JournalId.Value, evidence.BankDate,
                $"sim-payment:{op.Id:N}:return", "Bank returned this simulated tenant payment.", ct);
            if (outcome.JournalId is { } journalId)
            {
                // Reversal, effect and status commit in this ONE transaction, like the receipt.
                db.Add(new PaymentEffect { Id = UuidV7.NewId(), OperationId = op.Id, ObservationId = evidence.Id, JournalId = journalId, Kind = "Return" });
                op.Status = "Returned"; op.Reason = null;
            }
            else { refusal = outcome.Refusal ?? "return_rejected"; }
        }
        if (refusal is not null) { op.Reason = refusal; }
        op.LastAttemptAt = Now; await db.SaveChangesAsync(ct);
        return new(op, refusal);
    }

    /// <summary>
    /// Closes a review without posting (#490): staff have corrected the books by hand, or decided nothing
    /// is owed. The note is required and the evidence stays. An observation that arrives afterwards
    /// reopens the review.
    /// </summary>
    public async Task<PaymentOperation?> CloseReviewAsync(FixtureBinding binding, Guid id, Guid userId, string note, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null) { return null; }
        MatchBinding(op, binding);
        if (op.Status == "ReviewClosed") { return op; }
        // A technical failure is not closed: its way out is Retry, and a closed operation that kept that
        // reason would still post its receipt the next time evidence arrived.
        if (op.Status != "NeedsReview" || op.Reason == "technical_failure") { throw new PaymentConflictException(); }
        op.Status = "ReviewClosed"; op.ReviewNote = note; op.ReviewClosedAt = Now; op.ReviewClosedBy = userId;
        await db.SaveChangesAsync(ct);
        return op;
    }

    /// <summary>
    /// The payments still waiting for the processor to say how they went, oldest first: accepted,
    /// in processing, and not reported paid. These are what a lost notice leaves with no way on.
    /// </summary>
    public async Task<IReadOnlyList<WaitingPayment>> WaitingAsync(FixtureBinding binding, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        // Only what this binding answers for: a row of another generation, bank or account would set
        // how far back the processor is asked and then be refused when it came to be aged.
        return await db.Set<PaymentOperation>().AsNoTracking().Where(Waiting)
            .Where(x => x.Generation == binding.Generation && x.BankId == binding.BankId && x.Account == binding.Account)
            .OrderBy(x => x.CreatedAt).Select(x => new WaitingPayment(x.Id, x.CreatedAt)).ToListAsync(ct);
    }

    /// <summary>
    /// Sends a payment to a person when the processor has reported no outcome for
    /// <see cref="OutcomeOverdueAfter"/>, and says whether it did. Only one that is still waiting and
    /// at rest, with every fact it holds already judged: one the worker has or is about to take, or
    /// one with a fact unjudged, may be about to end by itself. Nothing is posted and nothing is asked
    /// of the processor. The caller must first have asked the processor for whatever it did not
    /// deliver; that cannot be checked from here. A fact that arrives afterwards is still judged:
    /// a failure ends the payment, and anything else is recorded and leaves it with a person.
    /// </summary>
    public async Task<bool> MarkOverdueAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        // Read first only to name the lock: the reference, then the operation, as ReceiveAsync and
        // CompleteAsync take them. An operation keeps the reference it has, and is read again below.
        var reference = await db.Set<PaymentOperation>().AsNoTracking().Where(x => x.Id == id)
            .Select(x => x.ProviderId).SingleOrDefaultAsync(ct);
        if (reference is null) { return false; }
        await LockAsync("reference:" + reference, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().Where(Waiting).SingleOrDefaultAsync(x => x.Id == id && x.ProviderId == reference, ct);
        if (op is null) { return false; }
        MatchBinding(op, binding);
        // At rest: not held, not due, and not between attempts. A retry that is scheduled has its own
        // way to a person, and one marked here would stay due with nobody to claim it.
        if (op.LeaseUntil > Now || op.DueAt <= Now || op.Reason is not null) { return false; }
        if (op.CreatedAt > Now - OutcomeOverdueAfter) { return false; }
        if (await db.Set<PaymentObservation>().CountAsync(x => x.ProviderId == reference, ct) != op.ProcessedCount) { return false; }
        op.Status = "NeedsReview"; op.Reason = "outcome_overdue";
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static void MatchBinding(PaymentOperation op, FixtureBinding binding)
    {
        if (op.Generation != binding.Generation || op.BankId != binding.BankId || op.Account != binding.Account)
        { throw new PaymentUnavailableException(); }
    }

    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
