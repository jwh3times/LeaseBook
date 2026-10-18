using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// The ledger read models (§C.6) sent through the real dispatcher, so each query's validator runs in
/// front of its handler as it does behind the endpoint. What is pinned here is what the projections
/// choose: the rules that refuse a malformed query, the basis an owner read selects, the owner ledger
/// with no rows, the batch lookups and their empty short circuits, and the period-charge guard's
/// subtype. PM income never reaches an owner read: a management fee shows only as the owner's debit.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class LedgerQueryTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _otherOwner = UuidV7.NewId();
    private readonly Guid _tenant = UuidV7.NewId();
    private readonly Guid _otherTenant = UuidV7.NewId();
    private readonly Guid _thirdTenant = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static readonly DateOnly Feb3 = new(2026, 2, 3);
    private static readonly DateOnly Feb27 = new(2026, 2, 27);

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly Guid Some = UuidV7.NewId();
    private const string BasisMessage = "Basis must be 'cash' or 'accrual'.";

    private static readonly Dictionary<string, (Func<ISender, Task> Send, string[] Properties, string? Message)> Cases = new()
    {
        ["GetOwnerLedger.OwnerId"] = Query(new GetOwnerLedger(Guid.Empty, "cash"), "OwnerId"),
        ["GetOwnerLedger.Basis"] = Query(new GetOwnerLedger(Some, "both"), "Basis", BasisMessage),
        ["GetOwnerLedger.Basis.Case"] = Query(new GetOwnerLedger(Some, "Cash"), "Basis", BasisMessage),

        ["GetBankRegister.BankAccountId"] = Query(new GetBankRegister(Guid.Empty), "BankAccountId"),
        ["GetBankRegister.Page"] = Query(new GetBankRegister(Some, Page: 0), "Page"),
        ["GetBankRegister.PageSize"] = Query(new GetBankRegister(Some, PageSize: 0), "PageSize"),
        ["GetBankRegister.PageSize.Max"] = Query(new GetBankRegister(Some, PageSize: 201), "PageSize"),

        ["GetDelinquencyAging.AsOf"] = Query(new GetDelinquencyAging(default), "AsOf"),

        ["GetManagementFeeIncome.Year"] = Query(new GetManagementFeeIncome(1999, 5), "Year"),
        ["GetManagementFeeIncome.Month"] = Query(new GetManagementFeeIncome(2026, 13), "Month"),

        ["GetRentObligationEntries.SourceRefs"] = Query(new GetRentObligationEntries([], Feb1), "SourceRefs"),
        ["GetRentObligationEntries.AsOf"] = Query(new GetRentObligationEntries(["rent:1"], default), "AsOf"),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_query_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (send, properties, message) = Cases[name];
        var sender = await UnusableSenderAsync();

        var refused = await Should.ThrowAsync<ValidationException>(() => send(sender));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        if (message is not null)
        {
            refused.Errors.First().ErrorMessage.ShouldBe(message);
        }
    }

    // ---- the owner ledger ----

    [Fact]
    public async Task An_owner_ledger_is_the_owners_equity_in_the_basis_asked_for_and_never_the_pm_income()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        Guid rent = default, payment = default, fee = default;
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            rent = await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(1450m), Feb1, "rent"), ct);
            payment = await events.PostAsync(new PaymentReceived(
                _tenant, _property, _owner, new Money(1450m), Feb3, PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
            // The fee credits pm_income 145 with no owner; the owner sees only the 145 debit.
            fee = await events.PostAsync(new ManagementFeeAssessed(_owner, _property, new Money(145m), Feb27, scope.TrustBankId, "fee"), ct);
        }, ct);

        var cash = await QueryAsync(scope, new GetOwnerLedger(_owner, "cash"), ct);
        (cash.OwnerId, cash.Basis, cash.Balance).ShouldBe((_owner, "cash", 1305m));
        cash.Rows.Select(r => (r.EntryId, r.Date, r.EventType, r.PropertyId, r.Amount, r.Balance)).ShouldBe(
        [
            (payment, Feb3, "PaymentReceived", (Guid?)_property, 1450m, 1450m),
            (fee, Feb27, "ManagementFeeAssessed", _property, -145m, 1305m),
        ]);

        var accrual = await QueryAsync(scope, new GetOwnerLedger(_owner, "accrual"), ct);
        (accrual.OwnerId, accrual.Basis, accrual.Balance).ShouldBe((_owner, "accrual", 1305m));
        accrual.Rows.Select(r => (r.EntryId, r.EventType, r.Amount, r.Balance)).ShouldBe(
        [
            (rent, "RentCharged", 1450m, 1450m),
            (fee, "ManagementFeeAssessed", -145m, 1305m),
        ]);
    }

    [Fact]
    public async Task An_owner_ledger_with_no_rows_has_a_zero_balance()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        // The owner's only activity is an accrual-only charge, so its cash ledger has no rows; the
        // other owner has no activity at all.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new RentCharged(_tenant, _property, _owner, null, new Money(1450m), Feb1, "rent"), ct), ct);

        var cash = await QueryAsync(scope, new GetOwnerLedger(_owner, "cash"), ct);
        cash.ShouldBe(new OwnerLedgerResponse(_owner, "cash", 0m, cash.Rows));
        cash.Rows.ShouldBeEmpty();

        var other = await QueryAsync(scope, new GetOwnerLedger(_otherOwner, "accrual"), ct);
        other.ShouldBe(new OwnerLedgerResponse(_otherOwner, "accrual", 0m, other.Rows));
        other.Rows.ShouldBeEmpty();

        // The same owner on accrual does have the charge, so the empty cash ledger is the basis filter.
        (await QueryAsync(scope, new GetOwnerLedger(_owner, "accrual"), ct)).Balance.ShouldBe(1450m);
    }

    // ---- owner balances ----

    [Fact]
    public async Task Owner_balances_count_cash_or_accrual_equity_and_the_same_deposits_either_way()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(1450m), Feb1, "rent"), ct);
            await events.PostAsync(new PaymentReceived(
                _tenant, _property, _owner, new Money(1000m), Feb3, PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
            await events.PostAsync(new ManagementFeeAssessed(_owner, _property, new Money(100m), Feb27, scope.TrustBankId, "fee"), ct);
            await events.PostAsync(new DepositCollected(_tenant, _property, _owner, new Money(500m), Feb1, scope.DepositBankId, "deposit"), ct);
        }, ct);

        // Cash: 1000 collected − 100 fee. Accrual: 1450 earned − 100 fee. Deposits are 'both'.
        var cash = await QueryAsync(scope, new GetOwnerBalances("cash"), ct);
        cash.Rows.ShouldBe([new OwnerBalanceRow(_owner, 900m, 500m, 1400m)]);
        cash.Totals.ShouldBe(new OwnerBalancesTotals(900m, 500m, 1400m));

        var accrual = await QueryAsync(scope, new GetOwnerBalances("accrual"), ct);
        accrual.Rows.ShouldBe([new OwnerBalanceRow(_owner, 1350m, 500m, 1850m)]);
        accrual.Totals.ShouldBe(new OwnerBalancesTotals(1350m, 500m, 1850m));

        // The basis is read case-insensitively; anything that is not accrual is cash.
        (await QueryAsync(scope, new GetOwnerBalances("ACCRUAL"), ct)).Rows.ShouldBe(accrual.Rows);
        (await QueryAsync(scope, new GetOwnerBalances("both"), ct)).Rows.ShouldBe(cash.Rows);
        (await QueryAsync(scope, new GetOwnerBalances(), ct)).Rows.ShouldBe(cash.Rows);
    }

    // ---- batch lookups ----

    [Fact]
    public async Task Internal_notes_come_back_for_the_requested_entries_that_have_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        Guid noted = default, plain = default, unrequested = default;
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            noted = await events.PostAsync(new RentCharged(
                _tenant, _property, _owner, null, new Money(100m), Feb1, "rent", "rent:1", InternalNote: "Lease addendum pending"), ct);
            plain = await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(100m), Feb1, "rent", "rent:2"), ct);
            unrequested = await events.PostAsync(new RentCharged(
                _tenant, _property, _owner, null, new Money(100m), Feb1, "rent", "rent:3", InternalNote: "not asked for"), ct);
        }, ct);

        var notes = await QueryAsync(scope, new GetEntryInternalNotes([noted, plain, noted]), ct);

        notes.ShouldBe(new Dictionary<Guid, string> { [noted] = "Lease addendum pending" });
        notes.ShouldNotContainKey(unrequested);
    }

    [Fact]
    public async Task Existing_source_refs_are_the_candidates_already_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(100m), Feb1, "rent", "rent:feb"), ct);
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(100m), Feb1, "rent", "rent:other"), ct);
        }, ct);

        var existing = await QueryAsync(scope, new GetExistingSourceRefs(["rent:feb", "rent:mar"]), ct);

        existing.ExistingKeys.ShouldBe(["rent:feb"], ignoreOrder: true);
    }

    [Fact]
    public async Task An_empty_batch_is_answered_without_reading_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        var sender = await UnusableSenderAsync();

        (await sender.Query(new GetEntryInternalNotes([]), ct)).ShouldBeEmpty();
        (await sender.Query(new GetExistingSourceRefs([]), ct)).ExistingKeys.ShouldBeEmpty();
        (await sender.Query(new GetTenantsChargedInPeriod("RentCharged", null, 2026, 2, []), ct)).TenantIds.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_period_charge_guard_matches_the_subtype_only_when_one_is_asked_for()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await NewScope(ct);
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(_tenant, _property, _owner, null, new Money(100m), Feb1, "rent"), ct);
            await events.PostAsync(new FeeCharged(
                _otherTenant, _property, _owner, null, new Money(25m), Feb3, FeeKind.Late, "late fee"), ct);
            await events.PostAsync(new FeeCharged(
                _thirdTenant, _property, _owner, null, new Money(25m), Feb3, FeeKind.Other, "key replacement"), ct);
            // March rent for the third tenant falls outside February.
            await events.PostAsync(new RentCharged(
                _thirdTenant, _property, _owner, null, new Money(100m), new DateOnly(2026, 3, 1), "rent"), ct);
        }, ct);
        Guid[] all = [_tenant, _otherTenant, _thirdTenant];

        (await QueryAsync(scope, new GetTenantsChargedInPeriod("RentCharged", null, 2026, 2, all), ct))
            .TenantIds.ShouldBe([_tenant], ignoreOrder: true);
        (await QueryAsync(scope, new GetTenantsChargedInPeriod("FeeCharged", null, 2026, 2, all), ct))
            .TenantIds.ShouldBe([_otherTenant, _thirdTenant], ignoreOrder: true);
        (await QueryAsync(scope, new GetTenantsChargedInPeriod("FeeCharged", "late", 2026, 2, all), ct))
            .TenantIds.ShouldBe([_otherTenant], ignoreOrder: true);
    }

    // ---- helpers ----

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    private Task<OrgScope> NewScope(CancellationToken ct) => ProvisionedScopeAsync(
        fixture, ct, owners: [_owner, _otherOwner], tenants: [_tenant, _otherTenant, _thirdTenant], properties: [_property]);

    private static (Func<ISender, Task>, string[], string?) Query<T>(IQuery<T> query, string property, string? message = null) =>
        (sender => sender.Query(query), [property], message);

    /// <summary>A context already disposed: a request that got past its validator would fail on its first read.</summary>
    private async Task<ISender> UnusableSenderAsync()
    {
        AppDbContext db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        return AccountingSender.Create(db, new OrgContext(), new ActorContext(), NoLeases);
    }

    private static async Task<T> QueryAsync<T>(OrgScope scope, IQuery<T> query, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases).Query(query, ct), ct);
        return result;
    }
}
