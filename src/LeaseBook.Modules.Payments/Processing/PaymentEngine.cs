using System.Globalization;
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

/// <summary>All methods run on the caller's org transaction. No provider I/O occurs here.</summary>
public sealed class PaymentEngine(DbContext db, IOrgContext org, TimeProvider clock,
    IPaymentLedger ledger, IPaymentEligibility eligibility)
{
    public static readonly int[] RetrySeconds = [1, 5, 30, 120, 600];
    public static readonly string[] EventKinds = ["Processing", "Succeeded", "Available", "PayoutPending",
        "PayoutPaid", "PayoutFailed", "Failed", "BankCredit", "Return", "Refund", "Dispute"];
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

    public async Task<PaymentOperation> SubmitAsync(FixtureBinding binding, Guid tenantId, Guid userId,
        Guid key, decimal amount, string currency, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync("request:" + userId + ":" + key, ct);
        var fingerprint = Hash(string.Join('|', tenantId, amount.ToString("0.00", CultureInfo.InvariantCulture), currency,
            binding.Generation, binding.BankId, binding.Account));
        var existing = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.UserId == userId && x.Key == key, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint) { throw new PaymentConflictException(); }
            return existing;
        }
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
            || observation.Mode != "Simulation") { throw new PaymentUnavailableException(); }
        await LockAsync("event:" + observation.EventId, ct);
        var fingerprint = Hash(JsonSerializer.Serialize(observation));
        var existing = await db.Set<PaymentObservation>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Account == binding.Account && x.Mode == "Simulation" && x.EventId == observation.EventId, ct);
        if (existing?.Fingerprint == fingerprint) { return; }
        // Preserve the original event. Authenticated reuse with altered content is separate evidence.
        var eventId = existing is null ? observation.EventId : "conflict:" + fingerprint;
        if (await db.Set<PaymentObservation>().AnyAsync(x => x.EventId == eventId && x.Account == binding.Account, ct)) { return; }
        db.Add(new PaymentObservation
        {
            Id = UuidV7.NewId(),
            EventId = eventId,
            ProviderId = existing?.ProviderId ?? observation.ProviderId,
            Generation = observation.Generation,
            Account = observation.Account,
            Mode = observation.Mode,
            Kind = existing is null ? observation.Kind : "Conflict",
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
        var providerId = existing?.ProviderId ?? observation.ProviderId;
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
        if (op.Status is "Settled" or "Failed" or "NeedsReview" && count == op.ProcessedCount) { return null; }
        op.LeaseClaimId = UuidV7.NewId(); op.LeaseUntil = Now.AddSeconds(30); op.LastAttemptAt = Now;
        await db.SaveChangesAsync(ct);
        return op;
    }

    public async Task CompleteAsync(FixtureBinding binding, Guid id, Guid leaseClaimId, ProcessorResult provider, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleAsync(x => x.Id == id, ct);
        MatchBinding(op, binding);
        if (op.LeaseClaimId != leaseClaimId) { return; }
        if (provider.Outcome != "Accepted" || string.IsNullOrWhiteSpace(provider.ProviderId))
        { throw new IOException("The simulated provider outcome is uncertain."); }
        if (op.ProviderId is not null && op.ProviderId != provider.ProviderId) { throw new PaymentConflictException(); }
        op.ProviderId = provider.ProviderId;
        var facts = await db.Set<PaymentObservation>().AsNoTracking().Where(x => x.ProviderId == op.ProviderId).ToListAsync(ct);
        var credits = facts.Where(x => x.Kind == "BankCredit").ToArray();
        string? reason = null;
        if (facts.Any(x => x.Kind == "Conflict")) { reason = "conflicting_evidence"; }
        else if (facts.Any(x => x.Kind is "Return" or "Refund" or "Dispute")) { reason = "return_requires_review"; }
        else if (facts.Any(x => x.Kind == "Failed") && (credits.Length > 0 || op.JournalId is not null)) { reason = "conflicting_evidence"; }
        else if (facts.Any(x => x.Kind == "PayoutFailed" && credits.Any(c => c.PayoutId == x.PayoutId)))
        { reason = "conflicting_evidence"; }
        else if (credits.Any(x => x.Gross != op.Amount || x.Fee != 0 || x.Net != op.Amount
            || x.Currency != op.Currency || x.BankId != op.BankId || x.Generation != op.Generation
            || string.IsNullOrWhiteSpace(x.EvidenceId) || string.IsNullOrWhiteSpace(x.PayoutId)
            || x.BankDate == default)) { reason = "unsupported_settlement"; }
        else if (credits.Select(x => (x.EvidenceId, x.PayoutId, x.BankDate)).Distinct().Count() > 1)
        { reason = "conflicting_evidence"; }

        if (reason is not null) { op.Status = "NeedsReview"; op.Reason = reason; }
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
                    $"sim-payment:{op.Id:N}:receipt", ct);
                db.Add(new PaymentEffect { Id = UuidV7.NewId(), OperationId = op.Id, ObservationId = credit.Id, JournalId = journalId });
                op.JournalId = journalId; op.Status = "Settled"; op.Reason = null;
            }
        }
        else { op.Status = "Processing"; op.Reason = null; }
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
        op.LeaseUntil = null; op.LeaseClaimId = null; op.Attempts++;
        op.Reason = reason;
        if (reason == "technical_failure" && op.Attempts <= RetrySeconds.Length)
        { op.Status = "Processing"; op.DueAt = Now.AddSeconds(RetrySeconds[op.Attempts - 1]); }
        else { op.Status = "NeedsReview"; op.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc); }
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> RetryAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await RequireFixtureAsync(binding, ct);
        await LockAsync(id.ToString(), ct);
        var op = await db.Set<PaymentOperation>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (op is null) { return false; }
        if (op.JournalId is not null) { return true; }
        if (op.Reason != "technical_failure") { throw new PaymentConflictException(); }
        op.Status = "Processing"; op.Reason = null; op.DueAt = Now; op.Attempts = 0;
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
