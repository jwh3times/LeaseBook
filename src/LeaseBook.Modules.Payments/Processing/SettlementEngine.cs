using System.Text.Json;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Modules.Payments.Processing;

/// <summary>One line of a processor's account of a payout. <see cref="Net"/> is signed: what it adds to the bank amount.</summary>
public sealed record ProcessorSettlementItem(
    string Item, string Kind, string ProviderId, decimal Gross, decimal Fee, decimal Net, string Currency);

/// <summary>Verified bank evidence for one payout, with the processor's list of what it contains.</summary>
public sealed record ProcessorSettlement(
    string PayoutId, string Account, string Mode, Guid Generation, string PayoutType, decimal BankAmount,
    string Currency, Guid BankId, DateOnly BankDate, string EvidenceId, DateTime ObservedAt,
    IReadOnlyList<ProcessorSettlementItem> Items);

/// <summary>
/// A bank's word for one payout that a processor reported apart from it (ADR-054): what the bank
/// received and on what day. <see cref="EvidenceId"/> is the evidence's own reference.
/// </summary>
public sealed record BankEvidence(string PayoutId, string Account, string Mode, Guid Generation,
    decimal BankAmount, DateOnly BankDate, string EvidenceId);

/// <summary>What became of bank evidence. Only <see cref="Stored"/> changes anything.</summary>
public static class BankEvidenceOutcomes
{
    /// <summary>Kept. The payout can now be checked against it.</summary>
    public const string Stored = "Stored";
    /// <summary>The payout already holds exactly this evidence.</summary>
    public const string Same = "Same";
    /// <summary>The payout already holds other evidence. This was not kept.</summary>
    public const string Conflicting = "Conflicting";
    /// <summary>No payout here is waiting for a bank under that reference.</summary>
    public const string Unknown = "Unknown";
    /// <summary>Not evidence that could be stored as it was given.</summary>
    public const string Malformed = "Malformed";
}

/// <param name="Evaluate">The payout was waiting for exactly this, and is to be checked and posted now.</param>
public sealed record BankEvidenceResult(string Outcome, Guid? Id = null, bool Evaluate = false);

/// <summary>
/// Stores, checks and posts payout batches (ADR-053). A payout posts completely or not at all, and a
/// payout that cannot post says why in a stable reason. A payout a processor reports apart from any
/// bank (ADR-054) is stored and held, and posts nothing until a bank's evidence for it arrives. All
/// methods run on the caller's organization transaction; no provider I/O occurs here.
/// </summary>
public sealed class SettlementEngine(DbContext db, TimeProvider clock, PaymentEngine payments, IPaymentLedger ledger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Stores the evidence, once, and returns the payout's id. A redelivery is a no-op. The same payout
    /// with different content is kept out and marks a payout still awaiting a decision as conflicting.
    /// Returns null for evidence that is not well formed: it cannot be stored as given, and storing an
    /// altered copy would not be evidence.
    /// </summary>
    public async Task<Guid?> ReceiveAsync(FixtureBinding binding, ProcessorSettlement evidence, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        if (evidence.Generation != binding.Generation || evidence.Account != binding.Account || evidence.Mode != binding.Mode)
        { throw new PaymentUnavailableException(); }
        if (!WellFormed(evidence)) { return null; }
        await payments.LockAsync("settlement:" + evidence.PayoutId, ct);
        var fingerprint = Fingerprint(evidence);
        var existing = await FindAsync(binding, evidence.PayoutId, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint == fingerprint) { return existing.Id; }
            // Decide under the same lock a posting takes, on the row as it is now: a payout that has
            // posted or been closed keeps that outcome, whatever arrives afterwards.
            await payments.LockAsync("settlement-id:" + existing.Id, ct);
            var current = await db.Set<PaymentSettlement>().SingleAsync(x => x.Id == existing.Id, ct);
            if (current.Status is SettlementStatuses.Received or SettlementStatuses.NeedsReview)
            {
                current.Status = SettlementStatuses.NeedsReview;
                current.Reason = "conflicting_evidence"; current.ReasonItem = null;
                await db.SaveChangesAsync(ct);
            }
            return current.Id;
        }

        var settlement = new PaymentSettlement
        {
            Id = UuidV7.NewId(),
            Generation = evidence.Generation,
            Account = evidence.Account,
            Mode = evidence.Mode,
            PayoutId = evidence.PayoutId,
            PayoutType = evidence.PayoutType,
            BankId = evidence.BankId,
            BankDate = evidence.BankDate,
            BankAmount = evidence.BankAmount,
            Currency = evidence.Currency,
            EvidenceId = evidence.EvidenceId,
            Fingerprint = fingerprint,
        };
        await AddAsync(settlement, evidence.Items, ct);
        return settlement.Id;
    }

    /// <summary>
    /// Stores a processor's own account of a payout, once, and returns the payout's id; null for one
    /// that is not well formed. Nothing posts from it. A payout the processor says it paid is stored
    /// with its lines and held until a bank's evidence arrives. One it says failed is stored, or
    /// marked if it is already here, and sent to a person whatever state it was in: held, in review,
    /// closed or posted. Nothing is reversed for it. The same account again is a no-op, and so is
    /// anything said of a payout already known to have failed; a different account of a payout still
    /// awaiting a decision marks it as conflicting.
    /// </summary>
    public async Task<Guid?> ReceivePayoutAsync(FixtureBinding binding, ProcessorPayout payout, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        if (payout.Generation != binding.Generation || payout.Account != binding.Account || payout.Mode != binding.Mode
            || payout.BankId != binding.BankId)
        { throw new PaymentUnavailableException(); }
        if (!WellFormed(payout)) { return null; }
        await payments.LockAsync("settlement:" + payout.PayoutId, ct);
        var failed = payout.State == ProcessorPayoutStates.Failed;
        var fingerprint = Fingerprint(payout);
        var existing = await FindAsync(binding, payout.PayoutId, ct);
        if (existing is null)
        {
            var settlement = new PaymentSettlement
            {
                Id = UuidV7.NewId(),
                Generation = payout.Generation,
                Account = payout.Account,
                Mode = payout.Mode,
                PayoutId = payout.PayoutId,
                PayoutType = payout.PayoutType,
                BankId = payout.BankId,
                ProcessorAmount = payout.Amount,
                Currency = payout.Currency,
                Fingerprint = fingerprint,
                Status = failed ? SettlementStatuses.NeedsReview : SettlementStatuses.AwaitingBank,
                Reason = failed ? SettlementReasons.PayoutFailed : null,
                FailedAt = failed ? payout.ObservedAt : null,
            };
            await AddAsync(settlement, payout.Items, ct);
            return settlement.Id;
        }

        // Decided under the lock a posting takes, on the row as it is now.
        await payments.LockAsync("settlement-id:" + existing.Id, ct);
        var current = await db.Set<PaymentSettlement>().SingleAsync(x => x.Id == existing.Id, ct);
        if (failed)
        {
            // Once. A second notice of the same failure must not reopen a review a person has closed.
            if (current.FailedAt is null)
            {
                current.FailedAt = payout.ObservedAt;
                current.Status = SettlementStatuses.NeedsReview;
                current.Reason = SettlementReasons.PayoutFailed; current.ReasonItem = null;
                await db.SaveChangesAsync(ct);
            }
            return current.Id;
        }
        // A payout known to have failed keeps that outcome: a notice that it was paid, arriving late
        // or out of order, changes nothing and is no conflict.
        if (current.FailedAt is not null || current.Fingerprint == fingerprint) { return current.Id; }
        if (current.Status is SettlementStatuses.Received or SettlementStatuses.AwaitingBank or SettlementStatuses.NeedsReview)
        {
            current.Status = SettlementStatuses.NeedsReview;
            current.Reason = "conflicting_evidence"; current.ReasonItem = null;
            await db.SaveChangesAsync(ct);
        }
        return current.Id;
    }

    /// <summary>
    /// Takes a bank's evidence for a payout that a processor reported apart from it. Given once: the
    /// same again is a no-op, and different evidence is kept out and marks a payout still awaiting a
    /// decision as conflicting. Nothing posts here. A payout that was waiting only for the bank
    /// becomes ready to be checked, which <see cref="BankEvidenceResult.Evaluate"/> says; one a
    /// person already holds keeps its status and reason, with the evidence beside it.
    /// </summary>
    public async Task<BankEvidenceResult> ReceiveBankEvidenceAsync(FixtureBinding binding, BankEvidence evidence, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        if (evidence.Generation != binding.Generation || evidence.Account != binding.Account || evidence.Mode != binding.Mode)
        { throw new PaymentUnavailableException(); }
        // A bank has not received tomorrow what it has yet to receive, and a receipt dated ahead would
        // post into a day nobody has reached. Today is the host's, in UTC.
        if (!Text(evidence.PayoutId, 100) || !Text(evidence.EvidenceId, 100) || evidence.BankDate == default
            || evidence.BankDate > DateOnly.FromDateTime(Now) || !Storable(evidence.BankAmount))
        { return new(BankEvidenceOutcomes.Malformed); }
        await payments.LockAsync("settlement:" + evidence.PayoutId, ct);
        // Only a payout the processor reported by itself can be waiting for a bank. One that arrived
        // with its bank evidence is not known here as something a bank has still to speak for.
        var existing = await FindAsync(binding, evidence.PayoutId, ct);
        if (existing?.ProcessorAmount is null) { return new(BankEvidenceOutcomes.Unknown); }
        await payments.LockAsync("settlement-id:" + existing.Id, ct);
        var current = await db.Set<PaymentSettlement>().SingleAsync(x => x.Id == existing.Id, ct);
        if (current.Banked)
        {
            if (current.BankAmount == evidence.BankAmount && current.BankDate == evidence.BankDate && current.EvidenceId == evidence.EvidenceId)
            { return new(BankEvidenceOutcomes.Same, current.Id); }
            // A payout that posted, was closed or is known to have failed keeps that outcome.
            if (current.FailedAt is null && current.Status is SettlementStatuses.Received or SettlementStatuses.NeedsReview)
            {
                current.Status = SettlementStatuses.NeedsReview;
                current.Reason = "conflicting_evidence"; current.ReasonItem = null;
                await db.SaveChangesAsync(ct);
            }
            return new(BankEvidenceOutcomes.Conflicting, current.Id);
        }
        current.BankAmount = evidence.BankAmount; current.BankDate = evidence.BankDate; current.EvidenceId = evidence.EvidenceId;
        var waiting = current.Status == SettlementStatuses.AwaitingBank;
        if (waiting) { current.Status = SettlementStatuses.Received; }
        await db.SaveChangesAsync(ct);
        return new(BankEvidenceOutcomes.Stored, current.Id, waiting);
    }

    private Task<PaymentSettlement?> FindAsync(FixtureBinding binding, string payoutId, CancellationToken ct) =>
        db.Set<PaymentSettlement>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Account == binding.Account && x.Mode == binding.Mode && x.PayoutId == payoutId, ct);

    private async Task AddAsync(PaymentSettlement settlement, IReadOnlyList<ProcessorSettlementItem> items, CancellationToken ct)
    {
        db.Add(settlement);
        db.AddRange(items.Select(x => new PaymentSettlementItem
        {
            Id = UuidV7.NewId(),
            SettlementId = settlement.Id,
            Item = x.Item,
            Kind = x.Kind,
            ProviderId = x.ProviderId,
            Gross = x.Gross,
            Fee = x.Fee,
            Net = x.Net,
            Currency = x.Currency,
        }));
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks a stored payout and posts it if it can be posted. <paramref name="confirmedBy"/> is the
    /// administrator posting a payout that contains a return; without one, such a payout waits.
    /// Without one, too, a payout that is already with a person is returned as it stands: the check
    /// that runs by itself judges a payout once, when it arrives or when its bank evidence does, and
    /// after that only an administrator's post checks it again.
    /// A payout no bank has spoken for is checked and never posted, whoever asks: it goes to a person
    /// if its lines could not post, and otherwise stays waiting for the bank.
    /// Returns null when the payout is unknown here.
    /// </summary>
    public async Task<PaymentSettlement?> EvaluateAsync(FixtureBinding binding, Guid id, Guid? confirmedBy, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        await payments.LockAsync("settlement-id:" + id, ct);
        var settlement = await db.Set<PaymentSettlement>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (settlement is null) { return null; }
        if (settlement.Generation != binding.Generation || settlement.Account != binding.Account || settlement.Mode != binding.Mode)
        { throw new PaymentUnavailableException(); }
        if (settlement.Status == SettlementStatuses.Posted) { return settlement; }
        // Neither can be posted whatever a person decides. A payout that failed may hold postings
        // already, from before it failed; checking it again must never post them a second time.
        if (settlement.Status == SettlementStatuses.Closed || settlement.FailedAt is not null
            || settlement.Reason is "conflicting_evidence" or SettlementReasons.PayoutFailed)
        { throw new PaymentConflictException(); }

        // In review, and nobody asked. The same notice arrives again, redelivered or found by a sweep
        // every few minutes; were each arrival to check the payout again, one whose cause had gone
        // (fees since funded, a period since reopened, a fault since mended) would post unattended.
        if (confirmedBy is null && settlement.Status == SettlementStatuses.NeedsReview) { return settlement; }

        var items = await db.Set<PaymentSettlementItem>().Where(x => x.SettlementId == id).OrderBy(x => x.Item).ToListAsync(ct);
        var providerIds = items.Where(x => x.Kind != SettlementItemKinds.Fee && x.ProviderId != "").Select(x => x.ProviderId)
            .Distinct().Order(StringComparer.Ordinal).ToArray();
        // The lock a fact is stored under, for every payment a line names, before the payments are
        // read: a fact about one of them is then either committed and seen below, or stored after
        // this commits, when the worker judges it against the receipt. Without it a failure or a
        // dispute stored a moment ago and not yet judged would leave the payment looking as if it
        // were still waiting, and the payout would receipt it. References first, then operations, the
        // order a notice and the worker take them in.
        foreach (var reference in providerIds) { await payments.LockAsync("reference:" + reference, ct); }
        var operations = await db.Set<PaymentOperation>()
            .Where(x => x.Account == settlement.Account && x.ProviderId != null && providerIds.Contains(x.ProviderId))
            .ToDictionaryAsync(x => x.ProviderId!, ct);
        // Lock every payment the payout touches, in a stable order, so a single-payment observation
        // cannot post or return the same payment while the batch does.
        foreach (var operation in operations.Values.OrderBy(x => x.Id)) { await payments.LockAsync(operation.Id.ToString(), ct); }
        foreach (var operation in operations.Values) { await db.Entry(operation).ReloadAsync(ct); }
        var operationIds = operations.Values.Select(o => o.Id).ToArray();
        var returned = (await db.Set<PaymentEffect>().AsNoTracking()
            .Where(x => x.Kind == "Return" && operationIds.Contains(x.OperationId))
            .Select(x => x.OperationId).ToListAsync(ct)).ToHashSet();
        // What has been said of each payment, judged or not. The status alone is what was last judged.
        var said = (await db.Set<PaymentObservation>().AsNoTracking()
            .Where(x => x.Account == settlement.Account && providerIds.Contains(x.ProviderId))
            .Select(x => new { x.ProviderId, x.Kind }).ToListAsync(ct)).ToLookup(x => x.ProviderId, x => x.Kind);

        var reason = Check(binding, settlement, items, operations, returned, said, out var lines);
        if (!settlement.Banked)
        {
            // No bank has spoken, so nothing posts. What could never post goes to a person now.
            if (reason is not null) { return await HoldAsync(settlement, reason, null, ct); }
            settlement.Status = SettlementStatuses.AwaitingBank; settlement.Reason = null; settlement.ReasonItem = null;
            await db.SaveChangesAsync(ct);
            return settlement;
        }
        if (reason is null && items.Any(x => x.Kind == SettlementItemKinds.Return) && confirmedBy is null)
        { reason = "settlement_requires_confirmation"; }
        if (reason is not null) { return await HoldAsync(settlement, reason, null, ct); }

        var outcome = await ledger.PostSettlementAsync(settlement.BankId, settlement.BankDate, settlement.PayoutId,
            PaymentMemos.Receipt(settlement.Mode), lines, ct);
        if (outcome.Refusal is not null) { return await HoldAsync(settlement, outcome.Refusal, outcome.RefusedItem, ct); }

        // Entries, effects, payment statuses and the payout's own status commit in this ONE transaction.
        foreach (var posting in outcome.Postings)
        {
            var item = items.Single(x => x.Item == posting.Item);
            if (posting.Kind == "FeeDifference") { item.FeeEntryId = posting.EntryId; } else { item.EntryId = posting.EntryId; }
            // A standalone fee belongs to the payout, not to a payment: it has no effect row.
            if (item.Kind == SettlementItemKinds.Fee) { continue; }
            var operation = operations[item.ProviderId];
            db.Add(new PaymentEffect
            {
                Id = UuidV7.NewId(),
                OperationId = operation.Id,
                SettlementId = settlement.Id,
                JournalId = posting.EntryId,
                Kind = posting.Kind == "FeeDifference" ? item.Kind + "Fee" : posting.Kind,
            });
            if (posting.Kind == "Receipt") { operation.JournalId = posting.EntryId; operation.Status = "Settled"; operation.Reason = null; }
            if (posting.Kind == "Return") { operation.Status = "Returned"; operation.Reason = null; }
        }
        settlement.Status = SettlementStatuses.Posted; settlement.Reason = null; settlement.ReasonItem = null;
        settlement.PostedAt = Now; settlement.PostedBy = confirmedBy;
        await db.SaveChangesAsync(ct);
        return settlement;
    }

    /// <summary>
    /// Records that checking or posting a payout failed for a technical reason, so that it shows as
    /// waiting and an administrator can try again or close it. Never overwrites a payout that posted.
    /// </summary>
    public async Task MarkFailedAsync(FixtureBinding binding, Guid id, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        await payments.LockAsync("settlement-id:" + id, ct);
        var settlement = await db.Set<PaymentSettlement>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (settlement is null || settlement.Status != SettlementStatuses.Received) { return; }
        await HoldAsync(settlement, "technical_failure", null, ct);
    }

    /// <summary>Closes a payout's review with a note and no posting. Returns null when the payout is unknown here.</summary>
    public async Task<PaymentSettlement?> CloseAsync(FixtureBinding binding, Guid id, Guid userId, string note, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        await payments.LockAsync("settlement-id:" + id, ct);
        var settlement = await db.Set<PaymentSettlement>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (settlement is null) { return null; }
        if (settlement.Generation != binding.Generation || settlement.Account != binding.Account || settlement.Mode != binding.Mode)
        { throw new PaymentUnavailableException(); }
        if (settlement.Status == SettlementStatuses.Closed) { return settlement; }
        if (settlement.Status == SettlementStatuses.Posted) { throw new PaymentConflictException(); }
        await ReleaseAsync(settlement, ct);
        settlement.Status = SettlementStatuses.Closed; settlement.ReviewNote = note;
        settlement.ReviewClosedAt = Now; settlement.ReviewClosedBy = userId;
        await db.SaveChangesAsync(ct);
        return settlement;
    }

    // A payout closed with nothing posted will never carry its payments to the bank (#581, ADR-053
    // addendum). Each one still waiting for it, with no receipt, goes to a person under a reason of its
    // own: left in processing it would read as in transit for ever. No later payout is expected to
    // carry it, since a processor returns a failed payout's funds as one balance line, not as the charges.
    // A payout that posted, and failed afterwards, receipted every payment it carried, so none waits.
    // Only payments on this payout's account and bank; a line naming another one's is not this payout's
    // to move. (An organization has one fixture, so a payment's generation is the payout's.)
    // Each payment is locked, in the order a payout's post locks them, and read again under its lock.
    // Its reference is not locked: nothing here waits on one, and a fact stored meanwhile wakes the
    // payment once this commits, as it wakes any payment in review.
    private async Task ReleaseAsync(PaymentSettlement settlement, CancellationToken ct)
    {
        var references = await db.Set<PaymentSettlementItem>().AsNoTracking()
            .Where(x => x.SettlementId == settlement.Id && x.Kind == SettlementItemKinds.Payment)
            .Select(x => x.ProviderId).ToListAsync(ct);
        var operations = await db.Set<PaymentOperation>()
            .Where(x => x.Account == settlement.Account && x.BankId == settlement.BankId && references.Contains(x.ProviderId!))
            .OrderBy(x => x.Id).ToListAsync(ct);
        foreach (var operation in operations)
        {
            await payments.LockAsync(operation.Id.ToString(), ct);
            await db.Entry(operation).ReloadAsync(ct);
            // A payment in processing has no receipt: one posts only as it settles. Nor has one in review
            // for a technical failure, whose only way out, a retry, would put it back in processing.
            if (!(operation.Status == "Processing" || (operation.Status == "NeedsReview" && operation.Reason == "technical_failure")))
            { continue; }
            operation.Status = "NeedsReview"; operation.Reason = "payout_closed";
            // Taken from the worker: whatever an attempt in flight ends in, its lease no longer matches,
            // so it changes nothing here. A payment in review is claimed only for a fact it has not
            // judged, so one left due with none would be found by the worker and never claimed. It
            // rests instead, as a judged payment does; one with a fact to judge stays due, and its
            // reason keeps it with a person whatever the judgement finds, short of a failure.
            operation.LeaseUntil = null; operation.LeaseClaimId = null;
            if (await db.Set<PaymentObservation>().CountAsync(x => x.Account == operation.Account
                    && x.ProviderId == operation.ProviderId, ct) == operation.ProcessedCount)
            { operation.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc); operation.Attempts = 0; }
        }
    }

    private async Task<PaymentSettlement> HoldAsync(PaymentSettlement settlement, string reason, string? item, CancellationToken ct)
    {
        settlement.Status = SettlementStatuses.NeedsReview; settlement.Reason = reason; settlement.ReasonItem = item;
        await db.SaveChangesAsync(ct);
        return settlement;
    }

    // Evidence that fits the columns it is stored in and names each line once. Anything else is not
    // stored; what it SAYS (an unsupported currency, a kind nobody posts) is judged later, by Check.
    private static bool WellFormed(ProcessorSettlement e) =>
        Text(e.PayoutId, 100) && !e.PayoutId.Contains(':', StringComparison.Ordinal)
        && Text(e.EvidenceId, 100) && Text(e.PayoutType, 20) && e.Currency is { Length: 3 }
        && e.Items is not null && e.Items.All(x => x is not null && Text(x.Item, 60) && Text(x.Kind, 20)
            && x.ProviderId is { Length: <= 100 } && x.Currency is { Length: 3 })
        && e.Items.Select(x => x.Item).Distinct(StringComparer.Ordinal).Count() == e.Items.Count;

    private static bool WellFormed(ProcessorPayout p) =>
        Text(p.PayoutId, 100) && !p.PayoutId.Contains(':', StringComparison.Ordinal)
        && Text(p.PayoutType, 20) && p.Currency is { Length: 3 } && Storable(p.Amount) && p.ObservedAt.Kind == DateTimeKind.Utc
        && p.State is ProcessorPayoutStates.Paid or ProcessorPayoutStates.Failed
        && p.Items is not null && p.Items.All(x => x is not null && Text(x.Item, 60) && Text(x.Kind, 20)
            && x.ProviderId is { Length: <= 100 } && x.Currency is { Length: 3 }
            && Storable(x.Gross) && Storable(x.Fee) && Storable(x.Net))
        && p.Items.Select(x => x.Item).Distinct(StringComparer.Ordinal).Count() == p.Items.Count;

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

    // An amount as NUMERIC(14,2) holds one: whole cents, and within twelve digits before the point.
    private static bool Storable(decimal amount) => Math.Abs(amount) <= 999999999999.99m && decimal.Round(amount, 2) == amount;

    // What the payout says, not how or when it was delivered: the observation time and the order of
    // the lines are left out, so a redelivery that differs only in those is the same evidence.
    private static string Fingerprint(ProcessorSettlement e) => PaymentEngine.Hash(JsonSerializer.Serialize(new
    {
        e.PayoutId,
        e.Account,
        e.Mode,
        e.Generation,
        e.PayoutType,
        e.BankAmount,
        e.Currency,
        e.BankId,
        e.BankDate,
        e.EvidenceId,
        Items = e.Items.OrderBy(x => x.Item, StringComparer.Ordinal).ToArray(),
    }));

    // What the processor says the payout is, not what became of it or when it said so: a second
    // notice that it was paid, under another event, is the same account of it.
    private static string Fingerprint(ProcessorPayout p) => PaymentEngine.Hash(JsonSerializer.Serialize(new
    {
        p.PayoutId,
        p.Account,
        p.Mode,
        p.Generation,
        p.PayoutType,
        p.Amount,
        p.Currency,
        p.BankId,
        Items = p.Items.OrderBy(x => x.Item, StringComparer.Ordinal).ToArray(),
    }));

    // The specification's checks, in its order: unsupported content, identify, tie. Builds the lines to
    // post as it goes; they are meaningful only when the result is null.
    //
    // Every line's net must follow from its own gross and fee, and every fee is bounded by what the
    // payment says. Otherwise a line could tie the payout to the bank while naming an arbitrary net,
    // and the difference would post as the PM's fee surplus.
    //
    // A payout the processor reported by itself has two figures to tie: its lines to what the
    // processor says it paid, and that to what the bank says it received. Before a bank has spoken
    // only the first can be checked, and a null result then means "could post", never "post".
    private static string? Check(FixtureBinding binding, PaymentSettlement settlement, List<PaymentSettlementItem> items,
        Dictionary<string, PaymentOperation> operations, HashSet<Guid> returned, ILookup<string, string> said,
        out List<SettlementLineRequest> lines)
    {
        lines = [];
        // The bank's word against the processor's, before anything the lines say: when they differ
        // nothing in the payout can be trusted to be what reached the bank.
        if (settlement.Banked && settlement.ProcessorAmount is { } paid && paid != settlement.BankAmount)
        { return SettlementReasons.BankAmountMismatch; }
        if (settlement.PayoutType != SettlementPayoutTypes.Standard || settlement.Currency != "USD"
            || settlement.BankId != binding.BankId || (settlement.Banked && settlement.BankDate == default)
            || items.Any(x => x.Currency != "USD" || x.Kind is not (SettlementItemKinds.Payment
                or SettlementItemKinds.Return or SettlementItemKinds.Fee)))
        { return "unsupported_settlement"; }
        if (items.Count == 0) { return "settlement_incomplete"; }
        // One line per payment and kind: a payout that names a payment twice contradicts itself.
        if (items.Where(x => x.Kind != SettlementItemKinds.Fee).GroupBy(x => (x.ProviderId, x.Kind)).Any(g => g.Count() > 1))
        { return "conflicting_evidence"; }
        // A payment and that payment's return in one payout (ADR-054, Known limits). A return posts
        // only against a receipt an earlier payout made, so one whose payment has no receipt yet is
        // something this model does not post; one whose payment has a receipt is paid a second time
        // here. Decided for the payout as a whole, before any line: each line alone is refused for
        // its own reason, and which came first would follow the order of the lines' names.
        // So is a pair whose payment this payout cannot identify: the two lines can never both pass,
        // the payment line wanting no receipt and the return line one, whatever becomes of it.
        var paired = items.Where(x => x.Kind == SettlementItemKinds.Return).Select(x => x.ProviderId)
            .Intersect(items.Where(x => x.Kind == SettlementItemKinds.Payment).Select(x => x.ProviderId)).ToArray();
        if (paired.Any(x => operations.TryGetValue(x, out var paid) && paid.JournalId is not null)) { return "conflicting_evidence"; }
        if (paired.Length > 0) { return "unsupported_settlement"; }

        foreach (var item in items)
        {
            if (item.Kind == SettlementItemKinds.Fee)
            {
                // A processor fee with no payment beside it: all of it is a shortfall.
                if (item.Fee <= 0m || item.Gross != 0m || item.Net != -item.Fee) { return "settlement_untied"; }
                lines.Add(new(item.Item, item.Kind, null, 0m, "", null, item.Fee));
                continue;
            }
            if (!operations.TryGetValue(item.ProviderId, out var operation)
                || operation.BankId != settlement.BankId || operation.Generation != settlement.Generation)
            { return "settlement_incomplete"; }
            var facts = said[item.ProviderId];

            if (item.Kind == SettlementItemKinds.Payment)
            {
                // Only a payment still waiting for its bank evidence. One that failed, or that staff
                // are reviewing or have closed, is not settled by a payout behind their backs.
                if (operation.JournalId is not null || operation.Status != "Processing") { return "conflicting_evidence"; }
                // Nor one of which a failure, a return, a refund, a dispute or a conflict has been
                // stored and not yet judged: its status still says it is waiting, and it is not.
                if (facts.Any(x => x is "Failed" or "Return" or "Refund" or "Dispute" or PaymentEngine.ConflictKind))
                { return "conflicting_evidence"; }
                if (item.Gross != operation.ChargedAmount || item.Fee < 0m || item.Net <= 0m || item.Gross - item.Fee != item.Net)
                { return "settlement_untied"; }
                lines.Add(new(item.Item, item.Kind, operation.TenantId, operation.Amount, operation.Method, null,
                    operation.Amount - item.Net));
            }
            else
            {
                // A return of something never receipted has nothing to reverse yet.
                if (operation.JournalId is null) { return "settlement_incomplete"; }
                // Only a payment that is settled, or whose return is already waiting for a decision.
                // A review that a refund or a dispute raised carries the same reason and is not one:
                // only a bank's return of the debit is posted as a return, so the review must hold a
                // return and nothing that says otherwise, judged or not.
                // Nor one of which a failure is stored: a collection that failed has nothing to return.
                if (returned.Contains(operation.Id) || facts.Any(x => x is "Failed" or "Refund" or "Dispute" or PaymentEngine.ConflictKind)
                    || !(operation.Status == "Settled" || (operation.Status == "NeedsReview"
                        && operation.Reason?.StartsWith("return_", StringComparison.Ordinal) == true && facts.Contains("Return"))))
                { return "conflicting_evidence"; }
                // Only the whole charge going back is a return this model posts.
                if (item.Gross != operation.ChargedAmount) { return "unsupported_settlement"; }
                // On a return the fee is what the processor gives back, at most the fee quoted. The bank
                // loses the charge less that. What is not given back is a shortfall; there is never a surplus.
                if (item.Fee < 0m || item.Fee > operation.QuotedFee || item.Net != -(item.Gross - item.Fee))
                { return "settlement_untied"; }
                lines.Add(new(item.Item, item.Kind, null, 0m, "", operation.JournalId, operation.QuotedFee - item.Fee));
            }
        }
        // To the bank's figure when there is one; it equals the processor's, or the check ended above.
        return items.Sum(x => x.Net) == (settlement.Banked ? settlement.BankAmount : settlement.ProcessorAmount) ? null : "settlement_untied";
    }
}
