using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Migration;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting.Migration;

/// <summary>
/// The two reads the host's onboarding runs over an import, through the real dispatcher: the totals
/// verification compares against what the operator attested, and the one cutover date the opening
/// positions share.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MigrationQueryTests(PostgresFixture fixture)
{
    private static readonly DateOnly Cutover = new(2026, 6, 30);
    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    [Fact]
    public async Task Verification_reads_each_total_and_the_clearing_residual_left_in_each_basis()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        var tenant = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner], tenants: [tenant]);
        var balanceForward = (IBalanceForward)Events(scope);

        // Each opening position posts its real leg against migration clearing, so a set that does not
        // tie leaves a residual: 1000 + 300 + 75 debited to clearing, 600 credited in both bases, and
        // the receivable's 50 credited in accrual alone.
        await scope.RunAsync(async () =>
        {
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, null, new Money(1000m), EntryBasis.Both, Cutover,
                "opening:equity", OwnerId: owner, BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TrustBank(scope.TrustBankId), new Money(600m), null, EntryBasis.Both, Cutover,
                "opening:bank", BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.SecurityDepositsHeld, null, new Money(300m), EntryBasis.Both, Cutover,
                "opening:deposit", TenantId: tenant, BankAccountId: scope.DepositBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.PmIncome, null, new Money(75m), EntryBasis.Both, Cutover,
                "opening:held-fees", BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TenantReceivable, new Money(50m), null, EntryBasis.Accrual, Cutover,
                "opening:receivable", TenantId: tenant), ct);
        }, ct);

        var data = await QueryAsync(scope, new GetMigrationVerificationData(), ct);

        data.ClearingCash.ShouldBe(775m);
        data.ClearingAccrual.ShouldBe(725m);
        data.OwnerEquityCashTotal.ShouldBe(1000m);
        data.DepositLiabilityTotal.ShouldBe(300m);
        data.HeldPmFeesTotal.ShouldBe(75m);
        data.BankBookBalances.ShouldBe(
            [
                new BankBookBalance(scope.TrustBankId, AccountCodes.TrustBank(scope.TrustBankId), 600m),
                new BankBookBalance(scope.DepositBankId, AccountCodes.TrustBank(scope.DepositBankId), 0m),
                new BankBookBalance(scope.OperatingBankId, AccountCodes.PmOperatingBank(scope.OperatingBankId), 0m),
            ],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Verification_of_an_organization_with_no_import_reads_zero_throughout()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var data = await QueryAsync(scope, new GetMigrationVerificationData(), ct);

        (data.ClearingCash, data.ClearingAccrual, data.OwnerEquityCashTotal, data.DepositLiabilityTotal, data.HeldPmFeesTotal)
            .ShouldBe((0m, 0m, 0m, 0m, 0m));
        data.BankBookBalances.Select(b => b.Book).ShouldBe([0m, 0m, 0m]);
    }

    [Fact]
    public async Task The_cutover_date_is_absent_until_an_opening_position_is_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner]);

        // Ordinary activity is not an opening position, so it sets no cutover.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new OwnerContribution(owner, null, new Money(500m), new DateOnly(2026, 7, 2), scope.TrustBankId, "contribution"),
            ct), ct);

        (await QueryAsync(scope, new GetOpeningCutoverDate(), ct)).CutoverDate.ShouldBeNull();
    }

    [Fact]
    public async Task The_cutover_date_is_the_one_date_the_opening_positions_share()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner]);
        var balanceForward = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, null, new Money(400m), EntryBasis.Both, Cutover,
                "opening:equity", OwnerId: owner, BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TrustBank(scope.TrustBankId), new Money(400m), null, EntryBasis.Both, Cutover,
                "opening:bank", BankAccountId: scope.TrustBankId), ct);

            // Activity after the cutover, on two other dates, does not move it.
            await Events(scope).PostAsync(new OwnerContribution(
                owner, null, new Money(100m), new DateOnly(2026, 7, 1), scope.TrustBankId, "after"), ct);
            await Events(scope).PostAsync(new OwnerContribution(
                owner, null, new Money(100m), new DateOnly(2026, 7, 15), scope.TrustBankId, "later"), ct);
        }, ct);

        (await QueryAsync(scope, new GetOpeningCutoverDate(), ct)).CutoverDate.ShouldBe(Cutover);
    }

    [Fact]
    public async Task Opening_positions_on_two_dates_are_refused_rather_than_one_chosen()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = UuidV7.NewId();
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [owner]);
        var balanceForward = (IBalanceForward)Events(scope);

        await scope.RunAsync(async () =>
        {
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.OwnerEquity, null, new Money(400m), EntryBasis.Both, Cutover,
                "opening:equity", OwnerId: owner, BankAccountId: scope.TrustBankId), ct);
            await balanceForward.PostOpeningPositionAsync(new OpeningPositionRequest(
                AccountCodes.TrustBank(scope.TrustBankId), new Money(400m), null, EntryBasis.Both, Cutover.AddDays(1),
                "opening:bank", BankAccountId: scope.TrustBankId), ct);
        }, ct);

        var refusal = await Should.ThrowAsync<InconsistentOpeningCutoverDatesException>(
            () => QueryAsync(scope, new GetOpeningCutoverDate(), ct));
        refusal.Message.ShouldBe(
            "Opening balances use more than one cutover date. Re-provision the migration organization before continuing.");
    }

    private static async Task<T> QueryAsync<T>(OrgScope scope, IQuery<T> query, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases).Query(query, ct), ct);
        return result;
    }
}
