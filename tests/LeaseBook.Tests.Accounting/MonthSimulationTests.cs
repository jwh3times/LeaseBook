using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-07 month simulation (TODO M1.4): five units through a full month — rent run, partial
/// collections, a late fee, a deposit collected at move-in and one applied at move-out, fees assessed +
/// swept, a correction (void), and owner disbursements. The core invariants are asserted <b>after every
/// posting</b> ("at all times"), and cash and accrual owner totals reconcile after settlement.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class MonthSimulationTests(PostgresFixture fixture)
{
    // Two owners; p1 → o1 (t1, t2), p2 → o2 (t3, t4, t5).
    private static readonly Guid O1 = Guid.Parse("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid O2 = Guid.Parse("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid P1 = Guid.Parse("00000000-0000-0000-0000-0000000000b1");
    private static readonly Guid P2 = Guid.Parse("00000000-0000-0000-0000-0000000000b2");
    private static readonly Guid T1 = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid T2 = Guid.Parse("00000000-0000-0000-0000-0000000000c2");
    private static readonly Guid T3 = Guid.Parse("00000000-0000-0000-0000-0000000000c3");
    private static readonly Guid T4 = Guid.Parse("00000000-0000-0000-0000-0000000000c4");
    private static readonly Guid T5 = Guid.Parse("00000000-0000-0000-0000-0000000000c5");

    [Fact]
    public async Task A_full_month_keeps_the_invariants_at_every_step_and_reconciles()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        // Move-in deposits for t1 (p1/o1) and t5 (p2/o2).
        await PostAndCheckAsync(scope, new DepositCollected(T1, P1, O1, new Money(1450m), Feb(1), DepositBankId, "t1 move-in"), ct);
        await PostAndCheckAsync(scope, new DepositCollected(T5, P2, O2, new Money(2150m), Feb(1), DepositBankId, "t5 move-in"), ct);

        // Rent run.
        await PostAndCheckAsync(scope, new RentCharged(T1, P1, O1, null, new Money(1450m), Feb(1), "rent t1"), ct);
        await PostAndCheckAsync(scope, new RentCharged(T2, P1, O1, null, new Money(1380m), Feb(1), "rent t2"), ct);
        await PostAndCheckAsync(scope, new RentCharged(T3, P2, O2, null, new Money(1620m), Feb(1), "rent t3"), ct);
        await PostAndCheckAsync(scope, new RentCharged(T4, P2, O2, null, new Money(1295m), Feb(1), "rent t4"), ct);
        await PostAndCheckAsync(scope, new RentCharged(T5, P2, O2, null, new Money(2150m), Feb(1), "rent t5"), ct);

        // Collections (t5 stays unpaid this month).
        await PostAndCheckAsync(scope, new PaymentReceived(T1, P1, O1, new Money(1450m), Feb(3), PaymentMethod.Ach, TrustBankId, "pay t1"), ct);

        // A correction: t2's payment is entered, found wrong, voided, then re-posted.
        var wrong = await PostAndCheckAsync(scope, new PaymentReceived(T2, P1, O1, new Money(1830m), Feb(4), PaymentMethod.Card, TrustBankId, "pay t2 (typo)"), ct);
        await scope.RunAsync(() => Reversal(scope).ReverseAsync(wrong, "wrong amount", Feb(4), ct), ct);
        await AssertCoreCleanAsync(scope, ct); // I6: the void leaves the invariants intact
        await PostAndCheckAsync(scope, new PaymentReceived(T2, P1, O1, new Money(1380m), Feb(4), PaymentMethod.Card, TrustBankId, "pay t2"), ct);

        // A late fee on t3, then it pays rent + fee.
        await PostAndCheckAsync(scope, new FeeCharged(T3, P2, O2, null, new Money(25m), Feb(10), FeeKind.Late, "late t3"), ct);
        await PostAndCheckAsync(scope, new PaymentReceived(T3, P2, O2, new Money(1645m), Feb(12), PaymentMethod.Ach, TrustBankId, "pay t3"), ct);
        await PostAndCheckAsync(scope, new PaymentReceived(T4, P2, O2, new Money(1295m), Feb(14), PaymentMethod.Check, TrustBankId, "pay t4"), ct);

        // Move-out: t1's deposit applied to owner income.
        await PostAndCheckAsync(scope, new DepositApplied(T1, P1, O1, new Money(1450m), Feb(26), DepositBankId, TrustBankId, DepositApplication.ToOwnerIncome, "t1 move-out"), ct);

        // Fees assessed and swept.
        await PostAndCheckAsync(scope, new ManagementFeeAssessed(O1, P1, new Money(290m), Feb(27), TrustBankId, "fee o1"), ct);
        await PostAndCheckAsync(scope, new ManagementFeeAssessed(O2, P2, new Money(310m), Feb(27), TrustBankId, "fee o2"), ct);
        await PostAndCheckAsync(scope, new PMFeesSwept(new Money(600m), Feb(27), TrustBankId, OperatingBankId, "sweep"), ct);

        // Owner disbursements (within each owner's cash equity).
        await PostAndCheckAsync(scope, new OwnerDisbursed(O1, new Money(3000m), Feb(28), TrustBankId, "draw o1"), ct);
        await PostAndCheckAsync(scope, new OwnerDisbursed(O2, new Money(2000m), Feb(28), TrustBankId, "draw o2"), ct);

        // Settlement epilogue: t5 finally pays, so every receivable is cleared and the bases converge.
        await PostAndCheckAsync(scope, new PaymentReceived(T5, P2, O2, new Money(2150m), Feb(28), PaymentMethod.Ach, TrustBankId, "settle t5"), ct);

        await AssertBasesConvergeAsync(scope, O1, ct);
        await AssertBasesConvergeAsync(scope, O2, ct);
        await AssertCoreCleanAsync(scope, ct);
    }

    private async Task<Guid> PostAndCheckAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () =>
        {
            id = await Events(scope).PostAsync(businessEvent, ct);
            var violations = await new InvariantChecks(scope.Db).CheckCoreAsync(ct);
            violations.ShouldBeEmpty(
                $"after {businessEvent.GetType().Name}: " + string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
        }, ct);
        return id;
    }

    private static async Task AssertCoreCleanAsync(OrgScope scope, CancellationToken ct)
    {
        IReadOnlyList<InvariantViolation> violations = [];
        await scope.RunAsync(async () => violations = await new InvariantChecks(scope.Db).CheckCoreAsync(ct), ct);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
    }

    private static async Task AssertBasesConvergeAsync(OrgScope scope, Guid ownerId, CancellationToken ct)
    {
        await scope.RunAsync(async () =>
        {
            var cash = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(ownerId, "cash"), ct)).Balance;
            var accrual = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(ownerId, "accrual"), ct)).Balance;
            cash.ShouldBe(accrual, $"owner {ownerId} cash/accrual must converge after settlement");
        }, ct);
    }

    private static DateOnly Feb(int day) => new(2026, 2, day);
}
