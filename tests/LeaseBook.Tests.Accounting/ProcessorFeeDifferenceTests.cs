using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The processor fee difference template (ADR-053, #500). <see cref="PaymentSettlementEvidenceTests"/>
/// shows the lines the model needs and what an unguarded shortfall does; these pin the template that
/// posts them, and the guard that keeps a shortfall inside the PM's held fees.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ProcessorFeeDifferenceTests(PostgresFixture fixture)
{
    private static readonly DateOnly Date = new(2026, 2, 6);

    [Fact]
    public async Task A_surplus_raises_the_bank_and_the_pm_fees_held_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var id = await PostAsync(scope, Difference(scope, 0.40m, FeeDifferenceDirection.Surplus, "payout:po_1:3:fee"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.TrustBank(scope.TrustBankId), 0.40m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            new(AccountCodes.PmIncome, null, 0.40m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await AssertEntryAsync(scope, id, "Surplus", "payout:po_1:3:fee", ct);
        (await HeldFeesAsync(scope, ct)).ShouldBe(0.40m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_shortfall_lowers_the_bank_and_the_pm_fees_held_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, new InterestEarned(new Money(80m), Date, scope.TrustBankId, "held fees"), ct);

        var id = await PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall, "payout:po_1:2:fee"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.PmIncome, 7.50m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            new(AccountCodes.TrustBank(scope.TrustBankId), null, 7.50m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await AssertEntryAsync(scope, id, "Shortfall", "payout:po_1:2:fee", ct);
        (await HeldFeesAsync(scope, ct)).ShouldBe(72.50m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_shortfall_may_take_held_fees_to_exactly_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, new InterestEarned(new Money(7.50m), Date, scope.TrustBankId, "held fees"), ct);

        await PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct);

        (await HeldFeesAsync(scope, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("7.49")]
    public async Task A_shortfall_beyond_held_fees_is_refused_and_posts_nothing(string heldFees)
    {
        var held = decimal.Parse(heldFees, System.Globalization.CultureInfo.InvariantCulture);
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        if (held > 0)
        {
            await PostAsync(scope, new InterestEarned(new Money(held), Date, scope.TrustBankId, "held fees"), ct);
        }
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        (error.Code, error.Shortfall, error.Held, error.BankAccountId)
            .ShouldBe(("pm_fees_insufficient", 7.50m, held, scope.TrustBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        (await HeldFeesAsync(scope, ct)).ShouldBe(held);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task Fees_held_in_another_bank_cannot_cover_a_shortfall_in_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, new InterestEarned(new Money(80m), Date, scope.DepositBankId, "held elsewhere"), ct);

        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        (error.Held, error.BankAccountId).ShouldBe((0m, scope.TrustBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        (await HeldFeesAsync(scope, ct)).ShouldBe(0m);
    }

    [Fact]
    public async Task Fees_earned_after_the_bank_date_cannot_cover_a_backdated_shortfall()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // Nothing was held on the bank date. Today 80.00 is, and a read of today's balance would pass.
        await PostAsync(scope, new InterestEarned(new Money(80m), Date.AddDays(20), scope.TrustBankId, "earned later"), ct);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        error.Held.ShouldBe(0m);
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task A_backdated_shortfall_is_refused_when_held_fees_dipped_below_it_since()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // 80.00 held on the bank date, all of it gone ten days later, 50.00 earned after that. The
        // shortfall fits on the bank date and fits today, and would still leave -7.50 held in between.
        await PostAsync(scope, new InterestEarned(new Money(80m), Date, scope.TrustBankId, "held"), ct);
        await PostAsync(scope, new BankFeeCharged(new Money(80m), Date.AddDays(10), scope.TrustBankId, "drained"), ct);
        await PostAsync(scope, new InterestEarned(new Money(50m), Date.AddDays(20), scope.TrustBankId, "earned later"), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        error.Held.ShouldBe(0m);
        (await HeldFeesAsync(scope, ct)).ShouldBe(50m);
    }

    [Theory]
    [InlineData(FeeDifferenceDirection.Surplus)]
    [InlineData(FeeDifferenceDirection.Shortfall)]
    public async Task A_bank_that_is_not_a_trust_bank_is_refused(FeeDifferenceDirection direction)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // Held fees exist in the operating bank, so the shortfall guard itself would pass.
        await PostAsync(scope, new InterestEarned(new Money(80m), Date, scope.OperatingBankId, "operating"), ct);
        var before = await EntryCountAsync(scope, ct);

        await Should.ThrowAsync<UnknownAccountException>(() => PostAsync(scope, new ProcessorFeeDifference(
            new Money(7.50m), direction, Date, scope.OperatingBankId, "Processor fee difference"), ct));

        (await EntryCountAsync(scope, ct)).ShouldBe(before);
    }

    [Theory]
    [InlineData(FeeDifferenceDirection.Surplus, 0)]
    [InlineData(FeeDifferenceDirection.Shortfall, 0)]
    [InlineData(FeeDifferenceDirection.Surplus, -5)]
    [InlineData(FeeDifferenceDirection.Shortfall, -5)]
    public async Task A_difference_that_is_not_a_positive_amount_is_refused(FeeDifferenceDirection direction, int amount)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, new InterestEarned(new Money(80m), Date, scope.TrustBankId, "held"), ct);
        var before = await EntryCountAsync(scope, ct);

        await Should.ThrowAsync<InvalidLineException>(
            () => PostAsync(scope, Difference(scope, amount, direction), ct));

        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        (await HeldFeesAsync(scope, ct)).ShouldBe(80m);
    }

    private static ProcessorFeeDifference Difference(
        OrgScope scope, decimal amount, FeeDifferenceDirection direction, string? sourceRef = null) =>
        new(new Money(amount), direction, Date, scope.TrustBankId, "Processor fee difference", sourceRef);

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task AssertEntryAsync(OrgScope scope, Guid id, string subtype, string sourceRef, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct);
            (entry.EventType, entry.EventSubtype, entry.EntryDate, entry.SourceRef)
                .ShouldBe(("ProcessorFeeDifference", subtype, Date, sourceRef));
        }, ct);

    private static async Task<decimal> HeldFeesAsync(OrgScope scope, CancellationToken ct)
    {
        decimal held = 0;
        await scope.RunAsync(async () => held = await new BalanceReader(scope.Db).HeldFeesAsync(scope.TrustBankId, ct), ct);
        return held;
    }

    private static async Task<int> EntryCountAsync(OrgScope scope, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }

    private static async Task AssertCleanAsync(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var checks = new InvariantChecks(scope.Db);
            (await checks.CheckCoreAsync(ct)).ShouldBeEmpty();
            (await checks.CheckTrustEquationAsync(ct)).ShouldBeEmpty();
        }, ct);
}
