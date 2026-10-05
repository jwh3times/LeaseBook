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

        var id = await Post(scope, Difference(scope, 0.40m, FeeDifferenceDirection.Surplus, "payout:po_1:3:fee"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.TrustBank(scope.TrustBankId), 0.40m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            new(AccountCodes.PmIncome, null, 0.40m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await AssertEntry(scope, id, "Surplus", "payout:po_1:3:fee", ct);
        (await HeldFees(scope, ct)).ShouldBe(0.40m);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_shortfall_lowers_the_bank_and_the_pm_fees_held_in_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await Post(scope, new InterestEarned(new Money(80m), Date, scope.TrustBankId, "held fees"), ct);

        var id = await Post(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall, "payout:po_1:2:fee"), ct);

        (await ReadLinesAsync(scope, id, ct)).ShouldBe(
        [
            new(AccountCodes.PmIncome, 7.50m, null, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
            new(AccountCodes.TrustBank(scope.TrustBankId), null, 7.50m, EntryBasis.Both, null, null, null, null, scope.TrustBankId),
        ], ignoreOrder: true);
        await AssertEntry(scope, id, "Shortfall", "payout:po_1:2:fee", ct);
        (await HeldFees(scope, ct)).ShouldBe(72.50m);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task A_shortfall_may_take_held_fees_to_exactly_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await Post(scope, new InterestEarned(new Money(7.50m), Date, scope.TrustBankId, "held fees"), ct);

        await Post(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct);

        (await HeldFees(scope, ct)).ShouldBe(0m);
        await AssertClean(scope, ct);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7.49)]
    public async Task A_shortfall_beyond_held_fees_is_refused_and_posts_nothing(decimal held)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        if (held > 0)
        {
            await Post(scope, new InterestEarned(new Money(held), Date, scope.TrustBankId, "held fees"), ct);
        }
        var before = await Entries(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => Post(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        (error.Code, error.Shortfall, error.Held, error.BankAccountId)
            .ShouldBe(("pm_fees_insufficient", 7.50m, held, scope.TrustBankId));
        (await Entries(scope, ct)).ShouldBe(before);
        (await HeldFees(scope, ct)).ShouldBe(held);
        await AssertClean(scope, ct);
    }

    [Fact]
    public async Task Fees_held_in_another_bank_cannot_cover_a_shortfall_in_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await Post(scope, new InterestEarned(new Money(80m), Date, scope.DepositBankId, "held elsewhere"), ct);

        await Should.ThrowAsync<PmFeesInsufficientException>(
            () => Post(scope, Difference(scope, 7.50m, FeeDifferenceDirection.Shortfall), ct));

        (await HeldFees(scope, ct)).ShouldBe(0m);
    }

    private static ProcessorFeeDifference Difference(
        OrgScope scope, decimal amount, FeeDifferenceDirection direction, string? sourceRef = null) =>
        new(new Money(amount), direction, Date, scope.TrustBankId, "Processor fee difference", sourceRef);

    private static async Task<Guid> Post(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task AssertEntry(OrgScope scope, Guid id, string subtype, string sourceRef, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var entry = await scope.Db.Set<JournalEntry>().AsNoTracking().SingleAsync(e => e.Id == id, ct);
            (entry.EventType, entry.EventSubtype, entry.EntryDate, entry.SourceRef)
                .ShouldBe(("ProcessorFeeDifference", subtype, Date, sourceRef));
        }, ct);

    private static async Task<decimal> HeldFees(OrgScope scope, CancellationToken ct)
    {
        decimal held = 0;
        await scope.RunAsync(async () => held = await new BalanceReader(scope.Db).HeldFeesAsync(scope.TrustBankId, ct), ct);
        return held;
    }

    private static async Task<int> Entries(OrgScope scope, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }

    private static async Task AssertClean(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var checks = new InvariantChecks(scope.Db);
            (await checks.CheckCoreAsync(ct)).ShouldBeEmpty();
            (await checks.CheckTrustEquationAsync(ct)).ShouldBeEmpty();
        }, ct);
}
