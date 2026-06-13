using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-06: the derived ledgers are queries over the journal. A small posted scenario is read back
/// through each read model and checked, and a second org's data stays invisible.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReadModelTests(PostgresFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public async Task Read_models_reflect_a_posted_scenario()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await PostScenarioAsync(scope, ct);

        // Bank books.
        var banks = await Query(scope, new GetBankBalancesHandler(scope.Db), new GetBankBalances(), ct);
        banks.Rows.Single(r => r.BankAccountId == TrustBankId).Book.ShouldBe(450m);   // +1450 -200 -800
        banks.Rows.Single(r => r.BankAccountId == DepositBankId).Book.ShouldBe(1000m);
        banks.Rows.Single(r => r.BankAccountId == OperatingBankId).Book.ShouldBe(200m); // swept fees

        // Owner balances.
        var owners = await Query(scope, new GetOwnerBalancesHandler(scope.Db), new GetOwnerBalances(), ct);
        var owner = owners.Rows.Single(r => r.OwnerId == Owner);
        owner.Operating.ShouldBe(450m);   // +1450 payment -200 fee -800 disbursement (cash+both)
        owner.Deposits.ShouldBe(1000m);
        owner.Total.ShouldBe(1450m);

        // Tenant ledger (security deposit excluded — that is the register).
        var ledger = await Query(scope, new GetTenantLedgerHandler(scope.Db), new GetTenantLedger(Tenant), ct);
        ledger.Rows.Count.ShouldBe(2);
        ledger.Rows[0].Category.ShouldBe("Rent");
        ledger.Rows[0].Charge.ShouldBe(1450m);
        ledger.Rows[0].Balance.ShouldBe(1450m);
        ledger.Rows[1].Category.ShouldBe("Payment");
        ledger.Rows[1].Payment.ShouldBe(1450m);
        ledger.Rows[1].Balance.ShouldBe(0m);
        ledger.Balance.ShouldBe(0m);

        // Deposit register.
        var deposits = await Query(scope, new GetDepositRegisterHandler(scope.Db), new GetDepositRegister(), ct);
        var held = deposits.Rows.Single(r => r.TenantId == Tenant && r.Kind == "deposit");
        held.Held.ShouldBe(1000m);

        // Trust equation: variance 0.00 on both trust banks; the PM operating bank is not listed.
        var equation = await Query(scope, new GetTrustEquationHandler(scope.Db), new GetTrustEquation(), ct);
        equation.Rows.Select(r => r.BankAccountId).ShouldBe([TrustBankId, DepositBankId], ignoreOrder: true);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m);
    }

    [Fact]
    public async Task A_second_orgs_data_is_invisible()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var orgA = await ProvisionedScopeAsync(fixture, ct);
        await using var orgB = await ProvisionedScopeAsync(fixture, ct);

        await PostScenarioAsync(orgA, ct);

        var bBanks = await Query(orgB, new GetBankBalancesHandler(orgB.Db), new GetBankBalances(), ct);
        bBanks.Rows.ShouldAllBe(r => r.Book == 0m); // B provisioned the same banks but posted nothing

        var bOwners = await Query(orgB, new GetOwnerBalancesHandler(orgB.Db), new GetOwnerBalances(), ct);
        bOwners.Rows.ShouldBeEmpty(); // none of A's owner rows leak into B
    }

    private static async Task PostScenarioAsync(OrgScope scope, CancellationToken ct)
    {
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(Tenant, Property, Owner, null, new Money(1450m), Feb(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(Tenant, Property, Owner, new Money(1450m), Feb(3), PaymentMethod.Ach, TrustBankId, "pay"), ct);
            await events.PostAsync(new DepositCollected(Tenant, Property, Owner, new Money(1000m), Feb(1), DepositBankId, "deposit"), ct);
            await events.PostAsync(new ManagementFeeAssessed(Owner, Property, new Money(200m), Feb(27), TrustBankId, "fee"), ct);
            await events.PostAsync(new PMFeesSwept(new Money(200m), Feb(27), TrustBankId, OperatingBankId, "sweep"), ct);
            await events.PostAsync(new OwnerDisbursed(Owner, new Money(800m), Feb(28), TrustBankId, "draw"), ct);
        }, ct);
    }

    private static async Task<TResult> Query<TQuery, TResult>(
        OrgScope scope, LeaseBook.SharedKernel.Cqrs.IQueryHandler<TQuery, TResult> handler, TQuery query, CancellationToken ct)
        where TQuery : LeaseBook.SharedKernel.Cqrs.IQuery<TResult>
    {
        TResult result = default!;
        await scope.RunAsync(async () => result = await handler.Handle(query, ct), ct);
        return result;
    }

    private static DateOnly Feb(int day) => new(2026, 2, day);
}
