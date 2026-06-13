using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// M3 / ADR-011 / P51: the deposit-against-charges and prepayment over-application guard. Unlike
/// <c>PaymentReceived</c> (which auto-splits the excess to a prepayment), an application has no excess
/// path, so over-applying would silently drive the receivable negative — the engine rejects with
/// <c>insufficient_receivable</c>. A deposit applied <c>ToOwnerIncome</c> (damages) stays unguarded.
/// Valid applications post and keep the core invariants (I1–I4) clean.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReceivableGuardTests(PostgresFixture fixture)
{
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid Unit = Guid.Parse("00000000-0000-0000-0000-0000000000c1");
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000f1");

    [Fact]
    public async Task Deposit_against_charges_exceeding_the_open_receivable_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), DepositBankId, "dep"), ct);

        // 1200 ≤ 1500 held (liability guard passes) but > 1000 owed → the receivable guard fires.
        await Should.ThrowAsync<InsufficientReceivableException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(1200m), D(28), DepositBankId, TrustBankId,
                DepositApplication.AgainstCharges, "over-apply"), ct), ct));
    }

    [Fact]
    public async Task Deposit_against_charges_up_to_the_receivable_posts_and_keeps_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), DepositBankId, "dep"), ct);
        await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1000m), D(28), DepositBankId, TrustBankId,
            DepositApplication.AgainstCharges, "apply exactly the receivable"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deposit_to_owner_income_is_not_guarded_by_the_receivable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        // No charge → receivable is 0, yet damages legitimately exceed it. ToOwnerIncome stays unguarded.
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), DepositBankId, "dep"), ct);
        await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1500m), D(28), DepositBankId, TrustBankId,
            DepositApplication.ToOwnerIncome, "damages"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Prepayment_application_exceeding_the_open_receivable_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(500m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(800m), D(1), TrustBankId, "pp"), ct);

        // 600 ≤ 800 held but > 500 owed → the receivable guard fires.
        await Should.ThrowAsync<InsufficientReceivableException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new PrepaymentApplied(
                Tenant, Property, Owner, new Money(600m), D(28), TrustBankId, "over-apply"), ct), ct));
    }

    [Fact]
    public async Task Prepayment_application_up_to_the_receivable_posts_and_keeps_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(500m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(800m), D(1), TrustBankId, "pp"), ct);
        await PostAsync(scope, new PrepaymentApplied(Tenant, Property, Owner, new Money(500m), D(28), TrustBankId, "apply"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    private Task<OrgScope> ScopeAsync(CancellationToken ct) =>
        ProvisionedScopeAsync(fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property], units: [Unit]);

    private static Task PostAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct) =>
        scope.RunAsync(() => Events(scope).PostAsync(businessEvent, ct), ct);

    private static async Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(OrgScope scope, CancellationToken ct)
    {
        IReadOnlyList<InvariantViolation> result = [];
        await scope.RunAsync(async () => result = await new InvariantChecks(scope.Db).CheckCoreAsync(ct), ct);
        return result;
    }

    private static DateOnly D(int day) => new(2026, 2, day);
}
