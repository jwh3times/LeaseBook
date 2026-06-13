using CsCheck;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-05 catalog property: for random valid amounts, every event's posting balances per basis <i>by
/// construction</i> — driven through the real engine (CsCheck, P29). Each case uses a fresh org so
/// generated cases are isolated and need no cleanup.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class CatalogBalancePropertyTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Every_event_balances_per_basis_for_random_amounts()
    {
        var ct = TestContext.Current.CancellationToken;

        // Exact two-decimal amounts, 0.01 .. 25,000.00.
        var genAmount = Gen.Int[1, 2_500_000].Select(cents => new Money(cents / 100m));

        await genAmount.SampleAsync(amount => RunCaseAsync(amount, ct), iter: 12);
    }

    private async Task RunCaseAsync(Money amount, CancellationToken ct)
    {
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var tenant = UuidV7.NewId();
        var owner = UuidV7.NewId();
        var property = UuidV7.NewId();
        var depTenant = UuidV7.NewId();
        var preTenant = UuidV7.NewId();
        var feeOwner = UuidV7.NewId();
        var drawOwner = UuidV7.NewId();
        var big = new Money(50_000_000m); // dwarfs any generated amount so guards always pass

        // Seed directory rows for every dimension id these events carry, so the journal-dimension FKs
        // resolve when the engine posts (P38 / ADR-008).
        await EnsureDirectoryAsync(fixture, ct,
            owners: [owner, feeOwner, drawOwner], tenants: [tenant, depTenant, preTenant], properties: [property]);

        // Accrual-only charges.
        await AssertBalancesAsync(scope, new RentCharged(tenant, property, owner, null, amount, D(1), "rent"), ct);
        await AssertBalancesAsync(scope, new CreditIssued(tenant, property, owner, amount, D(2), "credit"), ct);

        // Auto-split payment: charge `amount`, pay double → both the receivable and prepayment legs fire.
        await PostAsync(scope, new RentCharged(tenant, property, owner, null, amount, D(1), "rent2"), ct);
        await AssertBalancesAsync(scope, new PaymentReceived(
            tenant, property, owner, new Money(amount.Amount * 2m), D(3), PaymentMethod.Ach, TrustBankId, "pay"), ct);

        // Deposit applications (both variants) — a fresh tenant keeps the held balance clean.
        await PostAsync(scope, new DepositCollected(depTenant, property, owner, big, D(1), DepositBankId, "dep"), ct);
        await AssertBalancesAsync(scope, new DepositApplied(
            depTenant, property, owner, amount, D(28), DepositBankId, TrustBankId, DepositApplication.ToOwnerIncome, "da1"), ct);
        await AssertBalancesAsync(scope, new DepositApplied(
            depTenant, property, owner, amount, D(28), DepositBankId, TrustBankId, DepositApplication.AgainstCharges, "da2"), ct);

        // Prepayment application.
        await PostAsync(scope, new PrepaymentReceived(preTenant, property, owner, big, D(1), TrustBankId, "pp"), ct);
        await AssertBalancesAsync(scope, new PrepaymentApplied(preTenant, property, owner, amount, D(28), TrustBankId, "pa"), ct);

        // Fee then sweep (a fresh owner so this owner's equity is untouched by the draws above).
        await PostAsync(scope, new ManagementFeeAssessed(feeOwner, property, big, D(27), TrustBankId, "fee"), ct);
        await AssertBalancesAsync(scope, new PMFeesSwept(amount, D(27), TrustBankId, OperatingBankId, "sweep"), ct);

        // Contribution then disbursement.
        await PostAsync(scope, new OwnerContribution(drawOwner, property, big, D(1), TrustBankId, "contrib"), ct);
        await AssertBalancesAsync(scope, new OwnerDisbursed(drawOwner, amount, D(2), TrustBankId, "draw"), ct);
    }

    private static async Task PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct) =>
        await scope.RunAsync(async () => await Events(scope).PostAsync(businessEvent, ct), ct);

    private static async Task AssertBalancesAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        var lines = await ReadLinesAsync(scope, id, ct);

        foreach (var basis in new[] { EntryBasis.Cash, EntryBasis.Accrual })
        {
            var debits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Debit ?? 0m);
            var credits = lines.Where(l => l.Basis == basis || l.Basis == EntryBasis.Both).Sum(l => l.Credit ?? 0m);
            debits.ShouldBe(credits, $"{businessEvent.GetType().Name} must balance in {basis} basis");
        }
    }

    private static DateOnly D(int day) => new(2026, 5, day);
}
