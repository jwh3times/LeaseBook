using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// #468: a journal entry carries an owner-facing <c>description</c> and a staff-only
/// <c>internal_note</c>. The note is written once, at posting, through the single write path; a void's
/// reason becomes the reversal's note while its owner-facing description names what it corrected.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InternalNoteTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();

    [Fact]
    public async Task Internal_note_round_trips_through_the_posting_service()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        Guid withNote = default, blankNote = default, noNote = default;
        await scope.RunAsync(async () =>
        {
            withNote = await Posting(scope).PostAsync(Rent("n1") with { InternalNote = "Tenant disputes the late fee" }, ct);
            blankNote = await Posting(scope).PostAsync(Rent("n2") with { InternalNote = "   " }, ct);
            noNote = await Posting(scope).PostAsync(Rent("n3"), ct);
        }, ct);

        var entries = await ReadEntriesAsync(scope, [withNote, blankNote, noNote], ct);
        entries[withNote].Description.ShouldBe("Feb rent");
        entries[withNote].InternalNote.ShouldBe("Tenant disputes the late fee");
        entries[blankNote].InternalNote.ShouldBeNull("a blank note is no note — never an empty staff-only string");
        entries[noNote].InternalNote.ShouldBeNull();
    }

    [Fact]
    public async Task Every_staff_typed_event_carries_its_internal_note_onto_the_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        var events = new AccountingEvent[]
        {
            new RentCharged(_tenant, _property, _owner, null, new Money(1000m), Feb(1), "Rent", InternalNote: "note:rent"),
            new FeeCharged(_tenant, _property, _owner, null, new Money(50m), Feb(2), FeeKind.Other, "Fee", InternalNote: "note:fee"),
            new CreditIssued(_tenant, _property, _owner, new Money(10m), Feb(2), "Goodwill credit", InternalNote: "note:credit"),
            new PaymentReceived(_tenant, _property, _owner, new Money(300m), Feb(3), PaymentMethod.Ach, scope.TrustBankId,
                "Payment", InternalNote: "note:payment"),
            new DepositCollected(_tenant, _property, _owner, new Money(500m), Feb(3), scope.DepositBankId, "Deposit",
                InternalNote: "note:deposit"),
            new PrepaymentReceived(_tenant, _property, _owner, new Money(100m), Feb(3), scope.TrustBankId, "Prepayment",
                InternalNote: "note:prepayment"),
            new PrepaymentApplied(_tenant, _property, _owner, new Money(100m), Feb(4), scope.TrustBankId, "Applied",
                InternalNote: "note:prepayment-applied"),
            new DepositApplied(_tenant, _property, _owner, new Money(200m), Feb(4), scope.DepositBankId, scope.TrustBankId,
                DepositApplication.ToOwnerIncome, "Damages", InternalNote: "note:deposit-applied"),
            new InterestEarned(new Money(5m), Feb(5), scope.TrustBankId, "Interest", InternalNote: "note:interest"),
            new BankFeeCharged(new Money(2m), Feb(5), scope.TrustBankId, "Bank fee", InternalNote: "note:bank-fee"),
            new TrustTransfer(new Money(1m), Feb(5), scope.TrustBankId, scope.DepositBankId, "Transfer",
                InternalNote: "note:transfer"),
        };

        var ids = new List<(Guid Id, string Expected)>();
        await scope.RunAsync(async () =>
        {
            foreach (var e in events)
            {
                var id = await Events(scope).PostAsync(e, ct);
                ids.Add((id, NoteOf(e)));
            }
        }, ct);

        var entries = await ReadEntriesAsync(scope, ids.Select(i => i.Id).ToList(), ct);
        foreach (var (id, expected) in ids)
        {
            entries[id].InternalNote.ShouldBe(expected);
            entries[id].Description.ShouldNotBe(expected, "the note never replaces the owner-facing description");
        }
    }

    [Fact]
    public async Task A_void_stores_its_reason_as_the_internal_note_and_names_what_it_corrected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        Guid original = default, reversal = default;
        await scope.RunAsync(async () =>
        {
            original = await Posting(scope).PostAsync(Rent("v1") with { InternalNote = "original's own note" }, ct);
            reversal = await Reversal(scope).ReverseAsync(original, "keyed against the wrong tenant", Feb(20), ct);
        }, ct);

        var entries = await ReadEntriesAsync(scope, [original, reversal], ct);
        entries[reversal].Description.ShouldBe("Void — Feb rent");
        entries[reversal].InternalNote.ShouldBe("keyed against the wrong tenant");
        entries[reversal].ReversesEntryId.ShouldBe(original);

        // The original is untouched: append-only, its own note stays its own.
        entries[original].Description.ShouldBe("Feb rent");
        entries[original].InternalNote.ShouldBe("original's own note");

        // The accrual-only rent void has no cash lines, so it balances in cash only as 0 = 0; the
        // payment void below is what exercises the cash basis.
        AssertBalancesInBasis(await ReadLinesAsync(scope, reversal, ct), EntryBasis.Accrual);
    }

    [Fact]
    public async Task A_void_of_a_cash_entry_keeps_its_note_and_balances_in_both_bases()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        Guid payment = default, reversal = default;
        await scope.RunAsync(async () =>
        {
            // A receivable first, so the payment posts cash owner equity as well as the Both-basis bank line.
            await Events(scope).PostAsync(new RentCharged(
                _tenant, _property, _owner, null, new Money(1450m), Feb(1), "Feb rent"), ct);
            payment = await Events(scope).PostAsync(new PaymentReceived(
                _tenant, _property, _owner, new Money(500m), Feb(3), PaymentMethod.Check, scope.TrustBankId,
                "Check 1042", InternalNote: "Paid by guarantor"), ct);
            reversal = await Reversal(scope).ReverseAsync(payment, "Check returned unpaid", Feb(10), ct);
        }, ct);

        var entries = await ReadEntriesAsync(scope, [payment, reversal], ct);
        entries[reversal].Description.ShouldBe("Void — Check 1042");
        entries[reversal].InternalNote.ShouldBe("Check returned unpaid");
        entries[payment].InternalNote.ShouldBe("Paid by guarantor");

        var lines = await ReadLinesAsync(scope, reversal, ct);
        lines.ShouldContain(l => l.Basis == EntryBasis.Cash, "owner equity is recognized on the cash basis");
        lines.ShouldContain(l => l.Basis == EntryBasis.Both, "the bank line is posted on both bases");
        AssertBalancesInEveryBasis(lines);
    }

    /// <summary>Σ debits = Σ credits in each basis, over lines that actually exist there (never 0 = 0).</summary>
    private static void AssertBalancesInEveryBasis(IReadOnlyList<LineView> lines)
    {
        AssertBalancesInBasis(lines, EntryBasis.Cash);
        AssertBalancesInBasis(lines, EntryBasis.Accrual);
    }

    private static void AssertBalancesInBasis(IReadOnlyList<LineView> lines, EntryBasis basis)
    {
        var inBasis = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).ToList();
        var debits = inBasis.Sum(l => l.Debit ?? 0m);
        debits.ShouldBeGreaterThan(0m, $"the reversal must have {basis} lines, or its {basis} balance is vacuous");
        debits.ShouldBe(inBasis.Sum(l => l.Credit ?? 0m), $"reversal balances in {basis}");
    }

    [Fact]
    public async Task A_void_of_an_entry_with_no_description_reads_as_a_plain_void()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        Guid reversal = default;
        await scope.RunAsync(async () =>
        {
            var original = await Posting(scope).PostAsync(Rent("v2") with { Description = null }, ct);
            reversal = await Reversal(scope).ReverseAsync(original, "duplicate", Feb(20), ct);
        }, ct);

        var entry = (await ReadEntriesAsync(scope, [reversal], ct))[reversal];
        entry.Description.ShouldBe("Void");
        entry.InternalNote.ShouldBe("duplicate");
    }

    [Fact]
    public async Task The_owner_statement_shows_the_void_by_what_it_corrected_and_never_the_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        OwnerStatement statement = null!;
        await scope.RunAsync(async () =>
        {
            var charge = await Events(scope).PostAsync(new RentCharged(
                _tenant, _property, _owner, null, new Money(100m), May(10), "May rent",
                InternalNote: "STAFF-ONLY charge note"), ct);
            await Reversal(scope).ReverseAsync(charge, "STAFF-ONLY void reason", May(20), ct);

            statement = (await new GetOwnerStatementDataHandler(scope.Db).Handle(
                new GetOwnerStatementData([_owner], null, 2026, 5, "accrual"), ct)).ByOwner[_owner];
        }, ct);

        var descriptions = statement.Sections.SelectMany(s => s.Lines).Select(l => l.Description).ToList();
        descriptions.ShouldBe(["May rent", "Void — May rent"]);
        descriptions.ShouldAllBe(d => !d.Contains("STAFF-ONLY"));
    }

    private static string NoteOf(AccountingEvent e) =>
        (string)e.GetType().GetProperty("InternalNote")!.GetValue(e)!;

    private static async Task<Dictionary<Guid, JournalEntry>> ReadEntriesAsync(
        OrgScope scope, IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        Dictionary<Guid, JournalEntry> entries = [];
        await scope.RunAsync(async () =>
            entries = await scope.Db.Set<JournalEntry>().AsNoTracking()
                .Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct), ct);
        return entries;
    }

    private PostEntryRequest Rent(string sourceRef) => new(
        Feb(1), "RentCharged", null, "Feb rent", sourceRef,
        [
            new PostLineRequest(AccountCodes.TenantReceivable, new Money(1450m), null, EntryBasis.Accrual,
                PropertyId: _property, OwnerId: _owner, TenantId: _tenant),
            new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(1450m), EntryBasis.Accrual,
                PropertyId: _property, OwnerId: _owner),
        ]);

    private static DateOnly Feb(int day) => new(2026, 2, day);

    private static DateOnly May(int day) => new(2026, 5, day);
}
