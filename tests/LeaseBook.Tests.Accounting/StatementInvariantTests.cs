using CsCheck;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Statements;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-01 property/invariant suite for the statement engine:
/// (1) Tie-out: every owner statement over a random event sequence has Variance==0 and no
///     UncategorizedEventException (exhaustive section map).
/// (2) PM-income exclusion: no StatementLine on any owner statement originates from a pm_income
///     account line — structural because the query is scoped to account_class='owner_equity'.
/// (3) Consolidated == Σ per-property: for an owner with two properties, the consolidated statement
///     ending equals the sum of the two per-property endings, and section subtotals add up.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StatementInvariantTests(PostgresFixture fixture)
{
    private enum Op { Charge, Pay, Fee, Disburse, DepositApply }

    private static int Iterations =>
        int.TryParse(Environment.GetEnvironmentVariable("LEASEBOOK_PROPERTY_ITER"), out var n) ? n : 20;

    // ----- Fact 1 + 2: tie-out and PM-exclusion over random valid sequences ----------------------

    [Fact]
    public async Task Random_valid_sequences_tie_out_and_exclude_pm_income_on_every_basis()
    {
        var ct = TestContext.Current.CancellationToken;

        var genOp = Gen.Select(Gen.Int[0, 4].Select(i => (Op)i), Gen.Int[1, 300_000]);
        await genOp.Array[10].SampleAsync(ops => RunTieOutCaseAsync(ops, ct), iter: Iterations);
    }

    private async Task RunTieOutCaseAsync((Op Kind, int Cents)[] ops, CancellationToken ct)
    {
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var tenant = UuidV7.NewId();
        var owner = UuidV7.NewId();
        var property = UuidV7.NewId();

        await EnsureDirectoryAsync(fixture, scope, ct, owners: [owner], tenants: [tenant], properties: [property]);

        var date = new DateOnly(2026, 5, 15);

        // Run a mix of rent charges, payments, fee assessments, disbursements, and deposit applies.
        foreach (var (kind, cents) in ops)
        {
            var amount = cents / 100m;
            await scope.RunAsync(async () =>
            {
                var events = Events(scope);
                var balances = new BalanceReader(scope.Db);

                switch (kind)
                {
                    case Op.Charge:
                        await events.PostAsync(new RentCharged(tenant, property, owner, null, new Money(amount), date, "rent"), ct);
                        break;
                    case Op.Pay:
                        await events.PostAsync(new PaymentReceived(tenant, property, owner, new Money(amount), date, PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
                        break;
                    case Op.Fee:
                        await events.PostAsync(new ManagementFeeAssessed(owner, property, new Money(amount), date, scope.TrustBankId, "fee"), ct);
                        break;
                    case Op.Disburse:
                        var equity = await balances.OwnerEquityCashAsync(owner, ct);
                        var draw = Math.Min(amount, Math.Max(equity, 0m));
                        if (draw > 0m)
                        {
                            await events.PostAsync(new OwnerDisbursed(owner, new Money(draw), date, scope.TrustBankId, "draw"), ct);
                        }
                        break;
                    case Op.DepositApply:
                        // First collect a small deposit so apply has something to work with.
                        await events.PostAsync(new DepositCollected(tenant, property, owner, new Money(amount), date, scope.DepositBankId, "dep"), ct);
                        var held = await balances.DepositsHeldAsync(tenant, ct);
                        if (held > 0m)
                        {
                            await events.PostAsync(new DepositApplied(tenant, property, owner, new Money(held), date, scope.DepositBankId, scope.TrustBankId, DepositApplication.ToOwnerIncome, "da"), ct);
                        }
                        break;
                }
            }, ct);
        }

        // Assert tie-out + PM exclusion for both bases, May 2026.
        await scope.RunAsync(async () =>
        {
            foreach (var basis in new[] { "cash", "accrual" })
            {
                var handler = new GetOwnerStatementDataHandler(scope.Db);
                var result = await handler.Handle(
                    new GetOwnerStatementData([owner], null, 2026, 5, basis), ct);

                result.ByOwner.ContainsKey(owner).ShouldBeTrue($"owner missing from {basis} statement");
                var s = result.ByOwner[owner];

                // Fact 1: tie-out — no UncategorizedEventException was thrown (would have aborted) and
                // the categorical sum equals the raw sum (Variance==0).
                s.TieOut.Variance.ShouldBe(0m, $"{basis}: statement variance must be $0.00");
                s.TieOut.Balanced.ShouldBeTrue($"{basis}: statement must be balanced");
                (s.Beginning + s.Sections.Sum(sec => sec.Subtotal)).ShouldBe(s.Ending,
                    $"{basis}: beginning + section sums must equal ending");

                // Fact 2: PM-income exclusion — the pm_income account class carries no owner_id, so it
                // cannot appear in any owner-scoped statement section. Assert no section line maps to
                // ManagementFeeAssessed with a positive amount (the fee is a debit = negative to owner).
                s.Sections.SelectMany(sec => sec.Lines)
                    .ShouldNotContain(l => l.EventType == "ManagementFeeAssessed" && l.Amount > 0,
                        $"{basis}: ManagementFeeAssessed must appear as an expense (negative), not income");
            }
        }, ct);
    }

    // ----- Fact 3: consolidated == Σ per-property -------------------------------------------------

    [Fact]
    public async Task Consolidated_statement_equals_sum_of_per_property_statements()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var owner = UuidV7.NewId();
        var tenantA = UuidV7.NewId();
        var tenantB = UuidV7.NewId();
        var propertyA = UuidV7.NewId();
        var propertyB = UuidV7.NewId();

        await EnsureDirectoryAsync(fixture, scope, ct,
            owners: [owner],
            tenants: [tenantA, tenantB],
            properties: [propertyA, propertyB]);

        var date = new DateOnly(2026, 5, 10);

        // Post rent + payment for property A (1,200) and property B (950).
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(tenantA, propertyA, owner, null, new Money(1_200m), date, "rent A"), ct);
            await events.PostAsync(new PaymentReceived(tenantA, propertyA, owner, new Money(1_200m), date, PaymentMethod.Ach, scope.TrustBankId, "pay A"), ct);
            await events.PostAsync(new RentCharged(tenantB, propertyB, owner, null, new Money(950m), date, "rent B"), ct);
            await events.PostAsync(new PaymentReceived(tenantB, propertyB, owner, new Money(950m), date, PaymentMethod.Ach, scope.TrustBankId, "pay B"), ct);
        }, ct);

        await scope.RunAsync(async () =>
        {
            var handler = new GetOwnerStatementDataHandler(scope.Db);

            // Consolidated (no property filter).
            var consolidated = (await handler.Handle(
                new GetOwnerStatementData([owner], null, 2026, 5, "cash"), ct)).ByOwner[owner];

            // Per-property.
            var perA = (await handler.Handle(
                new GetOwnerStatementData([owner], propertyA, 2026, 5, "cash"), ct)).ByOwner[owner];
            var perB = (await handler.Handle(
                new GetOwnerStatementData([owner], propertyB, 2026, 5, "cash"), ct)).ByOwner[owner];

            // Ending must equal sum of per-property endings.
            consolidated.Ending.ShouldBe(perA.Ending + perB.Ending,
                "consolidated ending must equal Σ per-property endings");

            // Each section subtotal in the consolidated must equal the sum of the per-property subtotals
            // for that section (sections may be absent if no activity — treat absent as 0).
            var allKeys = consolidated.Sections.Select(s => s.Key).ToHashSet();
            foreach (var key in allKeys)
            {
                var consSubtotal = consolidated.Sections.FirstOrDefault(s => s.Key == key)?.Subtotal ?? 0m;
                var aSubtotal = perA.Sections.FirstOrDefault(s => s.Key == key)?.Subtotal ?? 0m;
                var bSubtotal = perB.Sections.FirstOrDefault(s => s.Key == key)?.Subtotal ?? 0m;
                consSubtotal.ShouldBe(aSubtotal + bSubtotal,
                    $"consolidated section {key} subtotal must equal Σ per-property subtotals");
            }

            // Structural: both per-property statements must also tie out.
            perA.TieOut.Balanced.ShouldBeTrue("per-property A must balance");
            perB.TieOut.Balanced.ShouldBeTrue("per-property B must balance");
        }, ct);
    }
}
