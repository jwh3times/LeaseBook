using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-08 golden file: the demo org's journal is replayed through the real engine by the actual
/// <see cref="DemoSeeder"/>, and the derived ledgers reproduce <c>seed/demo-org.json</c> to the cent
/// (figures asserted via the WP-06 read models). The seed figures are sacred (CLAUDE.md) — these
/// expectations change only when the dataset changes, deliberately.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class GoldenFileTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Focal_tenant_ledger_matches_the_prototype_rows()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var ledger = await QueryAsync(db => new GetTenantLedgerHandler(db).Handle(new GetTenantLedger(DemoIds.T1), ct), ct);

        ledger.Balance.ShouldBe(1450.00m);
        ledger.Rows.Count.ShouldBe(12);

        // Chronological (entry_date, posted_at, id). Running balances are engine-computed; the four
        // February prototype balances were authoring noise (§C.9 #1) — we assert the engine's values.
        var expected = new (string Date, string Category, decimal Charge, decimal Payment, decimal Balance, string Desc)[]
        {
            ("2026-02-01", "Rent", 1450m, 0m, 1450m, "Rent — February 2026"),
            ("2026-02-03", "Payment", 0m, 1450m, 0m, "ACH payment · ••4021"),
            ("2026-02-17", "Credit", 0m, 85m, -85m, "Goodwill credit — maintenance delay"),
            ("2026-02-18", "Maintenance", 85m, 0m, 0m, "Recharge — garbage disposal repair"),
            ("2026-03-01", "Rent", 1450m, 0m, 1450m, "Rent — March 2026"),
            ("2026-03-02", "Payment", 0m, 1450m, 0m, "Card payment · Visa ••6612"),
            ("2026-04-01", "Rent", 1450m, 0m, 1450m, "Rent — April 2026"),
            ("2026-04-06", "Late Fee", 25m, 0m, 1475m, "Late fee — April"),
            ("2026-04-06", "Payment", 0m, 1475m, 0m, "ACH payment · ••4021"),
            ("2026-05-01", "Rent", 1450m, 0m, 1450m, "Rent — May 2026"),
            ("2026-05-03", "Payment", 0m, 1450m, 0m, "ACH payment · ••4021"),
            ("2026-06-01", "Rent", 1450m, 0m, 1450m, "Rent — June 2026"),
        };

        for (var i = 0; i < expected.Length; i++)
        {
            var row = ledger.Rows[i];
            var e = expected[i];
            row.Date.ShouldBe(DateOnly.Parse(e.Date), $"row {i} date");
            row.Category.ShouldBe(e.Category, $"row {i} category");
            row.Charge.ShouldBe(e.Charge, $"row {i} charge");
            row.Payment.ShouldBe(e.Payment, $"row {i} payment");
            row.Balance.ShouldBe(e.Balance, $"row {i} balance");
            row.Description.ShouldBe(e.Desc, $"row {i} description");
        }
    }

    [Fact]
    public async Task Tenant_balances_match_the_dataset()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var expected = new (Guid Tenant, decimal Balance)[]
        {
            (DemoIds.T1, 1450m), (DemoIds.T2, 0m), (DemoIds.T3, 1620m), (DemoIds.T4, 0m),
            (DemoIds.T5, -75m), (DemoIds.T6, 2820m), (DemoIds.T7, 0m),
        };

        foreach (var (tenant, balance) in expected)
        {
            var ledger = await QueryAsync(db => new GetTenantLedgerHandler(db).Handle(new GetTenantLedger(tenant), ct), ct);
            ledger.Balance.ShouldBe(balance, $"tenant {tenant} balance");
        }
    }

    [Fact]
    public async Task Owner_balances_match_the_dataset()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var balances = await QueryAsync(db => new GetOwnerBalancesHandler(db).Handle(new GetOwnerBalances(), ct), ct);

        // (operating = owners[].oper, deposits = owners[].dep)
        var expected = new (Guid Owner, decimal Operating, decimal Deposits)[]
        {
            (DemoIds.O1, 14_820.50m, 8_400m), (DemoIds.O2, 41_280.75m, 26_100m),
            (DemoIds.O3, 3_210m, 2_900m), (DemoIds.O4, -420m, 4_350m),
            (DemoIds.O5, 22_640.30m, 14_200m), (DemoIds.O6, 9_870m, 11_650m),
            (DemoIds.O7, 1_840.25m, 1_450m), (DemoIds.O8, 18_305.60m, 17_800m),
        };

        foreach (var (owner, operating, deposits) in expected)
        {
            var row = balances.Rows.SingleOrDefault(r => r.OwnerId == owner);
            row.ShouldNotBeNull($"owner {owner} missing");
            row.Operating.ShouldBe(operating, $"owner {owner} operating");
            row.Deposits.ShouldBe(deposits, $"owner {owner} deposits");
        }
    }

    [Fact]
    public async Task Bank_books_and_trust_total_match_the_dataset()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var banks = await QueryAsync(db => new GetBankBalancesHandler(db).Handle(new GetBankBalances(), ct), ct);

        banks.Rows.Single(r => r.BankAccountId == DemoIds.OperBank).Book.ShouldBe(248_930.14m);
        banks.Rows.Single(r => r.BankAccountId == DemoIds.DepositBank).Book.ShouldBe(196_450.00m);
        banks.Rows.Single(r => r.BankAccountId == DemoIds.MgmtBank).Book.ShouldBe(38_240.55m);
        banks.Rows.Sum(r => r.Book).ShouldBe(483_620.69m); // kpis.trustTotal

        // P72: the seed's clearance mix reproduces the prototype register — Operating Trust has 3 uncleared
        // items (the −8,200 owner disbursement + a 1,380 + a 1,450 deposit, net −5,370), so cleared > book;
        // the deposit trust is fully cleared (uncleared 0). These figures are sacred (CLAUDE.md §C.8 / P72).
        var oper = banks.Rows.Single(r => r.BankAccountId == DemoIds.OperBank);
        oper.Cleared.ShouldBe(254_300.14m);
        oper.Uncleared.ShouldBe(-5_370.00m); // book − cleared (net of the 3 uncleared items)
        banks.Rows.Single(r => r.BankAccountId == DemoIds.DepositBank).Cleared.ShouldBe(196_450.00m);

        var register = await QueryAsync(
            db => new GetBankRegisterHandler(db).Handle(new GetBankRegister(DemoIds.OperBank, PageSize: 200), ct), ct);
        register.Totals.Cleared.ShouldBe(254_300.14m);
        register.Totals.UnclearedCount.ShouldBe(3);
    }

    [Fact]
    public async Task Deposit_register_and_trust_equation_hold()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var deposits = await QueryAsync(db => new GetDepositRegisterHandler(db).Handle(new GetDepositRegister(), ct), ct);
        deposits.Rows.Single(r => r.TenantId == DemoIds.T1 && r.Kind == "deposit").Held.ShouldBe(1_450.00m);

        var equation = await QueryAsync(db => new GetTrustEquationHandler(db).Handle(new GetTrustEquation(), ct), ct);
        equation.Rows.Select(r => r.BankAccountId).ShouldBe([DemoIds.OperBank, DemoIds.DepositBank], ignoreOrder: true);
        equation.Rows.ShouldAllBe(r => r.Variance == 0m); // variance 0.00 on both trust banks
    }

    // WP-8: the trust equation must reconcile as of an arbitrary period end (the compliance pack is
    // period-scoped). Date-bounding a balanced replay stays balanced at every AsOf.
    [Fact]
    public async Task Trust_equation_holds_and_date_bounds_at_period_ends()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        async Task<TrustEquationResponse> AsOf(DateOnly? asOf) =>
            await QueryAsync(db => new GetTrustEquationHandler(db).Handle(new GetTrustEquation(AsOf: asOf), ct), ct);

        // AsOf at/after the last entry (2026-06-03) reproduces the unbounded, as-of-now figures — the
        // "latest == all-time" tie, anchored to the sacred bank books.
        var unbounded = await AsOf(null);
        var atLatest = await AsOf(new DateOnly(2026, 6, 30));
        Oper(atLatest).Book.ShouldBe(Oper(unbounded).Book);
        Deposit(atLatest).Book.ShouldBe(Deposit(unbounded).Book);
        Oper(atLatest).Book.ShouldBe(248_930.14m);    // sacred (GetBankBalances golden)
        Deposit(atLatest).Book.ShouldBe(196_450.00m); // sacred

        // Cutover (2026-01-31): only the BalanceForward is posted, so the operating book is exactly
        // the opening position and no post-cutover activity is in view.
        var atCutover = await AsOf(new DateOnly(2026, 1, 31));
        Oper(atCutover).Book.ShouldBe(246_075.14m);    // sacred (DemoJournalSeed cutover)
        Deposit(atCutover).Book.ShouldBe(196_450.00m);

        // Pre-cutover: nothing posted at/before 2026-01-01 → no trust-bank rows at all.
        (await AsOf(new DateOnly(2026, 1, 1))).Rows.ShouldBeEmpty();

        // Date-bounding is real, not a no-op: the Mercer overpayment (2026-05-22) creates a 75.00
        // prepayment on the operating trust — present as-of 05-31, absent as-of 05-01.
        Oper(await AsOf(new DateOnly(2026, 5, 31))).Prepayments.ShouldBe(75.00m);
        Oper(await AsOf(new DateOnly(2026, 5, 1))).Prepayments.ShouldBe(0.00m);

        // The equation itself (variance 0.00 on every trust bank) holds at every period end.
        foreach (var asOf in new[]
                 {
                     new DateOnly(2026, 1, 31), new DateOnly(2026, 2, 28),
                     new DateOnly(2026, 5, 31), new DateOnly(2026, 6, 30),
                 })
        {
            (await AsOf(asOf)).Rows.ShouldAllBe(r => r.Variance == 0m);
        }
    }

    // WP-8: the deposit register must scope to one trust account and to a period end, and reconcile to
    // the trust equation's held components on that bank.
    [Fact]
    public async Task Deposit_register_filters_by_trust_account_and_period_end()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        async Task<DepositRegisterResponse> Reg(Guid? bank, DateOnly? asOf) =>
            await QueryAsync(db => new GetDepositRegisterHandler(db).Handle(new GetDepositRegister(bank, asOf), ct), ct);

        // Security-deposit trust, as of the latest period end: deposits only (prepayments live on the
        // operating bank), summing to the trust equation's deposit-liability component. Jasmine (T1)
        // holds 1,450.00.
        var depositBank = await Reg(DemoIds.DepositBank, new DateOnly(2026, 6, 30));
        depositBank.Rows.ShouldAllBe(r => r.Kind == "deposit");
        depositBank.Rows.Sum(r => r.Held).ShouldBe(196_450.00m);
        depositBank.Rows.Single(r => r.TenantId == DemoIds.T1).Held.ShouldBe(1_450.00m);
        depositBank.Rows.ShouldAllBe(r => r.Held >= 0m);

        // Operating trust, end of May: the 75.00 Mercer prepayment (2026-05-22) is the only held
        // position, reconciling to the equation's Prepayments component; no deposits live here.
        var operMayEnd = await Reg(DemoIds.OperBank, new DateOnly(2026, 5, 31));
        operMayEnd.Rows.ShouldAllBe(r => r.Kind == "prepayment");
        operMayEnd.Rows.Sum(r => r.Held).ShouldBe(75.00m);

        // As-of 05-01, before the overpayment: nothing held yet on the operating trust.
        (await Reg(DemoIds.OperBank, new DateOnly(2026, 5, 1))).Rows.ShouldBeEmpty();
    }

    private static TrustEquationRow Oper(TrustEquationResponse r) =>
        r.Rows.Single(x => x.BankAccountId == DemoIds.OperBank);

    private static TrustEquationRow Deposit(TrustEquationResponse r) =>
        r.Rows.Single(x => x.BankAccountId == DemoIds.DepositBank);

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_the_journal()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var first = await QueryAsync(db => db.Set<JournalEntry>().CountAsync(ct), ct);

        await DemoSeeder.SeedAsync(fixture.Api.Services, ct); // second run
        var second = await QueryAsync(db => db.Set<JournalEntry>().CountAsync(ct), ct);

        second.ShouldBe(first);
    }

    private Task SeedAsync(CancellationToken ct) => DemoSeeder.SeedAsync(fixture.Api.Services, ct);

    private async Task<T> QueryAsync<T>(Func<DbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsync(DemoSeeder.DemoOrgId, async () => result = await query(db), ct);
        return result;
    }
}
