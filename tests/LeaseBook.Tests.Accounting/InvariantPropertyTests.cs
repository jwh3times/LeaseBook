using CsCheck;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-07 property suite (P29): random valid event sequences run through the real engine on a fresh org
/// per case; I1–I4 must hold <b>after every posting</b> ("at all times"), voids included, and cash and
/// accrual owner totals must converge after a settlement epilogue (I5). The iteration count is the
/// <c>LEASEBOOK_PROPERTY_ITER</c> knob (default 20; CI/full runs set 100+ — see runbook).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InvariantPropertyTests(PostgresFixture fixture)
{
    private enum Op
    {
        Charge, Pay, DepositCollect, DepositApply, Fee, Sweep, Disburse, Void,
    }

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("LEASEBOOK_PROPERTY_ITER"), out var n) ? n : 20;

    [Fact]
    public async Task Random_valid_sequences_keep_the_invariants_and_converge_after_settlement()
    {
        var ct = TestContext.Current.CancellationToken;

        // 14 weighted ops per case; amounts are exact cents 0.01..3,000.00.
        var genOp = Gen.Select(Gen.Int[0, 7].Select(i => (Op)i), Gen.Int[1, 300_000]);
        await genOp.Array[14].SampleAsync(ops => RunCaseAsync(ops, ct), iter: Iterations);
    }

    private async Task RunCaseAsync((Op Kind, int Cents)[] ops, CancellationToken ct)
    {
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var tenant = UuidV7.NewId();
        var owner = UuidV7.NewId();
        var property = UuidV7.NewId();
        var voidable = new List<Guid>();

        // Seed directory rows for the dimension ids this case posts, so the journal-dimension FKs
        // resolve when the engine posts (P38 / ADR-008).
        await EnsureDirectoryAsync(fixture, ct, owners: [owner], tenants: [tenant], properties: [property]);

        foreach (var (kind, cents) in ops)
        {
            await scope.RunAsync(async () =>
            {
                await ApplyAsync(scope, kind, cents / 100m, tenant, owner, property, voidable, ct);

                // "at all times": every always-true invariant holds after this posting.
                var violations = await new InvariantChecks(scope.Db).CheckCoreAsync(ct);
                violations.ShouldBeEmpty(string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
            }, ct);
        }

        // Settlement epilogue: clear the receivable so the bases can converge.
        await scope.RunAsync(async () =>
        {
            var receivable = await new BalanceReader(scope.Db).TenantReceivableAsync(tenant, ct);
            if (receivable > 0m)
            {
                await Events(scope).PostAsync(new PaymentReceived(
                    tenant, property, owner, new Money(receivable), new DateOnly(2026, 2, 5), PaymentMethod.Ach, TrustBankId, "settle"), ct);
            }
        }, ct);

        await scope.RunAsync(async () =>
        {
            var cash = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(owner, "cash"), ct)).Balance;
            var accrual = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(owner, "accrual"), ct)).Balance;
            cash.ShouldBe(accrual, "cash and accrual owner totals must converge after settlement (I5)");
            (await new InvariantChecks(scope.Db).CheckCoreAsync(ct)).ShouldBeEmpty();
        }, ct);
    }

    private async Task ApplyAsync(
        OrgScope scope, Op kind, decimal amount, Guid tenant, Guid owner, Guid property,
        List<Guid> voidable, CancellationToken ct)
    {
        var events = Events(scope);
        var balances = new BalanceReader(scope.Db);

        switch (kind)
        {
            case Op.Charge:
                voidable.Add(await events.PostAsync(new RentCharged(tenant, property, owner, null, new Money(amount), new DateOnly(2026, 2, 1), "rc"), ct));
                break;
            case Op.Pay:
                voidable.Add(await events.PostAsync(new PaymentReceived(tenant, property, owner, new Money(amount), new DateOnly(2026, 2, 3), PaymentMethod.Ach, TrustBankId, "pay"), ct));
                break;
            case Op.DepositCollect:
                voidable.Add(await events.PostAsync(new DepositCollected(tenant, property, owner, new Money(amount), new DateOnly(2026, 2, 1), DepositBankId, "dep"), ct));
                break;
            case Op.DepositApply:
                var held = await balances.DepositsHeldAsync(tenant, ct);
                var apply = Math.Min(amount, held);
                if (apply > 0m)
                {
                    voidable.Add(await events.PostAsync(new DepositApplied(tenant, property, owner, new Money(apply), new DateOnly(2026, 2, 28), DepositBankId, TrustBankId, DepositApplication.ToOwnerIncome, "da"), ct));
                }

                break;
            case Op.Fee:
                voidable.Add(await events.PostAsync(new ManagementFeeAssessed(owner, property, new Money(amount), new DateOnly(2026, 2, 27), TrustBankId, "fee"), ct));
                break;
            case Op.Sweep:
                var fees = await balances.HeldFeesAsync(TrustBankId, ct);
                var sweep = Math.Min(amount, fees);
                if (sweep > 0m)
                {
                    voidable.Add(await events.PostAsync(new PMFeesSwept(new Money(sweep), new DateOnly(2026, 2, 27), TrustBankId, OperatingBankId, "sw"), ct));
                }

                break;
            case Op.Disburse:
                var equity = await balances.OwnerEquityCashAsync(owner, ct);
                var draw = Math.Min(amount, Math.Max(equity, 0m));
                if (draw > 0m)
                {
                    voidable.Add(await events.PostAsync(new OwnerDisbursed(owner, new Money(draw), new DateOnly(2026, 2, 28), TrustBankId, "draw"), ct));
                }

                break;
            case Op.Void:
                if (voidable.Count > 0)
                {
                    var id = voidable[^1];
                    voidable.RemoveAt(voidable.Count - 1);
                    await Reversal(scope).ReverseAsync(id, "rng void", new DateOnly(2026, 2, 2), ct);
                }

                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }
}
