using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Ledgers;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Web.Persistence;
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
    // Fresh per test instance (ADR-013: composite dim FK forbids reusing a fixed id across orgs).
    private readonly Guid Tenant = UuidV7.NewId();
    private readonly Guid Owner = UuidV7.NewId();
    private readonly Guid Property = UuidV7.NewId();

    [Fact]
    public async Task Core_invariants_are_clean_after_a_full_round_of_activity()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(Tenant, Property, Owner, null, new Money(1450m), D(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(Tenant, Property, Owner, new Money(1450m), D(3), PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
            await events.PostAsync(new DepositCollected(Tenant, Property, Owner, new Money(1000m), D(1), scope.DepositBankId, "dep"), ct);
            await events.PostAsync(new ManagementFeeAssessed(Owner, Property, new Money(200m), D(27), scope.TrustBankId, "fee"), ct);
            await events.PostAsync(new PMFeesSwept(new Money(200m), D(27), scope.TrustBankId, scope.OperatingBankId, "sweep"), ct);
            await events.PostAsync(new OwnerDisbursed(Owner, new Money(800m), D(28), scope.TrustBankId, "draw"), ct);
        }, ct);

        var violations = await CheckCoreAsync(scope, ct);
        violations.ShouldBeEmpty(string.Join("; ", violations.Select(v => $"{v.Invariant}:{v.Detail}")));
    }

    [Fact]
    public async Task Engine_rejects_an_entry_that_would_violate_I1()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

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
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);
        await scope.RunAsync(() => Events(scope).PostAsync(
            new DepositCollected(Tenant, Property, Owner, new Money(500m), D(1), scope.DepositBankId, "dep"), ct), ct);

        await Should.ThrowAsync<InsufficientLiabilityException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(900m), D(28), scope.DepositBankId, scope.TrustBankId,
                DepositApplication.ToOwnerIncome, "over"), ct), ct));
    }

    [Fact]
    public async Task The_I1_checker_catches_an_injected_unbalanced_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

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
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

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
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new RentCharged(Tenant, Property, Owner, null, new Money(1000m), D(1), "rent"), ct);
            await events.PostAsync(new PaymentReceived(Tenant, Property, Owner, new Money(1000m), D(3), PaymentMethod.Ach, scope.TrustBankId, "pay"), ct);
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

    [Fact]
    public async Task Applying_a_deposit_releases_the_owner_attributed_liability_I7()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new DepositCollected(
                Tenant, Property, Owner, new Money(1000m), D(1), scope.DepositBankId, "dep"), ct);
            await events.PostAsync(new DepositApplied(
                Tenant, Property, Owner, new Money(1000m), D(28), scope.DepositBankId, scope.TrustBankId,
                DepositApplication.ToOwnerIncome, "damages"), ct);
        }, ct);

        // The collection credited the liability with an owner dim; the application must debit it with the
        // same dim, or the owner's deposit column never comes back down (WP-13 finding §12.11).
        (await OwnerDeposits(scope, ct)).ShouldBe(0m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Refunding_a_deposit_releases_the_owner_attributed_liability_I7()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new DepositCollected(
                Tenant, Property, Owner, new Money(1000m), D(1), scope.DepositBankId, "dep"), ct);
            await events.PostAsync(new RefundIssued(
                Tenant, new Money(1000m), D(28), scope.DepositBankId, RefundSource.Deposits, "move-out refund",
                PropertyId: Property, OwnerId: Owner), ct);
        }, ct);

        (await OwnerDeposits(scope, ct)).ShouldBe(0m);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_prepayment_refund_stays_owner_undimensioned_I7()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        Guid refundId = default;
        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new PrepaymentReceived(
                Tenant, Property, Owner, new Money(400m), D(1), scope.TrustBankId, "prepay"), ct);
            refundId = await events.PostAsync(new RefundIssued(
                Tenant, new Money(400m), D(28), scope.TrustBankId, RefundSource.Prepayments, "prepay refund"), ct);
        }, ct);

        // Prepayments are collected with no owner dim (GetOwnerBalances deliberately drops them), so the
        // refund must not invent one — symmetry, not blanket attribution.
        var liability = (await ReadLinesAsync(scope, refundId, ct))
            .Single(l => l.Code == AccountCodes.TenantPrepayments);
        liability.OwnerId.ShouldBeNull();
        liability.TenantId.ShouldBe(Tenant);
        (await CheckCoreAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task The_I7_checker_catches_a_disposition_that_drops_the_owner_dimension()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            var events = Events(scope);
            await events.PostAsync(new DepositCollected(
                Tenant, Property, Owner, new Money(1000m), D(1), scope.DepositBankId, "dep"), ct);

            // The public refund path now rejects a mismatched exact bucket before posting. Write the
            // historical pre-ADR-026 shape through the lower-level posting seam so this test still
            // proves the independent I7 sweep catches already-persisted bad attribution: the tenant
            // nets to zero (I4 stays clean), while the owner bucket holds +1000 and the unattributed
            // bucket goes to -1000.
            await Posting(scope).PostAsync(new PostEntryRequest(
                D(28), "RefundIssued", null, "Historical malformed refund", "bad-refund",
                [
                    new PostLineRequest(
                        AccountCodes.SecurityDepositsHeld, new Money(1000m), null, EntryBasis.Both,
                        TenantId: Tenant, BankAccountId: scope.DepositBankId),
                    new PostLineRequest(
                        AccountCodes.TrustBank(scope.DepositBankId), null, new Money(1000m), EntryBasis.Both,
                        BankAccountId: scope.DepositBankId),
                ]), ct);
        }, ct);

        (await Check(scope, c => new InvariantChecks(scope.Db).CheckDepositLiabilitiesNonNegativeAsync(c), ct))
            .ShouldBeEmpty();
        var violations = await Check(
            scope, c => new InvariantChecks(scope.Db).CheckDepositAttributionSymmetricAsync(c), ct);
        violations.ShouldContain(v => v.Invariant == "I7");
    }

    [Fact]
    public async Task The_I10_sweep_catches_a_prepayment_applied_against_a_bank_that_never_held_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant], properties: [Property]);

        await scope.RunAsync(async () =>
        {
            await Events(scope).PostAsync(new PrepaymentReceived(
                Tenant, Property, Owner, new Money(400m), D(1), scope.DepositBankId, "prepay"), ct);

            // The pre-#475 application shape: it debited whichever bank the caller named. Written through
            // the lower-level posting seam, because the public path now refuses it. Every entry balances,
            // each bank's trust equation holds, and the tenant nets to zero (I4 clean) — only the per-bank
            // attribution is wrong: the deposit bank still shows +400 held, the trust bank −400.
            await Posting(scope).PostAsync(new PostEntryRequest(
                D(28), "PrepaymentApplied", null, "Historical cross-bank application", "bad-apply",
                [
                    new PostLineRequest(AccountCodes.TenantPrepayments, new Money(400m), null, EntryBasis.Both,
                        TenantId: Tenant, BankAccountId: scope.TrustBankId),
                    new PostLineRequest(AccountCodes.TenantReceivable, null, new Money(400m), EntryBasis.Accrual,
                        PropertyId: Property, OwnerId: Owner, TenantId: Tenant),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(400m), EntryBasis.Cash,
                        PropertyId: Property, OwnerId: Owner, BankAccountId: scope.TrustBankId),
                ]), ct);
        }, ct);

        var violations = await CheckCoreAsync(scope, ct);
        violations.ShouldHaveSingleItem().Invariant.ShouldBe("I10");
    }

    /// <summary>
    /// The sweep reports every breach it exists to find, each under its own id and with its own detail,
    /// in the order the sweep runs its checks. Each violation is planted in isolation, so it trips its
    /// own check and nothing else's: the trust bank's equation is off only by the one line meant to put
    /// it off, and every entry but one balances.
    /// <para>
    /// No public path can write any of these rows, and one of them (a PM-income line carrying an owner)
    /// the schema itself refuses. So they are written as the migrator, in one transaction that also
    /// lifts that one CHECK constraint, and the sweep reads them inside that transaction before it rolls
    /// back. Nothing is committed: no other session ever sees the constraint gone or the rows written.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_sweep_reports_each_planted_breach_under_its_own_id_with_its_detail()
    {
        var ct = TestContext.Current.CancellationToken;
        Guid[] tenants = [UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId(), UuidV7.NewId()];
        await using var scope = await ProvisionedScopeAsync(
            fixture, ct, owners: [Owner], tenants: [Tenant, .. tenants], properties: [Property]);
        var (t2, t3, t4, t5, t6) = (tenants[0], tenants[1], tenants[2], tenants[3], tenants[4]);
        var trust = AccountCodes.TrustBank(scope.TrustBankId);
        var deposit = AccountCodes.TrustBank(scope.DepositBankId);
        var operating = AccountCodes.PmOperatingBank(scope.OperatingBankId);

        await using var plant = await PlantedJournal.BeginAsync(fixture, scope.OrgId, ct);
        await plant.LiftPmIncomeOwnerCheckAsync(ct);

        // I1: debits 100 in accrual against nothing; credits 60 in cash against nothing.
        var (unbalanced, _) = await plant.EntryAsync("Injected", ct,
            new(AccountCodes.TenantReceivable, 100m, null, "accrual", Tenant: Tenant),
            new(AccountCodes.TenantReceivable, null, 60m, "cash", Tenant: Tenant));

        // I2: the trust bank holds 250 that no owner, tenant or fee accounts for.
        await plant.EntryAsync("Injected", ct,
            new(trust, 250m, null, "both", Bank: scope.TrustBankId),
            new(AccountCodes.TenantReceivable, null, 250m, "both", Tenant: Tenant));

        // I3: PM income attributed to an owner, in the operating bank so the trust equation is untouched.
        var (_, pmLines) = await plant.EntryAsync("Injected", ct,
            new(operating, 40m, null, "both", Bank: scope.OperatingBankId),
            new(AccountCodes.PmIncome, null, 40m, "both", Owner: Owner, Bank: scope.OperatingBankId));

        // I4 and I7: more deposit released for t2 than was ever held, with no owner on it.
        await plant.EntryAsync("Injected", ct,
            new(AccountCodes.SecurityDepositsHeld, 30m, null, "both", Tenant: t2, Bank: scope.DepositBankId),
            new(deposit, null, 30m, "both", Bank: scope.DepositBankId));

        // I7 alone: t3 holds nothing in total, but the owner's bucket is released into the unattributed one.
        await plant.EntryAsync("Injected", ct,
            new(AccountCodes.SecurityDepositsHeld, 20m, null, "both", Tenant: t3, Owner: Owner, Bank: scope.DepositBankId),
            new(AccountCodes.SecurityDepositsHeld, null, 20m, "both", Tenant: t3, Bank: scope.DepositBankId));

        // I4 and I10: prepaid credit released from the trust bank for t4, which never held any.
        await plant.EntryAsync("Injected", ct,
            new(AccountCodes.TenantPrepayments, 15m, null, "both", Tenant: t4, Bank: scope.TrustBankId),
            new(trust, null, 15m, "both", Bank: scope.TrustBankId));

        // I4 and I10: prepaid credit released for t5 from no bank at all.
        await plant.EntryAsync("Injected", ct,
            new(AccountCodes.TenantPrepayments, 10m, null, "both", Tenant: t5),
            new(AccountCodes.TenantReceivable, null, 10m, "both", Tenant: t5));

        // I9: migration clearing left at 5.00 in cash and 7.00 in accrual.
        await plant.EntryAsync("OpeningBalance", ct,
            new(AccountCodes.MigrationClearing, 5m, null, "cash"),
            new(AccountCodes.TenantReceivable, null, 5m, "cash", Tenant: t6));
        await plant.EntryAsync("OpeningBalance", ct,
            new(AccountCodes.MigrationClearing, 7m, null, "accrual"),
            new(AccountCodes.TenantReceivable, null, 7m, "accrual", Tenant: t6));

        // I8: an owner credit under an event type no statement section covers, balanced in the trust bank.
        var (uncovered, _) = await plant.EntryAsync("UnmappedOwnerCredit", ct,
            new(trust, 12m, null, "both", Bank: scope.TrustBankId),
            new(AccountCodes.OwnerEquity, null, 12m, "both", Owner: Owner, Bank: scope.TrustBankId));

        var violations = await plant.SweepAsync(ct);

        violations.Select(v => v.Invariant).ShouldBe(
            ["I1", "I1", "I2", "I3", "I4", "I4", "I4", "I7", "I7", "I10", "I10", "I9", "I9", "I8"]);
        violations.Select(v => $"{v.Invariant} {v.Detail}").ShouldBe(
            [
                $"I1 entry {unbalanced} does not balance in cash: debits 0.00 != credits 60.00",
                $"I1 entry {unbalanced} does not balance in accrual: debits 100.00 != credits 0.00",
                $"I2 trust equation off by 250.00 on bank {scope.TrustBankId}",
                $"I3 pm_income line {pmLines[1]} carries an owner_id",
                $"I4 held security_deposits_held for tenant {t2} is negative (-30.00)",
                $"I4 held tenant_prepayments for tenant {t4} is negative (-15.00)",
                $"I4 held tenant_prepayments for tenant {t5} is negative (-10.00)",
                $"I7 held security deposit for tenant {t2} under owner (unattributed) is negative (-30.00) "
                    + "— a disposition released a different owner bucket than the collection credited",
                $"I7 held security deposit for tenant {t3} under owner {Owner} is negative (-20.00) "
                    + "— a disposition released a different owner bucket than the collection credited",
                $"I10 held prepayment for tenant {t4} in bank {scope.TrustBankId} is negative (-15.00) "
                    + "— more prepaid credit was released from this bank than it ever held",
                $"I10 held prepayment for tenant {t5} in bank (none) is negative (-10.00) "
                    + "— more prepaid credit was released from this bank than it ever held",
                "I9 migration_clearing does not net to $0 in cash: residual 5.00",
                "I9 migration_clearing does not net to $0 in accrual: residual 7.00",
                "I8 event_type 'UnmappedOwnerCredit' posts owner-attributed owner_equity lines but has no "
                    + $"statement section — 1 line(s), e.g. entry {uncovered}; every owner statement covering "
                    + "one of them fails to render",
            ],
            ignoreOrder: true);
    }

    /// <summary>
    /// Journal rows written as the migrator into one organization, in a transaction that is only ever
    /// rolled back, with an <see cref="AppDbContext"/> on the same transaction for the sweep to read
    /// them through.
    /// </summary>
    private sealed class PlantedJournal : IAsyncDisposable
    {
        private readonly NpgsqlConnection _conn;
        private readonly NpgsqlTransaction _tx;
        private readonly Guid _orgId;
        private readonly Dictionary<string, (Guid Id, string Class)> _accounts;

        private PlantedJournal(
            NpgsqlConnection conn, NpgsqlTransaction tx, Guid orgId, Dictionary<string, (Guid, string)> accounts)
        {
            _conn = conn;
            _tx = tx;
            _orgId = orgId;
            _accounts = accounts;
        }

        public sealed record Line(
            string Code, decimal? Debit, decimal? Credit, string Basis,
            Guid? Tenant = null, Guid? Owner = null, Guid? Bank = null);

        public static async Task<PlantedJournal> BeginAsync(PostgresFixture fixture, Guid orgId, CancellationToken ct)
        {
            var conn = new NpgsqlConnection(fixture.MigratorConnectionString);
            await conn.OpenAsync(ct);
            var tx = await conn.BeginTransactionAsync(ct);

            // FORCE ROW LEVEL SECURITY binds the migrator too, so every read and write here is this org's.
            await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

            var accounts = new Dictionary<string, (Guid, string)>(StringComparer.Ordinal);
            await using (var read = new NpgsqlCommand("SELECT code, id, class FROM accounts", conn, tx))
            await using (var reader = await read.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    accounts[reader.GetString(0)] = (reader.GetGuid(1), reader.GetString(2));
                }
            }

            return new PlantedJournal(conn, tx, orgId, accounts);
        }

        /// <summary>Drops the PM-income/owner CHECK for this transaction only; the rollback restores it.</summary>
        public async Task LiftPmIncomeOwnerCheckAsync(CancellationToken ct)
        {
            await using var lift = new NpgsqlCommand(
                "ALTER TABLE journal_lines DROP CONSTRAINT ck_journal_lines_pm_income_no_owner", _conn, _tx);
            await lift.ExecuteNonQueryAsync(ct);
        }

        public async Task<(Guid EntryId, Guid[] LineIds)> EntryAsync(string eventType, CancellationToken ct, params Line[] lines)
        {
            var entryId = UuidV7.NewId();
            await using (var entry = new NpgsqlCommand(
                "INSERT INTO journal_entries (id, org_id, entry_date, event_type, posted_at, created_at) " +
                "VALUES (@id, @org, DATE '2026-02-01', @type, now(), now())", _conn, _tx))
            {
                entry.Parameters.AddWithValue("id", entryId);
                entry.Parameters.AddWithValue("org", _orgId);
                entry.Parameters.AddWithValue("type", eventType);
                await entry.ExecuteNonQueryAsync(ct);
            }

            var lineIds = new Guid[lines.Length];
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var (accountId, accountClass) = _accounts[line.Code];
                lineIds[i] = UuidV7.NewId();
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO journal_lines (id, org_id, entry_id, account_id, account_class, debit, credit, basis, " +
                    "tenant_id, owner_id, bank_account_id, created_at) VALUES (@id, @org, @entry, @account, @class, " +
                    "@debit, @credit, @basis, @tenant, @owner, @bank, now())", _conn, _tx);
                insert.Parameters.AddWithValue("id", lineIds[i]);
                insert.Parameters.AddWithValue("org", _orgId);
                insert.Parameters.AddWithValue("entry", entryId);
                insert.Parameters.AddWithValue("account", accountId);
                insert.Parameters.AddWithValue("class", accountClass);
                insert.Parameters.Add(new NpgsqlParameter<decimal?>("debit", line.Debit));
                insert.Parameters.Add(new NpgsqlParameter<decimal?>("credit", line.Credit));
                insert.Parameters.AddWithValue("basis", line.Basis);
                insert.Parameters.Add(new NpgsqlParameter<Guid?>("tenant", line.Tenant));
                insert.Parameters.Add(new NpgsqlParameter<Guid?>("owner", line.Owner));
                insert.Parameters.Add(new NpgsqlParameter<Guid?>("bank", line.Bank));
                await insert.ExecuteNonQueryAsync(ct);
            }

            return (entryId, lineIds);
        }

        /// <summary>The whole sweep, read through a context on this transaction.</summary>
        public async Task<IReadOnlyList<InvariantViolation>> SweepAsync(CancellationToken ct)
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(_conn, npgsql => npgsql.SetPostgresVersion(18, 0))
                .UseSnakeCaseNamingConvention()
                .Options;
            await using var db = new AppDbContext(options);
            await db.Database.UseTransactionAsync(_tx, ct);
            return await new InvariantChecks(db).CheckCoreAsync(ct);
        }

        public async ValueTask DisposeAsync()
        {
            await _tx.RollbackAsync();
            await _tx.DisposeAsync();
            await _conn.DisposeAsync();
        }
    }

    private static async Task<decimal> OwnerDeposits(OrgScope scope, CancellationToken ct)
    {
        decimal deposits = 0;
        await scope.RunAsync(async () => deposits =
            (await new GetOwnerBalancesHandler(scope.Db).Handle(new GetOwnerBalances(), ct)).Totals.Deposits, ct);
        return deposits;
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

    private async Task<decimal> TenantBalance(OrgScope scope, CancellationToken ct)
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
        // policy needs an organization context — set it for this transaction.
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

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
