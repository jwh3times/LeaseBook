using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
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
    // Fresh per test instance (ADR-013: composite dim FK forbids reusing a fixed id across orgs).
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();
    private readonly Guid Unit = UuidV7.NewId();
    private readonly Guid Tenant = UuidV7.NewId();

    [Fact]
    public async Task Deposit_against_charges_exceeding_the_open_receivable_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), scope.DepositBankId, "dep"), ct);

        // 1200 ≤ 1500 held (liability guard passes) but > 1000 owed → the receivable guard fires.
        await Should.ThrowAsync<InsufficientReceivableException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(1200m), D(28), scope.DepositBankId, scope.TrustBankId,
                DepositApplication.AgainstCharges, "over-apply"), ct), ct));
    }

    [Fact]
    public async Task Deposit_against_charges_up_to_the_receivable_posts_and_keeps_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), scope.DepositBankId, "dep"), ct);
        await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1000m), D(28), scope.DepositBankId, scope.TrustBankId,
            DepositApplication.AgainstCharges, "apply exactly the receivable"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Deposit_to_owner_income_is_not_guarded_by_the_receivable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        // No charge → receivable is 0, yet damages legitimately exceed it. ToOwnerIncome stays unguarded.
        await PostAsync(scope, new DepositCollected(Tenant, Property, Owner, new Money(1500m), D(1), scope.DepositBankId, "dep"), ct);
        await PostAsync(scope, new DepositApplied(
            Tenant, Property, Owner, new Money(1500m), D(28), scope.DepositBankId, scope.TrustBankId,
            DepositApplication.ToOwnerIncome, "damages"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Prepayment_application_exceeding_the_open_receivable_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(500m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(800m), D(1), scope.TrustBankId, "pp"), ct);

        // 600 ≤ 800 held but > 500 owed → the receivable guard fires.
        await Should.ThrowAsync<InsufficientReceivableException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new PrepaymentApplied(
                Tenant, Property, Owner, new Money(600m), D(28), scope.TrustBankId, "over-apply"), ct), ct));
    }

    [Fact]
    public async Task Prepayment_application_up_to_the_receivable_posts_and_keeps_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(500m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(800m), D(1), scope.TrustBankId, "pp"), ct);
        await PostAsync(scope, new PrepaymentApplied(Tenant, Property, Owner, new Money(500m), D(28), scope.TrustBankId, "apply"), ct);

        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Prepayment_application_from_a_bank_that_does_not_hold_it_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(500m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(800m), D(1), scope.DepositBankId, "pp"), ct);

        // 500 ≤ 800 held by the tenant and ≤ 500 owed, but none of it sits in the operating trust (#475).
        var refused = await Should.ThrowAsync<InsufficientLiabilityException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new PrepaymentApplied(
                Tenant, Property, Owner, new Money(500m), D(28), scope.TrustBankId, "wrong bank"), ct), ct));
        refused.Held.ShouldBe(0m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Credit_up_to_the_receivable_posts_and_keeps_invariants()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, Credit(1000m), ct);

        (await ReceivableAsync(scope, ct)).ShouldBe(0m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Credit_one_cent_over_the_receivable_is_rejected_and_posts_nothing()
    {
        // #652: a credit has no excess path either, so one over the receivable would drive it negative
        // with nothing held behind it.
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);

        var refused = await Should.ThrowAsync<InsufficientReceivableException>(() => PostAsync(scope, Credit(1000.01m), ct));
        (refused.Kind, refused.Requested, refused.Owed, refused.TenantId)
            .ShouldBe((ReceivableSource.Credit, 1000.01m, 1000m, (Guid?)Tenant));
        (await ReceivableAsync(scope, ct)).ShouldBe(1000m);
    }

    [Fact]
    public async Task Credit_to_a_tenant_who_owes_nothing_is_rejected_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        var refused = await Should.ThrowAsync<InsufficientReceivableException>(() => PostAsync(scope, Credit(0.01m), ct));
        refused.Owed.ShouldBe(0m);
        (await ReceivableAsync(scope, ct)).ShouldBe(0m);
    }

    [Fact]
    public async Task Credit_against_a_receivable_already_below_zero_is_rejected_against_zero_owed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        // An allowed unapplied credit leaves the tenant at −50; the next plain credit owes against 0, not −50.
        await PostAsync(scope, Credit(50m, allowUnapplied: true), ct);

        var refused = await Should.ThrowAsync<InsufficientReceivableException>(() => PostAsync(scope, Credit(0.01m), ct));
        refused.Owed.ShouldBe(0m);
        (await ReceivableAsync(scope, ct)).ShouldBe(-50m);
    }

    [Fact]
    public async Task An_allowed_unapplied_credit_posts_past_the_receivable_and_says_so()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(100m), D(1), "rent"), ct);
        var entryId = await PostEntryAsync(scope, Credit(185m, allowUnapplied: true), ct);

        (await ReceivableAsync(scope, ct)).ShouldBe(-85m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
        (await SubtypeAsync(scope, entryId, ct)).ShouldBe(CreditIssued.UnappliedSubtype);
    }

    [Fact]
    public async Task An_allowance_a_credit_did_not_need_leaves_no_mark()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(100m), D(1), "rent"), ct);
        var entryId = await PostEntryAsync(scope, Credit(100m, allowUnapplied: true), ct);

        (await SubtypeAsync(scope, entryId, ct)).ShouldBeNull();
    }

    [Fact]
    public async Task The_ledger_reports_what_a_credit_may_draw_on_not_the_net_balance()
    {
        // A held prepayment lowers the ledger balance but is not yet applied, so it does not lower what
        // the tenant owes: the credit form must offer the receivable, which is what the guard reads.
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ScopeAsync(ct);

        await PostAsync(scope, new RentCharged(Tenant, Property, Owner, Unit, new Money(1000m), D(1), "rent"), ct);
        await PostAsync(scope, new PrepaymentReceived(Tenant, Property, Owner, new Money(400m), D(2), scope.TrustBankId, "pp"), ct);

        var ledger = await LedgerAsync(scope, ct);
        (ledger.Balance, ledger.Owed).ShouldBe((600m, 1000m));
        await PostAsync(scope, Credit(1000m), ct);

        await PostAsync(scope, Credit(50m, allowUnapplied: true), ct);
        (await LedgerAsync(scope, ct)).Owed.ShouldBe(0m, "a receivable below zero leaves nothing owed, not a negative");
    }

    private async Task<TenantLedgerResponse> LedgerAsync(OrgScope scope, CancellationToken ct)
    {
        TenantLedgerResponse result = null!;
        await scope.RunAsync(async () => result = await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(Tenant), ct), ct);
        return result;
    }

    private CreditIssued Credit(decimal amount, bool allowUnapplied = false) =>
        new(Tenant, Property, Owner, new Money(amount), D(10), "goodwill", AllowUnappliedCredit: allowUnapplied);

    private async Task<decimal> ReceivableAsync(OrgScope scope, CancellationToken ct)
    {
        var result = 0m;
        await scope.RunAsync(async () => result = await new BalanceReader(scope.Db).TenantReceivableAsync(Tenant, ct), ct);
        return result;
    }

    private static async Task<Guid> PostEntryAsync(OrgScope scope, AccountingEvent businessEvent, CancellationToken ct)
    {
        var id = Guid.Empty;
        await scope.RunAsync(async () => id = await Events(scope).PostAsync(businessEvent, ct), ct);
        return id;
    }

    private static async Task<string?> SubtypeAsync(OrgScope scope, Guid entryId, CancellationToken ct)
    {
        string? result = null;
        await scope.RunAsync(async () => result = await scope.Db.Set<JournalEntry>()
            .Where(e => e.Id == entryId).Select(e => e.EventSubtype).SingleAsync(ct), ct);
        return result;
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
