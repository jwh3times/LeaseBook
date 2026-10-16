using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Payments.Contracts;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.SharedKernel;

namespace LeaseBook.Tests.Payments.Support;

/// <summary>A receipt Payments asked the ledger to post.</summary>
internal sealed record ReceiptRequest(Guid TenantId, Guid BankId, decimal Amount, DateOnly Date, string Method,
    string Memo, string SourceRef, Guid EntryId);

/// <summary>A return Payments asked the ledger to post.</summary>
internal sealed record ReturnRequest(Guid ReceiptJournalId, DateOnly Date, string SourceRef, string Note);

/// <summary>A payout Payments asked the ledger to post.</summary>
internal sealed record SettlementRequest(Guid BankId, DateOnly BankDate, string PayoutReference, string ReceiptMemo,
    IReadOnlyList<SettlementLineRequest> Lines);

/// <summary>
/// The ledger port, recording what Payments asked of it. It answers as Accounting does when nothing
/// refuses: a receipt or a return is one entry, and a payout posts one entry per receipt and return
/// and one per non-zero fee difference. A test sets <see cref="ReturnRefusal"/> or
/// <see cref="SettlementRefusal"/> to make the ledger refuse instead, which posts nothing.
/// <para>
/// Each entry id it returns is a real journal entry, because the payment tables hold foreign keys to
/// the journal: a one-cent owner contribution, posted through Accounting's public event surface. Its
/// lines mean nothing here and are never read.
/// </para>
/// </summary>
internal sealed class RecordingLedger(IAccountingEvents events, Guid ownerId, Guid bankId) : IPaymentLedger
{
    private int _entries;

    public List<ReceiptRequest> Receipts { get; } = [];

    public List<ReturnRequest> Returns { get; } = [];

    public List<SettlementRequest> Settlements { get; } = [];

    /// <summary>When set, a return is refused with this code and nothing is posted.</summary>
    public string? ReturnRefusal { get; set; }

    /// <summary>When set, a payout is refused with this code, on this item, and nothing is posted.</summary>
    public (string Code, string? Item)? SettlementRefusal { get; set; }

    /// <summary>
    /// When set, a payout's answer also names a receipt for this item, which the payout does not
    /// hold: a ledger answering outside its contract.
    /// </summary>
    public string? StrayPostingItem { get; set; }

    public async Task<Guid> RecordSettledReceiptAsync(Guid tenantId, Guid bankId, decimal amount, DateOnly date,
        string method, string memo, string sourceRef, CancellationToken ct)
    {
        var entryId = await EntryAsync(date, ct);
        Receipts.Add(new(tenantId, bankId, amount, date, method, memo, sourceRef, entryId));
        return entryId;
    }

    public async Task<PaymentReturnOutcome> ReturnSettledReceiptAsync(Guid receiptJournalId, DateOnly date,
        string sourceRef, string note, CancellationToken ct)
    {
        Returns.Add(new(receiptJournalId, date, sourceRef, note));
        return ReturnRefusal is { } refusal ? new(null, refusal) : new(await EntryAsync(date, ct), null);
    }

    public async Task<SettlementOutcome> PostSettlementAsync(Guid bankId, DateOnly bankDate, string payoutReference,
        string receiptMemo, IReadOnlyList<SettlementLineRequest> lines, CancellationToken ct)
    {
        Settlements.Add(new(bankId, bankDate, payoutReference, receiptMemo, lines));
        if (SettlementRefusal is { } refusal) { return new([], refusal.Code, refusal.Item); }
        var postings = new List<SettlementPostingResult>();
        foreach (var line in lines)
        {
            if (line.Kind == SettlementItemKinds.Payment) { postings.Add(new(line.Item, "Receipt", await EntryAsync(bankDate, ct))); }
            if (line.Kind == SettlementItemKinds.Return) { postings.Add(new(line.Item, "Return", await EntryAsync(bankDate, ct))); }
            if (line.FeeDifference != 0m) { postings.Add(new(line.Item, "FeeDifference", await EntryAsync(bankDate, ct))); }
        }
        if (StrayPostingItem is { } stray) { postings.Add(new(stray, "Receipt", await EntryAsync(bankDate, ct))); }
        return new(postings, null, null);
    }

    private Task<Guid> EntryAsync(DateOnly date, CancellationToken ct) => events.PostAsync(new OwnerContribution(
        ownerId, null, new Money(0.01m), date, bankId, "Payments test stand-in entry",
        $"payments-test:{Guid.NewGuid():N}:{++_entries}"), ct);
}

/// <summary>Whether a tenant may pay on a bank on a date. Everyone may, unless the test says otherwise.</summary>
internal sealed class StubEligibility : IPaymentEligibility
{
    public bool Eligible { get; set; } = true;

    public List<PaymentEligibilityRequest> Asked { get; } = [];

    public Task<IReadOnlyDictionary<PaymentEligibilityRequest, bool>> ReadAsync(
        IReadOnlyList<PaymentEligibilityRequest> requests, CancellationToken ct)
    {
        Asked.AddRange(requests);
        return Task.FromResult<IReadOnlyDictionary<PaymentEligibilityRequest, bool>>(
            requests.ToDictionary(x => x, _ => Eligible));
    }
}

/// <summary>
/// The organization's fee rules: a fixed fee for both methods, none by default. A rule with only a
/// fixed fee quotes exactly that fee, which keeps the figures in the tests readable.
/// </summary>
internal sealed class StubFeeRules : IPaymentFeeRules
{
    public decimal Fee { get; set; }

    public Task<IReadOnlyDictionary<string, ConvenienceFeeRule>> ReadAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyDictionary<string, ConvenienceFeeRule>>(new Dictionary<string, ConvenienceFeeRule>
        {
            [PaymentMethods.Card] = new(0, Fee, null),
            [PaymentMethods.Ach] = new(0, Fee, null),
        });
}
