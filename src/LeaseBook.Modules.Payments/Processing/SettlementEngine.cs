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
/// Stores, checks and posts payout batches (ADR-053). A payout posts completely or not at all, and a
/// payout that cannot post says why in a stable reason. All methods run on the caller's organization
/// transaction; no provider I/O occurs here.
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
        var existing = await db.Set<PaymentSettlement>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.Account == binding.Account && x.PayoutId == evidence.PayoutId, ct);
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
            PayoutId = evidence.PayoutId,
            PayoutType = evidence.PayoutType,
            BankId = evidence.BankId,
            BankDate = evidence.BankDate,
            BankAmount = evidence.BankAmount,
            Currency = evidence.Currency,
            EvidenceId = evidence.EvidenceId,
            Fingerprint = fingerprint,
        };
        db.Add(settlement);
        db.AddRange(evidence.Items.Select(x => new PaymentSettlementItem
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
        return settlement.Id;
    }

    /// <summary>
    /// Checks a stored payout and posts it if it can be posted. <paramref name="confirmedBy"/> is the
    /// administrator posting a payout that contains a return; without one, such a payout waits.
    /// Returns null when the payout is unknown here.
    /// </summary>
    public async Task<PaymentSettlement?> EvaluateAsync(FixtureBinding binding, Guid id, Guid? confirmedBy, CancellationToken ct)
    {
        await payments.RequireFixtureAsync(binding, ct);
        await payments.LockAsync("settlement-id:" + id, ct);
        var settlement = await db.Set<PaymentSettlement>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (settlement is null) { return null; }
        if (settlement.Generation != binding.Generation || settlement.Account != binding.Account)
        { throw new PaymentUnavailableException(); }
        if (settlement.Status == SettlementStatuses.Posted) { return settlement; }
        if (settlement.Status == SettlementStatuses.Closed || settlement.Reason == "conflicting_evidence")
        { throw new PaymentConflictException(); }

        var items = await db.Set<PaymentSettlementItem>().Where(x => x.SettlementId == id).OrderBy(x => x.Item).ToListAsync(ct);
        var providerIds = items.Where(x => x.Kind != SettlementItemKinds.Fee).Select(x => x.ProviderId).Distinct().ToArray();
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

        var reason = Check(binding, settlement, items, operations, returned, out var lines);
        if (reason is null && items.Any(x => x.Kind == SettlementItemKinds.Return) && confirmedBy is null)
        { reason = "settlement_requires_confirmation"; }
        if (reason is not null) { return await HoldAsync(settlement, reason, null, ct); }

        var outcome = await ledger.PostSettlementAsync(settlement.BankId, settlement.BankDate, settlement.PayoutId, lines, ct);
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
        if (settlement.Generation != binding.Generation || settlement.Account != binding.Account)
        { throw new PaymentUnavailableException(); }
        if (settlement.Status == SettlementStatuses.Closed) { return settlement; }
        if (settlement.Status == SettlementStatuses.Posted) { throw new PaymentConflictException(); }
        settlement.Status = SettlementStatuses.Closed; settlement.ReviewNote = note;
        settlement.ReviewClosedAt = Now; settlement.ReviewClosedBy = userId;
        await db.SaveChangesAsync(ct);
        return settlement;
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

    private static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max;

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

    // The specification's checks, in its order: unsupported content, identify, tie. Builds the lines to
    // post as it goes; they are meaningful only when the result is null.
    //
    // Every line's net must follow from its own gross and fee, and every fee is bounded by what the
    // payment says. Otherwise a line could tie the payout to the bank while naming an arbitrary net,
    // and the difference would post as the PM's fee surplus.
    private static string? Check(FixtureBinding binding, PaymentSettlement settlement, List<PaymentSettlementItem> items,
        Dictionary<string, PaymentOperation> operations, HashSet<Guid> returned, out List<SettlementLineRequest> lines)
    {
        lines = [];
        if (items.Count == 0) { return "settlement_incomplete"; }
        if (settlement.PayoutType != SettlementPayoutTypes.Standard || settlement.Currency != "USD"
            || settlement.BankId != binding.BankId || settlement.BankDate == default
            || items.Any(x => x.Currency != "USD" || x.Kind is not (SettlementItemKinds.Payment
                or SettlementItemKinds.Return or SettlementItemKinds.Fee)))
        { return "unsupported_settlement"; }
        // One line per payment and kind: a payout that names a payment twice contradicts itself.
        if (items.Where(x => x.Kind != SettlementItemKinds.Fee).GroupBy(x => (x.ProviderId, x.Kind)).Any(g => g.Count() > 1))
        { return "conflicting_evidence"; }

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

            if (item.Kind == SettlementItemKinds.Payment)
            {
                // Only a payment still waiting for its bank evidence. One that failed, or that staff
                // are reviewing or have closed, is not settled by a payout behind their backs.
                if (operation.JournalId is not null || operation.Status != "Processing") { return "conflicting_evidence"; }
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
                if (returned.Contains(operation.Id) || !(operation.Status == "Settled" || (operation.Status == "NeedsReview"
                    && operation.Reason?.StartsWith("return_", StringComparison.Ordinal) == true)))
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
        return items.Sum(x => x.Net) == settlement.BankAmount ? null : "settlement_untied";
    }
}
