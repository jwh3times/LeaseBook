using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.LedgerPosting;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// A staff void is held to the same floor as any other charge against the PM's held fees (#720). A void
/// whose mirrored lines lower the held fees in a bank is refused with <c>pm_fees_insufficient</c> when
/// they would go below zero on its date or any later one: past zero the bank would be paying the PM's
/// charges from owners' and tenants' money. A void that leaves the held fees alone or raises them is
/// unaffected. Sent through the real dispatcher.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class VoidHeldFeesFloorTests(PostgresFixture fixture)
{
    private static readonly DateOnly Date = new(2026, 2, 6);

    [Fact]
    public async Task Voiding_a_pm_contribution_that_a_bank_fee_drew_on_is_refused_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var contribution = await PostAsync(scope, Contribution(scope.TrustBankId, 25m), ct);
        await PostAsync(scope, new BankFeeCharged(new Money(25m), Date, scope.TrustBankId, "Bank fee"), ct);
        var before = await EntryCountAsync(scope, ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(
            () => VoidAsync(scope, contribution, ct));

        (error.Code, error.Charge, error.Shortfall, error.Held, error.BankAccountId)
            .ShouldBe(("pm_fees_insufficient", HeldFeesCharge.Void, 25m, 0m, scope.TrustBankId));
        error.Message.ShouldStartWith(
            "Voiding this entry would take the management fees held in Operating Trust 25.00 below zero");
        (await EntryCountAsync(scope, ct)).ShouldBe(before);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task A_pm_contribution_nothing_drew_on_can_be_voided()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var contribution = await PostAsync(scope, Contribution(scope.TrustBankId, 25m), ct);

        await VoidAsync(scope, contribution, ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task Once_the_bank_fee_is_voided_the_pm_contribution_it_drew_on_can_be_voided_too()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var contribution = await PostAsync(scope, Contribution(scope.TrustBankId, 25m), ct);
        var fee = await PostAsync(scope, new BankFeeCharged(new Money(25m), Date, scope.TrustBankId, "Bank fee"), ct);

        // Voiding a bank fee gives the held fees back, so the floor never stands in its way.
        await VoidAsync(scope, fee, ct);
        await VoidAsync(scope, contribution, ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task Voiding_a_management_fee_already_swept_out_of_the_trust_bank_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner]);
        await PostAsync(scope, new OwnerContribution(
            owner, null, new Money(1_000m), Date, scope.TrustBankId, "Owner funds"), ct);
        var fee = await PostAsync(scope, new ManagementFeeAssessed(
            owner, null, new Money(80m), Date, scope.TrustBankId, "Management fee"), ct);
        await PostAsync(scope, new PMFeesSwept(
            new Money(80m), Date, scope.TrustBankId, scope.OperatingBankId, "Fee sweep"), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(() => VoidAsync(scope, fee, ct));

        (error.Shortfall, error.Held, error.BankAccountId).ShouldBe((80m, 0m, scope.TrustBankId));
        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
    }

    [Fact]
    public async Task Voiding_a_management_fee_still_held_in_trust_is_unaffected()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner]);
        await PostAsync(scope, new OwnerContribution(
            owner, null, new Money(1_000m), Date, scope.TrustBankId, "Owner funds"), ct);
        var fee = await PostAsync(scope, new ManagementFeeAssessed(
            owner, null, new Money(80m), Date, scope.TrustBankId, "Management fee"), ct);

        await VoidAsync(scope, fee, ct);

        (await HeldFeesAsync(scope, scope.TrustBankId, ct)).ShouldBe(0m);
        await AssertCleanAsync(scope, ct);
    }

    [Fact]
    public async Task Voiding_a_sweep_is_refused_when_the_pm_operating_bank_no_longer_holds_what_it_received()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostAsync(scope, Contribution(scope.TrustBankId, 80m), ct);
        var sweep = await PostAsync(scope, new PMFeesSwept(
            new Money(80m), Date, scope.TrustBankId, scope.OperatingBankId, "Fee sweep"), ct);
        // The PM operating bank's charge spends what the sweep brought in.
        await PostAsync(scope, new BankFeeCharged(new Money(30m), Date, scope.OperatingBankId, "Bank fee"), ct);

        var error = await Should.ThrowAsync<PmFeesInsufficientException>(() => VoidAsync(scope, sweep, ct));

        (error.Shortfall, error.Held, error.BankAccountId, error.Charge)
            .ShouldBe((80m, 50m, scope.OperatingBankId, HeldFeesCharge.VoidInPmOperatingBank));
        // A PM contribution goes only into a trust account, so it is not offered as the remedy here.
        error.Message.ShouldStartWith("Voiding this entry would take the management fees held in Management "
            + "Operating 30.00 below zero on or after ");
        error.Message.ShouldEndWith(", so nothing was posted. Void the later charge that uses them first.");
    }

    [Fact]
    public async Task A_backdated_void_into_a_reconciled_month_is_refused_as_locked_not_as_short()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var contribution = await PostAsync(scope, Contribution(scope.TrustBankId, 25m), ct);
        await PostAsync(scope, new BankFeeCharged(new Money(25m), Date, scope.TrustBankId, "Bank fee"), ct);
        // February reconciled with both lines outstanding: they net to a book balance of zero.
        await scope.RunAsync(async () =>
        {
            var reconciliation = await new StartReconciliationHandler(scope.Db, Calendar(new DateOnly(2026, 2, 28)), Lock(scope))
                .Handle(new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct);
            await new FinalizeReconciliationHandler(scope.Db, scope.Actor, scope.Tenant, Calendar(new DateOnly(2026, 2, 28)), Lock(scope))
                .Handle(new FinalizeReconciliation(reconciliation.Id), ct);
        }, ct);

        // No contribution can be dated into a locked month, so a shortfall there is not the answer to give.
        await Should.ThrowAsync<AccountPeriodLockedException>(() => scope.RunAsync(() =>
            AccountingSender.Create(scope, NoLeases).Send(
                new VoidEntry(contribution, "posted in error", Date.AddDays(10), "void:locked"), ct), ct));
    }

    private static PMContribution Contribution(Guid bank, decimal amount) =>
        new(new Money(amount), Date, bank, "PM contribution");

    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task VoidAsync(OrgScope scope, Guid entryId, CancellationToken ct) =>
        await scope.RunAsync(() => AccountingSender.Create(scope, NoLeases)
            .Send(new VoidEntry(entryId, "posted in error", null, $"void:{entryId}"), ct), ct);

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

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
            (await new LeaseBook.Modules.Accounting.Diagnostics.InvariantChecks(scope.Db).CheckCoreAsync(ct))
                .ShouldBeEmpty(), ct);
}
