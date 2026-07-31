using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-13 golden lock for the scenario org (lock-after-observation, derivation cross-checked in the
/// design note). The scenario org's figures are hand-authored tripwires: any posting-template,
/// rounding, or fee-basis change moves them, and this suite is what makes that movement loud.
/// Entity ids are fresh UUIDv7 per seed, so rows are resolved by directory name, never by id.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ScenarioGoldenTests(PostgresFixture fixture)
{
    private static readonly DateOnly GoldenAsOf = new(2026, 6, 30);

    [Fact]
    public async Task Tenant_receivable_balances_match_the_hand_authored_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tenants = await TenantIdsAsync(ct);

        var balances = await QueryAsync(
            db => new GetTenantBalancesHandler(db).Handle(new GetTenantBalances(), ct), ct);
        decimal BalanceOf(string name) =>
            balances.Rows.SingleOrDefault(r => r.TenantId == tenants[name])?.Balance ?? 0m;

        // Balance is the tenant's NET position: receivable minus held prepayments (the demo's
        // Mercer −75 has the same shape) — T-S1's 200.00 surviving prepayment reads as credit.
        BalanceOf("Avery Whitfield").ShouldBe(-200.00m);
        // 4 × 1,000 rent + late fees (50 at-cap ×3 runs) + 25 NSF − 600 − 1,000 − 1,415 − 85 credit;
        // the June duplicate late fee and its void cancel exactly.
        BalanceOf("Rosa Delgado").ShouldBe(1_075.00m);
        BalanceOf("Kenji Nakamura").ShouldBe(0m);
        BalanceOf("Priya Raman").ShouldBe(0m); // move-out fully settled by the deposit disposition
        BalanceOf("Marcus Vale").ShouldBe(0m);
        // 450 opening + 4 × 200 rent + 4 × 15.00 floor-clamped late fees, never a payment.
        BalanceOf("Dean Ashford").ShouldBe(1_310.00m);
        BalanceOf("Lena Osei").ShouldBe(0m);
        BalanceOf("Silas Byrd").ShouldBe(0m);
    }

    [Fact]
    public async Task Owner_balances_land_on_the_designed_end_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var owners = await OwnerIdsAsync(ct);

        var balances = await QueryAsync(
            db => new GetOwnerBalancesHandler(db).Handle(new GetOwnerBalances(), ct), ct);
        (decimal Operating, decimal Deposits) Of(string name)
        {
            var row = balances.Rows.SingleOrDefault(r => r.OwnerId == owners[name]);
            return (row?.Operating ?? 0m, row?.Deposits ?? 0m);
        }

        // Every disbursing owner is swept to exactly its reserve each run.
        //
        // KNOWN OVERSTATEMENT (WP-13 finding, design note §12.11): the deposits column reads
        // 5,220.00, not the true 3,820.00 — DepositApplied/RefundIssued debit
        // security_deposits_held with a tenant dim but NO owner dim (AccountingEventService.cs:213,
        // :358), while collected/imported credits carry owner. Per-tenant reads clear correctly;
        // the owner-dimensioned column never decreases after a disposition, so T-S4's fully-
        // disposed 1,400.00 still shows under O-S1. The journal is append-only, so the template
        // fix cannot repair history — this literal locks today's OBSERVED behavior and must move
        // in the same commit as that fix.
        Of("Harborview Holdings").ShouldBe((500.00m, 5_220.00m));
        Of("Beacon Ridge LLC").ShouldBe((0.00m, 1_375.00m)); // zero-bps owner, fully disbursed
        Of("Stillwater Properties").ShouldBe((1_052.63m, 0m)); // parked at the floor — never moved
        Of("Meridian Estates").ShouldBe((-250.00m, 0m)); // negative-equity owner — never disbursed
        Of("Cypress Grove Partners").ShouldBe((250.00m, 0m));
    }

    [Fact]
    public async Task Trust_equation_holds_per_bank_with_the_pinned_terms()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var equation = await QueryAsync(
            db => new GetTrustEquationHandler(db).Handle(new GetTrustEquation(), ct), ct);

        var operating = equation.Rows.Single(r => r.BankAccountId == ScenarioSeeder.OperatingTrustId);
        operating.Book.ShouldBe(2_121.37m);
        operating.OwnerEquity.ShouldBe(1_552.63m); // 500 + 0 + 1,052.63 − 250 + 250
        operating.DepositLiabilities.ShouldBe(0m);
        operating.Prepayments.ShouldBe(200.00m); // T-S1's surviving residual
        operating.HeldPmFees.ShouldBe(368.74m); // June fees − bank fees + interest attribution
        operating.Variance.ShouldBe(0m);

        var deposit = equation.Rows.Single(r => r.BankAccountId == ScenarioSeeder.DepositTrustId);
        deposit.Book.ShouldBe(5_195.00m);
        deposit.DepositLiabilities.ShouldBe(5_195.00m); // 3,820 (O-S1) + 1,375 (O-S2)
        deposit.HeldPmFees.ShouldBe(0m); // interest attribution swept back by the trust transfer
        deposit.Variance.ShouldBe(0m);
    }

    [Fact]
    public async Task Bank_books_and_uncleared_counts_reflect_the_open_June()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        var banks = await QueryAsync(
            db => new GetBankBalancesHandler(db).Handle(new GetBankBalances(), ct), ct);

        var operating = banks.Rows.Single(r => r.BankAccountId == ScenarioSeeder.OperatingTrustId);
        operating.Book.ShouldBe(2_121.37m);
        // June is deliberately unreconciled; only the statement-import confirmations cleared lines
        // (T-S1's 1,150 matched, T-S3's 1,620 suggested, the 8.50 created adjustment).
        operating.UnclearedCount.ShouldBe(9);

        var deposit = banks.Rows.Single(r => r.BankAccountId == ScenarioSeeder.DepositTrustId);
        deposit.Book.ShouldBe(5_195.00m);
        deposit.UnclearedCount.ShouldBe(2); // T-S7's deposit + the outbound transfer leg

        // The PM's own bank holds exactly the two sweep literals.
        banks.Rows.Single(r => r.BankAccountId == ScenarioSeeder.PmOperatingId)
            .Book.ShouldBe(2_552.97m); // 1,800.00 (April partial) + 752.97 (May full)
    }

    [Fact]
    public async Task Delinquency_buckets_fill_all_four_aged_bands_at_the_golden_as_of()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tenants = await TenantIdsAsync(ct);

        var aging = await QueryAsync(
            db => new GetDelinquencyAgingHandler(db).Handle(new GetDelinquencyAging(GoldenAsOf), ct), ct);

        aging.Rows.Count.ShouldBe(2); // only positive-total tenants surface

        // T-S6: opening receivable (age 122) + March rent + fee (age 121) all land over-90.
        var tS6 = aging.Rows.Single(r => r.TenantId == tenants["Dean Ashford"]);
        (tS6.Current, tS6.D1_30, tS6.D31_60, tS6.D61_90, tS6.Over90)
            .ShouldBe((0m, 215.00m, 215.00m, 215.00m, 665.00m));
        tS6.Total.ShouldBe(1_310.00m);

        // T-S2's partial payments bucket by their own entry_date (not FIFO-applied), so individual
        // buckets legitimately go negative while Total stays right — lock the OBSERVED shape; do
        // not "fix" the negatives (GetDelinquencyAging.cs:55-57 documents the semantics).
        var tS2 = aging.Rows.Single(r => r.TenantId == tenants["Rosa Delgado"]);
        (tS2.Current, tS2.D1_30, tS2.D31_60, tS2.D61_90, tS2.Over90)
            .ShouldBe((0m, 1_075.00m, -415.00m, -35.00m, 450.00m));
        tS2.Total.ShouldBe(1_075.00m);
    }

    [Fact]
    public async Task Management_fee_income_pins_each_month_including_adjustment_effects()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);

        async Task<decimal> MonthAsync(int month)
        {
            var result = await QueryAsync(
                db => new GetManagementFeeIncomeHandler(db).Handle(new GetManagementFeeIncome(2026, month), ct), ct);
            var row = result.Rows.ShouldHaveSingleItem(); // engine posts property-null pm_income only
            row.PropertyId.ShouldBeNull();
            return row.Amount;
        }

        // The report is net pm_income for the month, so bank fees/interest move it while sweeps and
        // trust transfers (paired debit+credit in-month) do not. The OpeningBalance 350.00 held-fees
        // position is excluded by the WP-7 D10 rule and appears in no month.
        (await MonthAsync(3)).ShouldBe(1_146.60m); // 819.00 (O-S1 @700bps) + 327.60 (O-S5 @800bps)
        (await MonthAsync(4)).ShouldBe(346.20m); // 361.20 (O-S1 only; O-S5 parked) − 15.00 back-dated fee
        (await MonthAsync(5)).ShouldBe(722.51m); // 445.37 + 264.80 + 12.34 deposit-trust interest
        (await MonthAsync(6)).ShouldBe(356.40m); // 249.90 + 130.00 − 15.00 fee − 8.50 created fee
    }

    [Fact]
    public async Task Deposit_register_carries_both_kinds_at_the_golden_as_of()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tenants = await TenantIdsAsync(ct);

        var register = await QueryAsync(
            db => new GetDepositRegisterHandler(db).Handle(new GetDepositRegister(), ct), ct);

        decimal Held(string name, string kind) =>
            register.Rows.SingleOrDefault(r => r.TenantId == tenants[name] && r.Kind == kind)?.Held ?? 0m;

        Held("Avery Whitfield", "deposit").ShouldBe(1_200.00m);
        Held("Rosa Delgado", "deposit").ShouldBe(1_000.00m);
        Held("Kenji Nakamura", "deposit").ShouldBe(1_620.00m);
        Held("Lena Osei", "deposit").ShouldBe(1_375.00m); // collected in June, awaiting application
        Held("Priya Raman", "deposit").ShouldBe(0m); // fully disposed at move-out (apply + refund)
        // The surviving prepayment residual — the register's second kind must stay non-empty.
        Held("Avery Whitfield", "prepayment").ShouldBe(200.00m);
        Held("Marcus Vale", "prepayment").ShouldBe(0m); // applied then refunded to exactly zero
    }

    [Fact]
    public async Task Void_pair_is_visible_in_the_tenant_ledger()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var tenants = await TenantIdsAsync(ct);

        var ledger = await QueryAsync(
            db => new GetTenantLedgerHandler(db).Handle(new GetTenantLedger(tenants["Rosa Delgado"]), ct), ct);

        var voided = ledger.Rows.Where(r => r.IsVoided).ToList();
        var reversal = ledger.Rows.Where(r => r.ReversesEntryId is not null).ToList();
        voided.ShouldHaveSingleItem("exactly one voided entry (the duplicate June late fee)");
        reversal.ShouldHaveSingleItem("exactly one linked reversal");
        reversal[0].ReversesEntryId.ShouldBe(voided[0].EntryId);
    }

    /// <summary>
    /// R7: the consolidated = Σ per-property identity is scoped to property-dimensioned rows only —
    /// the opening position, run-posted management fees, and disbursements carry no PropertyId, so
    /// they appear in the consolidated statement and in neither per-property view. The delta is
    /// pinned rather than pretending the naive identity holds.
    /// </summary>
    [Fact]
    public async Task Consolidated_statement_pins_the_property_null_delta()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync(ct);
        var owners = await OwnerIdsAsync(ct);
        var oS1 = owners["Harborview Holdings"];
        var (pS1, pS2) = await QueryAsync(async db =>
        {
            var byAddress = await db.Set<Property>()
                .Where(p => p.Address == "14 Harborview Ln" || p.Address == "72 Beacon Hill Ct")
                .ToDictionaryAsync(p => p.Address, p => p.Id, ct);
            return (byAddress["14 Harborview Ln"], byAddress["72 Beacon Hill Ct"]);
        }, ct);

        var consolidated = (await QueryAsync(db => new GetOwnerStatementDataHandler(db).Handle(
            new GetOwnerStatementData([oS1], null, 2026, 5, "cash"), ct), ct)).ByOwner[oS1];
        var perA = (await QueryAsync(db => new GetOwnerStatementDataHandler(db).Handle(
            new GetOwnerStatementData([oS1], pS1, 2026, 5, "cash"), ct), ct)).ByOwner[oS1];
        var perB = (await QueryAsync(db => new GetOwnerStatementDataHandler(db).Handle(
            new GetOwnerStatementData([oS1], pS2, 2026, 5, "cash"), ct), ct)).ByOwner[oS1];

        consolidated.Ending.ShouldBe(500.00m); // swept to exactly the reserve
        // Property-null rows through May 31: opening 8,250 (CR) − fees (819 + 361.20 + 445.37)
        // − disbursements (10,381 + 4,298.80 + 5,417.05) = −13,472.42.
        (perA.Ending + perB.Ending - consolidated.Ending).ShouldBe(13_472.42m);
    }

    // ── Harness ──────────────────────────────────────────────────────────────

    private Task SeedAsync(CancellationToken ct) => ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

    private Task<IReadOnlyDictionary<string, Guid>> OwnerIdsAsync(CancellationToken ct) =>
        QueryAsync(async db => (IReadOnlyDictionary<string, Guid>)await db.Set<Owner>()
            .Where(o => !o.IsSystem).ToDictionaryAsync(o => o.Name, o => o.Id, ct), ct);

    private Task<IReadOnlyDictionary<string, Guid>> TenantIdsAsync(CancellationToken ct) =>
        QueryAsync(async db => (IReadOnlyDictionary<string, Guid>)await db.Set<Tenant>()
            .Where(t => !t.IsSystem).ToDictionaryAsync(t => t.DisplayName, t => t.Id, ct), ct);

    private async Task<T> QueryAsync<T>(Func<DbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsync(ScenarioSeeder.ScenarioOrgId, async () => result = await query(db), ct);
        return result;
    }
}
