using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LeaseBook.Web.Seeding;

/// <summary>
/// Replays the Tarheel demo dataset (§C.8) through the real accounting engine: provision the chart of
/// accounts, post the cutover BalanceForward (2026-01-31), then post every event Feb–Jun in
/// chronological order. The derived read-model figures reconcile to the cent against
/// <c>seed/demo-org.json</c> (the golden tests are the proof). Idempotent — skips if any journal entry
/// already exists for the org. Runs inside the seeder's ambient org transaction.
/// </summary>
internal static class DemoJournalSeed
{
    public static async Task SeedAsync(IServiceProvider sp, DbContext db, CancellationToken ct)
    {
        if (await db.Set<JournalEntry>().AnyAsync(ct))
        {
            return; // already seeded
        }

        var chartOfAccounts = sp.GetRequiredService<IChartOfAccounts>();
        var balanceForward = sp.GetRequiredService<IBalanceForward>();
        var events = sp.GetRequiredService<IAccountingEvents>();

        await chartOfAccounts.ProvisionAsync(
            [
                new BankAccountSpec(DemoIds.OperBank, "Operating Trust", BankPurpose.Trust),
                new BankAccountSpec(DemoIds.DepositBank, "Security Deposit Trust", BankPurpose.Deposit),
                new BankAccountSpec(DemoIds.MgmtBank, "PM Operating", BankPurpose.Operating),
            ], ct);

        await PostBalanceForwardAsync(balanceForward, ct);
        await PostReplayedEventsAsync(events, ct);
    }

    // The cutover positions. Each owner-equity / deposit residual = JSON target − Σ(effect of the
    // replayed events that touch it); the entry ties per bank by construction (DR bank book = CR
    // positions). If any literal is changed, the golden tests must change with it (CLAUDE.md).
    private static Task PostBalanceForwardAsync(IBalanceForward balanceForward, CancellationToken ct)
    {
        BalanceForwardLine OwnerEquityCr(Guid owner, decimal amount) =>
            new(AccountCodes.OwnerEquity, null, M(amount), OwnerId: owner, BankAccountId: DemoIds.OperBank);
        BalanceForwardLine OwnerEquityDr(Guid owner, decimal amount) =>
            new(AccountCodes.OwnerEquity, M(amount), null, OwnerId: owner, BankAccountId: DemoIds.OperBank);
        BalanceForwardLine DepositCr(Guid tenant, Guid owner, decimal amount) =>
            new(AccountCodes.SecurityDepositsHeld, null, M(amount), OwnerId: owner, TenantId: tenant, BankAccountId: DemoIds.DepositBank);

        var lines = new List<BalanceForwardLine>
        {
            // Operating trust: book 246,075.14 = Σ owner equity (o4 is negative → a debit).
            new(AccountCodes.TrustBank(DemoIds.OperBank), M(246_075.14m), null, BankAccountId: DemoIds.OperBank),
            OwnerEquityCr(DemoIds.O1, 13_665.50m),    // 14,820.50 target − 1,155.00 replayed
            OwnerEquityCr(DemoIds.O2, 38_110.75m),    // 41,280.75 − 3,170.00
            OwnerEquityCr(DemoIds.O3, 3_210.00m),     // no replayed activity
            OwnerEquityDr(DemoIds.O4, 420.00m),       // −420.00 target
            OwnerEquityCr(DemoIds.O5, 21_345.30m),    // 22,640.30 − 1,295.00
            OwnerEquityCr(DemoIds.O6, 9_870.00m),
            OwnerEquityCr(DemoIds.O7, 1_840.25m),
            OwnerEquityCr(DemoIds.O8, 18_305.60m),
            OwnerEquityCr(DemoIds.AggregateOwners, 140_147.74m), // unlisted 15 owners; +2,840 fee offset → ties oper book

            // Security-deposit trust: book 196,450.00 = held per owner (incl. Jasmine) + unattributed.
            new(AccountCodes.TrustBank(DemoIds.DepositBank), M(196_450.00m), null, BankAccountId: DemoIds.DepositBank),
            DepositCr(DemoIds.T1, DemoIds.O1, 1_450.00m),       // Jasmine
            DepositCr(DemoIds.AggDepO1, DemoIds.O1, 6_950.00m), // o1 dep 8,400.00 − Jasmine 1,450.00
            DepositCr(DemoIds.AggDepO2, DemoIds.O2, 26_100.00m),
            DepositCr(DemoIds.AggDepO3, DemoIds.O3, 2_900.00m),
            DepositCr(DemoIds.AggDepO4, DemoIds.O4, 4_350.00m),
            DepositCr(DemoIds.AggDepO5, DemoIds.O5, 14_200.00m),
            DepositCr(DemoIds.AggDepO6, DemoIds.O6, 11_650.00m),
            DepositCr(DemoIds.AggDepO7, DemoIds.O7, 1_450.00m),
            DepositCr(DemoIds.AggDepO8, DemoIds.O8, 17_800.00m),
            // Unattributed (the 15 unlisted owners): 196,450.00 − 86,850.00 = 109,600.00, no owner.
            new(AccountCodes.SecurityDepositsHeld, null, M(109_600.00m),
                TenantId: DemoIds.AggregateDepositsUnattributed, BankAccountId: DemoIds.DepositBank),

            // PM operating: book 35,400.55 = accumulated swept PM income (mgmt is outside the trust equation).
            new(AccountCodes.PmOperatingBank(DemoIds.MgmtBank), M(35_400.55m), null, BankAccountId: DemoIds.MgmtBank),
            new(AccountCodes.PmIncome, null, M(35_400.55m), BankAccountId: DemoIds.MgmtBank),
        };

        return balanceForward.PostAsync(
            new BalanceForwardRequest(new DateOnly(2026, 1, 31), lines, "Cutover opening positions"), ct);
    }

    // The post-cutover events, in chronological order (§C.8). The focal tenant (t1) descriptions are
    // verbatim from the prototype ledger; other tenants' figures are sized to their final balances.
    private static async Task PostReplayedEventsAsync(IAccountingEvents events, CancellationToken ct)
    {
        var o = DemoIds.OperBank;

        // February (focal tenant).
        await events.PostAsync(new RentCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(1_450m), D(2, 1), "Rent — February 2026"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T1, DemoIds.P1, DemoIds.O1, M(1_450m), D(2, 3), PaymentMethod.Ach, o, "ACH payment · ••4021"), ct);
        await events.PostAsync(new CreditIssued(DemoIds.T1, DemoIds.P1, DemoIds.O1, M(85m), D(2, 17), "Goodwill credit — maintenance delay"), ct);
        await events.PostAsync(new FeeCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(85m), D(2, 18), FeeKind.MaintenanceRecharge, "Recharge — garbage disposal repair"), ct);

        // March.
        await events.PostAsync(new RentCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(1_450m), D(3, 1), "Rent — March 2026"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T1, DemoIds.P1, DemoIds.O1, M(1_450m), D(3, 2), PaymentMethod.Card, o, "Card payment · Visa ••6612"), ct);

        // April (late fee posted before the payment that clears it).
        await events.PostAsync(new RentCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(1_450m), D(4, 1), "Rent — April 2026"), ct);
        await events.PostAsync(new FeeCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(25m), D(4, 6), FeeKind.Late, "Late fee — April"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T1, DemoIds.P1, DemoIds.O1, M(1_475m), D(4, 6), PaymentMethod.Ach, o, "ACH payment · ••4021"), ct);

        // May rent run (all units).
        await events.PostAsync(new RentCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(1_450m), D(5, 1), "Rent — May 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T3, DemoIds.P3, DemoIds.O2, null, M(1_620m), D(5, 1), "Rent — May 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T4, DemoIds.P5, DemoIds.O5, null, M(1_295m), D(5, 1), "Rent — May 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T5, DemoIds.P2, DemoIds.O1, null, M(2_150m), D(5, 1), "Rent — May 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T6, DemoIds.P4, DemoIds.O2, null, M(1_410m), D(5, 1), "Rent — May 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T7, DemoIds.P3, DemoIds.O2, null, M(1_550m), D(5, 1), "Rent — May 2026"), ct);

        // May collections + the fee assessment and sweep.
        await events.PostAsync(new PaymentReceived(DemoIds.T1, DemoIds.P1, DemoIds.O1, M(1_450m), D(5, 3), PaymentMethod.Ach, o, "ACH payment · ••4021"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T5, DemoIds.P2, DemoIds.O1, M(2_225m), D(5, 22), PaymentMethod.Ach, o, "ACH payment — Mercer (overpay)"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T7, DemoIds.P3, DemoIds.O2, M(1_550m), D(5, 26), PaymentMethod.Ach, o, "ACH payment — Tate"), ct);
        await events.PostAsync(new ManagementFeeAssessed(DemoIds.AggregateOwners, null, M(2_840m), D(5, 27), o, "Management fees — May"), ct);
        await events.PostAsync(new PMFeesSwept(M(2_840m), D(5, 27), o, DemoIds.MgmtBank, "Mgmt fee transfer → PM Operating"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T4, DemoIds.P5, DemoIds.O5, M(1_295m), D(5, 28), PaymentMethod.Check, o, "Check payment — Ramsey"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T3, DemoIds.P3, DemoIds.O2, M(1_620m), D(5, 30), PaymentMethod.Ach, o, "ACH payment — Bello"), ct);

        // June: rent run, the owner disbursement, and the Pryor payment.
        await events.PostAsync(new RentCharged(DemoIds.T1, DemoIds.P1, DemoIds.O1, null, M(1_450m), D(6, 1), "Rent — June 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T2, DemoIds.P1, DemoIds.O1, null, M(1_380m), D(6, 1), "Rent — June 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T3, DemoIds.P3, DemoIds.O2, null, M(1_620m), D(6, 1), "Rent — June 2026"), ct);
        await events.PostAsync(new RentCharged(DemoIds.T6, DemoIds.P4, DemoIds.O2, null, M(1_410m), D(6, 1), "Rent — June 2026"), ct);
        await events.PostAsync(new OwnerDisbursed(DemoIds.O1, M(8_200m), D(6, 2), o, "Owner disbursement — Hargrove Family Trust"), ct);
        await events.PostAsync(new PaymentReceived(DemoIds.T2, DemoIds.P1, DemoIds.O1, M(1_380m), D(6, 3), PaymentMethod.Ach, o, "ACH payment — Pryor"), ct);
    }

    private static Money M(decimal amount) => new(amount);

    private static DateOnly D(int month, int day) => new(2026, month, day);
}
