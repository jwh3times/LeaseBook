using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Persistence;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-02 (M4): the bank register is a projection of journal lines on a bank account, with clearance
/// status (LEFT JOIN bank_line_status, absence ≡ uncleared) and book/cleared/uncleared totals. A second
/// org's register stays empty (RLS).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RegisterReadModelTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();

    [Fact]
    public async Task Register_projects_bank_lines_with_clearance_and_totals()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        await PostScenarioAsync(scope, ct);

        // Clearance: the 1450 deposit cleared, the 8200 withdrawal reconciled, the 10000 left uncleared.
        await SetStatusAsync(scope, l => l.Debit?.Amount == 1450m, BankLineStatus.Cleared, ct);
        await SetStatusAsync(scope, l => l.Credit?.Amount == 8200m, BankLineStatus.Reconciled, ct);

        var register = await Query(scope, new GetBankRegisterHandler(scope.Db), new GetBankRegister(scope.TrustBankId), ct);

        register.Rows.Count.ShouldBe(3); // contribution + payment + disbursement (rent has no bank line)
        register.Total.ShouldBe(3);
        register.Totals.Book.ShouldBe(3250m);       // 10000 + 1450 - 8200
        register.Totals.Cleared.ShouldBe(-6750m);   // 1450 (cleared) - 8200 (reconciled)
        register.Totals.Uncleared.ShouldBe(10000m); // the 10000 contribution, still uncleared
        register.Totals.UnclearedCount.ShouldBe(1);
        register.Totals.DepositsInView.ShouldBe(11450m);
        register.Totals.WithdrawalsInView.ShouldBe(8200m);

        var deposit1450 = register.Rows.Single(r => r.Deposit == 1450m);
        deposit1450.Status.ShouldBe(BankLineStatus.Cleared);
        deposit1450.Withdrawal.ShouldBeNull();
        register.Rows.Single(r => r.Withdrawal == 8200m).Status.ShouldBe(BankLineStatus.Reconciled);
        register.Rows.Single(r => r.Deposit == 10000m).Status.ShouldBe(BankLineStatus.Uncleared);

        // The bank-balances read agrees with the register totals.
        var balances = await Query(scope, new GetBankBalancesHandler(scope.Db), new GetBankBalances(), ct);
        var trust = balances.Rows.Single(r => r.BankAccountId == scope.TrustBankId);
        trust.Book.ShouldBe(3250m);
        trust.Cleared.ShouldBe(-6750m);
        trust.Uncleared.ShouldBe(10000m);
    }

    [Fact]
    public async Task Register_type_filter_and_pagination_narrow_the_view()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);

        await PostScenarioAsync(scope, ct);

        var deposits = await Query(scope, new GetBankRegisterHandler(scope.Db),
            new GetBankRegister(scope.TrustBankId, Type: RegisterTypeFilter.Deposits), ct);
        deposits.Rows.Count.ShouldBe(2); // 10000 + 1450
        deposits.Rows.ShouldAllBe(r => r.Withdrawal == null);

        var withdrawals = await Query(scope, new GetBankRegisterHandler(scope.Db),
            new GetBankRegister(scope.TrustBankId, Type: RegisterTypeFilter.Withdrawals), ct);
        withdrawals.Rows.Count.ShouldBe(1); // 8200
        withdrawals.Rows[0].Withdrawal.ShouldBe(8200m);

        var firstPage = await Query(scope, new GetBankRegisterHandler(scope.Db),
            new GetBankRegister(scope.TrustBankId, Page: 1, PageSize: 2), ct);
        firstPage.Rows.Count.ShouldBe(2);
        firstPage.Total.ShouldBe(3); // window count spans the whole filtered set, not the page
    }

    [Fact]
    public async Task A_second_orgs_register_is_empty()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var orgA = await ProvisionedScopeAsync(
            fixture, ct, owners: [_owner], tenants: [_tenant], properties: [_property]);
        await using var orgB = await ProvisionedScopeAsync(fixture, ct);

        await orgA.RunAsync(() => Events(orgA).PostAsync(
            new OwnerContribution(_owner, _property, new Money(500m), D(1), orgA.TrustBankId, "seed"), ct), ct);

        var bRegister = await Query(orgB, new GetBankRegisterHandler(orgB.Db), new GetBankRegister(orgB.TrustBankId), ct);
        bRegister.Rows.ShouldBeEmpty();
        bRegister.Total.ShouldBe(0);
        bRegister.Totals.Book.ShouldBe(0m);
        bRegister.Totals.UnclearedCount.ShouldBe(0);
    }

    private async Task PostScenarioAsync(OrgScope scope, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new OwnerContribution(_owner, _property, new Money(10000m), D(1), scope.TrustBankId, "seed"), ct);
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(1450m), D(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(_tenant, _property, _owner, new Money(1450m), D(3), PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
            await events.PostAsync(new OwnerDisbursed(_owner, new Money(8200m), D(5), scope.TrustBankId, "draw"), ct);
        }, ct);

    // Status is written via a raw upsert (the real path — bank_line_status is never EF-tracked, P62),
    // as the app role inside the scope's RLS transaction.
    private static async Task SetStatusAsync(
        OrgScope scope, Func<JournalLine, bool> predicate, BankLineStatus status, CancellationToken ct) =>
        await scope.RunAsync(async () =>
        {
            var lines = await scope.Db.Set<JournalLine>()
                .Where(l => l.BankAccountId == scope.TrustBankId
                    && l.AccountClass == AccountClass.TrustBank
                    && (l.Basis == EntryBasis.Cash || l.Basis == EntryBasis.Both))
                .ToListAsync(ct);
            var match = lines.Single(predicate);
            var statusText = BankLineStatusConverter.ToDb(status);
            await scope.Db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO bank_line_status (journal_line_id, org_id, status, cleared_at, created_at, updated_at)
                VALUES ({match.Id}, {scope.OrgId}, {statusText}, now(), now(), now())
                """, ct);
        }, ct);

    private static async Task<TResult> Query<TQuery, TResult>(
        OrgScope scope, IQueryHandler<TQuery, TResult> handler, TQuery query, CancellationToken ct)
        where TQuery : IQuery<TResult>
    {
        TResult result = default!;
        await scope.RunAsync(async () => result = await handler.Handle(query, ct), ct);
        return result;
    }

    private static DateOnly D(int day) => new(2026, 2, day);
}
