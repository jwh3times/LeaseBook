using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-07 targeted invariants (§C.7): I1–I4 are clean on engine-produced data; the engine rejects what
/// would breach them; the checker is non-vacuous (catches injected bad data); and I5/I6 hold.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class InvariantTests(PostgresFixture fixture)
{
    private static readonly Guid Tenant = Guid.Parse("00000000-0000-0000-0000-0000000000f1");
    private static readonly Guid Owner = Guid.Parse("00000000-0000-0000-0000-0000000000e1");
    private static readonly Guid Property = Guid.Parse("00000000-0000-0000-0000-0000000000d1");

    [Fact]
    public async Task Core_invariants_are_clean_after_a_full_round_of_activity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(Tenant, Property, Owner, null, new Money(1450m), D(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(Tenant, Property, Owner, new Money(1450m), D(3), PaymentMethod.Ach, TrustBankId, "pay"), ct);
            await events.PostAsync(new DepositCollected(Tenant, Property, Owner, new Money(1000m), D(1), DepositBankId, "dep"), ct);
            await events.PostAsync(new ManagementFeeAssessed(Owner, Property, new Money(200m), D(27), TrustBankId, "fee"), ct);
            await events.PostAsync(new PMFeesSwept(new Money(200m), D(27), TrustBankId, OperatingBankId, "sweep"), ct);
            await events.PostAsync(new OwnerDisbursed(Owner, new Money(800m), D(28), TrustBankId, "draw"), ct);
        }, ct);

        var violations = await CheckCoreAsync(scope, ct);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
    }

    [Fact]
    public async Task Engine_rejects_an_entry_that_would_violate_I1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        await Should.ThrowAsync<UnbalancedEntryException>(() => scope.RunAsync(() =>
            Posting(scope).PostAsync(new PostEntryRequest(
                D(1), "RentCharged", null, null, null,
                [
                    new PostLineRequest(AccountCodes.TenantReceivable, new Money(100m), null, EntryBasis.Accrual, OwnerId: Owner),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(90m), EntryBasis.Accrual, OwnerId: Owner),
                ]), ct), ct));
    }

    [Fact]
    public async Task Engine_rejects_an_over_application_that_would_violate_I4()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        await scope.RunAsync(() => Events(scope).PostAsync(
            new DepositCollected(Tenant, Property, Owner, new Money(500m), D(1), DepositBankId, "dep"), ct), ct);

        await Should.ThrowAsync<InsufficientLiabilityException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(900m), D(28), DepositBankId, TrustBankId,
                DepositApplication.ToOwnerIncome, "over"), ct), ct));
    }

    [Fact]
    public async Task The_I1_checker_catches_an_injected_unbalanced_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        // Resolve a real account, then plant an unbalanced entry as the migrator (bypassing the engine,
        // which would never let this exist) — proving the checker is not vacuous (§A money-path note).
        Guid receivableAccountId = default;
        await scope.RunAsync(async () => receivableAccountId = await scope.Db.Set<Account>()
            .Where(a => a.Code == AccountCodes.TenantReceivable).Select(a => a.Id).SingleAsync(ct), ct);
        await InjectUnbalancedEntryAsync(scope.OrgId, receivableAccountId, ct);

        var violations = await Check(scope, c => new InvariantChecks(scope.Db).CheckEntriesBalanceAsync(c), ct);
        violations.ShouldContain(v => v.Invariant == "I1");
    }

    [Fact]
    public async Task A_void_and_its_reversal_net_to_zero_in_the_ledgers_I6()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        Guid entryId = default;
        await scope.RunAsync(async () =>
            entryId = await Events(scope).PostAsync(
                new RentCharged(Tenant, Property, Owner, null, new Money(1450m), D(1), "rent"), ct), ct);

        // Before the void the tenant owes 1450.
        var before = await TenantBalance(scope, ct);
        before.ShouldBe(1450m);

        await scope.RunAsync(() => Reversal(scope).ReverseAsync(entryId, "correction", D(2), ct), ct);

        // After the void the tenant ledger nets to zero and the core invariants stay clean.
        (await TenantBalance(scope, ct)).ShouldBe(0m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Cash_and_accrual_owner_totals_converge_after_settlement_I5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(Tenant, Property, Owner, null, new Money(1000m), D(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(Tenant, Property, Owner, new Money(1000m), D(3), PaymentMethod.Ach, TrustBankId, "pay"), ct);
        }, ct);

        decimal cash = 0;
        decimal accrual = 0;
        await scope.RunAsync(async () =>
        {
            cash = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(Owner, "cash"), ct)).Balance;
            accrual = (await new GetOwnerLedgerHandler(scope.Db).Handle(new GetOwnerLedger(Owner, "accrual"), ct)).Balance;
        }, ct);

        accrual.ShouldBe(1000m);
        cash.ShouldBe(accrual); // receivable settled → bases converge
    }

    private static Task<IReadOnlyList<InvariantViolation>> CheckCoreAsync(OrgScope scope, CancellationToken ct) =>
        Check(scope, c => new InvariantChecks(scope.Db).CheckCoreAsync(c), ct);

    private static async Task<IReadOnlyList<InvariantViolation>> Check(
        OrgScope scope, Func<CancellationToken, Task<IReadOnlyList<InvariantViolation>>> check, CancellationToken ct)
    {
        IReadOnlyList<InvariantViolation> result = [];
        await scope.RunAsync(async () => result = await check(ct), ct);
        return result;
    }

    private static async Task<decimal> TenantBalance(OrgScope scope, CancellationToken ct)
    {
        decimal balance = 0;
        await scope.RunAsync(async () =>
            balance = (await new GetTenantLedgerHandler(scope.Db).Handle(new GetTenantLedger(Tenant), ct)).Balance, ct);
        return balance;
    }

    private async Task InjectUnbalancedEntryAsync(Guid orgId, Guid accountId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        // FORCE ROW LEVEL SECURITY applies even to the migrator (the table owner), so the WITH CHECK
        // policy needs an org context — set it for this transaction.
        await using (var setOrg = new NpgsqlCommand("SELECT set_config('app.org_id', @org, true)", conn, tx))
        {
            setOrg.Parameters.AddWithValue("org", orgId.ToString());
            await setOrg.ExecuteNonQueryAsync(ct);
        }

        var entryId = UuidV7.NewId();
        await using (var entry = new NpgsqlCommand(
            "INSERT INTO journal_entries (id, org_id, entry_date, event_type, posted_at, created_at) " +
            "VALUES (@id, @org, DATE '2026-02-01', 'Injected', now(), now())", conn, tx))
        {
            entry.Parameters.AddWithValue("id", entryId);
            entry.Parameters.AddWithValue("org", orgId);
            await entry.ExecuteNonQueryAsync(ct);
        }

        await using (var line = new NpgsqlCommand(
            "INSERT INTO journal_lines (id, org_id, entry_id, account_id, account_class, debit, basis, created_at) " +
            "VALUES (@id, @org, @entry, @account, 'tenant_receivable', 100.00, 'accrual', now())", conn, tx))
        {
            line.Parameters.AddWithValue("id", UuidV7.NewId());
            line.Parameters.AddWithValue("org", orgId);
            line.Parameters.AddWithValue("entry", entryId);
            line.Parameters.AddWithValue("account", accountId);
            await line.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    private static DateOnly D(int day) => new(2026, 2, day);
}
