using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The guarded payment return (#490, ADR-052). <see cref="PaymentLifecycleEvidenceTests"/> shows what the
/// unguarded reversal does to a spent receipt; these show the command refusing exactly those cases and
/// mirroring the receipt when nothing has been spent.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReturnTenantPaymentTests(PostgresFixture fixture)
{
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private static readonly DateOnly ReceiptDate = new(2026, 2, 3);
    private static readonly DateOnly ReturnDate = new(2026, 3, 5);

    [Fact]
    public async Task An_untouched_receipt_is_mirrored_line_for_line_on_the_return_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 1200m), ct);
        var original = await ReadLinesAsync(scope, receipt, ct);
        // The receipt's own month is closed; the return lands in the open month the bank dated it.
        await scope.RunAsync(() => Periods(scope).CloseAsync(2026, 2, ct), ct);

        var reversal = await Return(scope, receipt, ReturnDate, ct);

        (await ReadLinesAsync(scope, reversal, ct))
            .ShouldBe(original.Select(l => l with { Debit = l.Credit, Credit = l.Debit }), ignoreOrder: true);
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == reversal, ct);
            (entry.ReversesEntryId, entry.EntryDate, entry.SourceRef, entry.InternalNote)
                .ShouldBe((receipt, ReturnDate, "return:1", "bank return"));
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    [Fact]
    public async Task Consumed_prepaid_credit_blocks_the_return_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 1000m, ct);
        var receipt = await Post(scope, Receipt(scope, 1200m), ct);
        await Charge(scope, 200m, ct);
        await Post(scope, new PrepaymentApplied(_tenant, _property, _owner, new Money(200m),
            new DateOnly(2026, 2, 4), scope.TrustBankId, "consume excess"), ct);

        var error = await Should.ThrowAsync<PaymentReturnBlockedException>(() => Return(scope, receipt, ReturnDate, ct));

        error.Code.ShouldBe("return_prepayment_consumed");
        await AssertNothingReversed(scope, ct);
    }

    [Fact]
    public async Task Equity_held_in_another_bank_cannot_cover_a_return_drawn_on_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        // The owner's total stays at 1,000, but nothing is left in the bank the receipt went into.
        await Post(scope, new OwnerContribution(_owner, _property, new Money(1000m), ReceiptDate,
            scope.DepositBankId, "contribution held elsewhere"), ct);
        await Post(scope, new OwnerDisbursed(_owner, new Money(600m), ReceiptDate, scope.TrustBankId, "draw"), ct);

        var error = await Should.ThrowAsync<PaymentReturnBlockedException>(() => Return(scope, receipt, ReturnDate, ct));

        error.Code.ShouldBe("return_owner_funds_disbursed");
        await AssertNothingReversed(scope, ct);
    }

    [Fact]
    public async Task An_overdrawn_balance_in_another_bank_blocks_the_return_through_the_owner_total()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        // This bank still holds the 600, so the per-bank read passes; the owner as a whole holds nothing.
        await Post(scope, new VendorPaid(_owner, _property, new Money(600m), ReceiptDate, scope.DepositBankId,
            "Plumber", "repair"), ct);

        var error = await Should.ThrowAsync<PaymentReturnBlockedException>(() => Return(scope, receipt, ReturnDate, ct));

        error.Code.ShouldBe("return_owner_funds_disbursed");
        await AssertNothingReversed(scope, ct);
    }

    [Fact]
    public async Task Money_that_arrived_after_the_return_date_cannot_cover_a_backdated_reversal()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        await Post(scope, new OwnerDisbursed(_owner, new Money(600m), new DateOnly(2026, 2, 28), scope.TrustBankId, "draw"), ct);
        // Today the owner holds 600 again, but from the return date until this receipt the reversal
        // would leave the owner 600 overdrawn - on a March statement, for one.
        await Charge(scope, 600m, ct);
        await Post(scope, Receipt(scope, 600m, new DateOnly(2026, 4, 10), "receipt:2"), ct);

        var error = await Should.ThrowAsync<PaymentReturnBlockedException>(() => Return(scope, receipt, ReturnDate, ct));

        error.Code.ShouldBe("return_owner_funds_disbursed");
        await AssertNothingReversed(scope, ct);
    }

    [Fact]
    public async Task A_return_dated_before_its_receipt_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);

        var error = await Should.ThrowAsync<PaymentReturnBlockedException>(
            () => Return(scope, receipt, ReceiptDate.AddDays(-1), ct));

        error.Code.ShouldBe("return_precedes_receipt");
        await AssertNothingReversed(scope, ct);
    }

    [Fact]
    public async Task Only_a_payment_receipt_can_be_returned_and_only_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        var charge = await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);

        await Should.ThrowAsync<EntryNotFoundException>(() => Return(scope, charge, ReturnDate, ct));
        await Return(scope, receipt, ReturnDate, ct);
        await Should.ThrowAsync<AlreadyReversedException>(() => Return(scope, receipt, ReturnDate, ct));
    }

    [Fact]
    public async Task A_return_dated_in_a_closed_period_is_refused_without_moving_the_date()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await Charge(scope, 600m, ct);
        var receipt = await Post(scope, Receipt(scope, 600m), ct);
        await scope.RunAsync(() => Periods(scope).CloseAsync(ReturnDate.Year, ReturnDate.Month, ct), ct);

        await Should.ThrowAsync<PeriodClosedException>(() => Return(scope, receipt, ReturnDate, ct));
        await AssertNothingReversed(scope, ct);
    }

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

    private Task<Guid> Charge(OrgScope scope, decimal amount, CancellationToken ct) => Post(scope,
        new RentCharged(_tenant, _property, _owner, null, new Money(amount), new DateOnly(2026, 2, 1), "rent"), ct);

    private PaymentReceived Receipt(OrgScope scope, decimal amount, DateOnly? date = null, string sourceRef = "receipt:1") =>
        new(_tenant, _property, _owner, new Money(amount), date ?? ReceiptDate, PaymentMethod.Ach,
            scope.TrustBankId, "simulated settlement evidence", sourceRef);

    private static async Task<Guid> Post(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<Guid> Return(OrgScope scope, Guid receipt, DateOnly date, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = (await new ReturnTenantPaymentHandler(
                scope.Db, new PostingLock(scope.Db, scope.Tenant), Reversal(scope))
            .Handle(new ReturnTenantPayment(receipt, date, "return:1", "bank return"), ct)).EntryId, ct);
        return id;
    }

    private static Task AssertNothingReversed(OrgScope scope, CancellationToken ct) => scope.RunAsync(async () =>
    {
        (await scope.Db.Set<JournalEntry>().CountAsync(e => e.ReversesEntryId != null, ct)).ShouldBe(0);
        (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
    }, ct);
}
