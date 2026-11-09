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
/// The guards on the two bank adjustments that draw on the PM's held fees (#553, ADR-014 addendum). A
/// bank fee, and a transfer out of a bank, are paid from the management fees held in that bank and
/// from nothing else: past zero the entry would still balance and the trust equation would still hold,
/// with a negative held-fees term standing for money owed to owners and tenants. The floor is the one
/// the processor fee shortfall reads (ADR-053) — the lowest held balance on or after the entry's date.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class BankAdjustmentGuardTests(PostgresFixture fixture)
{
    private static readonly DateOnly Date = new(2026, 2, 6);

    [Theory]
    [InlineData("0")]
    [InlineData("24.99")]
    public async Task A_bank_fee_beyond_the_held_fees_is_refused_and_posts_nothing(string heldFees)
    {
        var held = decimal.Parse(heldFees, System.Globalization.CultureInfo.InvariantCulture);
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        if (held > 0)
        {
            await PostAsync(scope, Interest(scope.TrustBankId, held, Date), ct);
        }
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Fee(scope.TrustBankId, 25m, Date), ct));

        (error.Code, error.Shortfall, error.Held, error.BankAccountId)
            .ShouldBe(("pm_fees_insufficient", 25m, held, scope.TrustBankId));
        error.Message.ShouldNotContain("processor");
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(held);
    }

    [Fact]
    public async Task A_bank_fee_may_take_held_fees_to_exactly_zero()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 25m, Date), ct);

        await PostAsync(scope, Fee(scope.TrustBankId, 25m, Date), ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_backdated_bank_fee_is_refused_when_held_fees_dipped_below_it_since()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // 80.00 held on the fee's date, all of it gone ten days later, 50.00 earned after that. Today's
        // 50.00 covers the 25.00 fee, and so did the fee's own date, but it would leave -25.00 in between.
        await PostAsync(scope, Interest(scope.TrustBankId, 80m, Date), ct);
        await PostAsync(scope, Fee(scope.TrustBankId, 80m, Date.AddDays(10)), ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 50m, Date.AddDays(20)), ct);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Fee(scope.TrustBankId, 25m, Date), ct));

        error.Held.ShouldBe(0m);
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(50m);
    }

    [Fact]
    public async Task Fees_held_in_another_bank_cannot_cover_a_bank_fee_in_this_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.DepositBankId, 80m, Date), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Fee(scope.TrustBankId, 25m, Date), ct));

        (error.Held, error.BankAccountId).ShouldBe((0m, scope.TrustBankId));
    }

    [Fact]
    public async Task A_transfer_beyond_the_source_banks_held_fees_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 99.99m, Date), ct);
        // The destination holds plenty; it only gains from a transfer, so its fees cannot cover one.
        await PostAsync(scope, Interest(scope.DepositBankId, 500m, Date), ct);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Transfer(scope.TrustBankId, scope.DepositBankId, 100m, Date), ct));

        (error.Code, error.Shortfall, error.Held, error.BankAccountId)
            .ShouldBe(("pm_fees_insufficient", 100m, 99.99m, scope.TrustBankId));
        error.Message.ShouldNotContain("processor");
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
    }

    [Fact]
    public async Task A_transfer_the_source_covers_posts_whatever_the_destination_holds()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 100m, Date), ct);

        await PostAsync(scope, Transfer(scope.TrustBankId, scope.DepositBankId, 100m, Date), ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        (await HeldFeesAsync(scope, scope.DepositBankId, ct)).ShouldBe(100m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_backdated_transfer_is_refused_when_the_source_dipped_below_it_since()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 80m, Date), ct);
        await PostAsync(scope, Fee(scope.TrustBankId, 80m, Date.AddDays(10)), ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 50m, Date.AddDays(20)), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Transfer(scope.TrustBankId, scope.DepositBankId, 25m, Date), ct));

        error.Held.ShouldBe(0m);
    }

    [Theory]
    [InlineData(TransferEnd.Destination)]
    [InlineData(TransferEnd.Source)]
    public async Task A_transfer_with_a_pm_operating_bank_at_either_end_is_refused(TransferEnd end)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // Fees are held at both ends, so the floor guard alone would let either transfer through.
        await PostAsync(scope, Interest(scope.TrustBankId, 500m, Date), ct);
        await PostAsync(scope, Interest(scope.OperatingBankId, 500m, Date), ct);
        var (from, to) = end == TransferEnd.Destination
            ? (scope.TrustBankId, scope.OperatingBankId)
            : (scope.OperatingBankId, scope.TrustBankId);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<TransferBankNotTrustException>(
            () => PostAsync(scope, Transfer(from, to, 100m, Date), ct));

        (error.Code, error.End, error.BankAccountId)
            .ShouldBe(("transfer_bank_not_trust", end, scope.OperatingBankId));
        error.Message.ShouldContain(end == TransferEnd.Destination ? "goes to" : "comes from");
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
    }

    // ---- a PM contribution (#720) ----

    [Fact]
    public async Task A_refused_bank_fee_posts_once_a_pm_contribution_dated_on_or_before_it_covers_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Interest(scope.TrustBankId, 10m, Date), ct);
        await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Fee(scope.TrustBankId, 35m, Date), ct));

        await PostAsync(scope, Contribution(scope.TrustBankId, 25m, Date), ct);
        await PostAsync(scope, Fee(scope.TrustBankId, 35m, Date), ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_pm_contribution_dated_after_a_bank_fee_does_not_cover_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Contribution(scope.TrustBankId, 25m, Date.AddDays(1)), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => PostAsync(scope, Fee(scope.TrustBankId, 25m, Date), ct));

        error.Held.ShouldBe(0m);
        error.Message.ShouldContain("A PM contribution to this account dated on or before the fee covers it.");
    }

    [Fact]
    public async Task A_pm_contribution_into_the_pm_operating_bank_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<ContributionBankNotTrustException>(
            () => PostAsync(scope, Contribution(scope.OperatingBankId, 25m, Date), ct));

        (error.Code, error.BankAccountId).ShouldBe(("contribution_bank_not_trust", scope.OperatingBankId));
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
    }

    private static PMContribution Contribution(Guid bank, decimal amount, DateOnly date) =>
        new(new Money(amount), date, bank, "PM contribution");

    private static TrustTransfer Transfer(Guid from, Guid to, decimal amount, DateOnly date) =>
        new(new Money(amount), date, from, to, "Transfer");

    private static BankFeeCharged Fee(Guid bank, decimal amount, DateOnly date) =>
        new(new Money(amount), date, bank, "Bank fee");

    private static InterestEarned Interest(Guid bank, decimal amount, DateOnly date) =>
        new(new Money(amount), date, bank, "held fees");

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<decimal> HeldFeesAsync(OrgScope scope, Guid bank, CancellationToken ct)
    {
        decimal held = 0;
        await scope.RunAsync(async () => held = await new BalanceReader(scope.Db).HeldFeesAsync(bank, ct), ct);
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
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty(), ct);
}
