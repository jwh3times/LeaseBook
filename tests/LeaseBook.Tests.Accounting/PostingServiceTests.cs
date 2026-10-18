using System.Diagnostics;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Accounting.Posting;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-04: the posting service is the single write path, and every rejection is a typed domain error.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PostingServiceTests(PostgresFixture fixture)
{
    private readonly Guid _ownerId = UuidV7.NewId();
    private readonly Guid _propertyId = UuidV7.NewId();
    private readonly Guid _tenantId = UuidV7.NewId();

    [Fact]
    public void An_entry_refuses_a_missing_line()
    {
        var entry = JournalEntry.Create(
            new DateOnly(2026, 2, 1), "RentCharged", null, null, null, null,
            Actor.System("test-harness"), new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        Should.Throw<ArgumentNullException>(() => entry.AddLine(null!)).ParamName.ShouldBe("line");
        entry.Lines.ShouldBeEmpty();
    }

    [Fact]
    public async Task Posting_a_balanced_entry_persists_lines_with_denormalized_class_and_audits_them()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        Guid entryId = default;
        await scope.RunAsync(async () =>
            entryId = await AccountingTestHarness.Posting(scope).PostAsync(Rent("rent-1"), ct), ct);

        entryId.ShouldNotBe(Guid.Empty);

        JournalEntry? entry = null;
        long journalAudits = 0;
        var allActorsNull = false;
        await scope.RunAsync(async () =>
        {
            entry = await scope.Db.Set<JournalEntry>().AsNoTracking()
                .Include(e => e.Lines).SingleAsync(e => e.Id == entryId, ct);
            var journal = scope.Db.AuditEvents
                .Where(a => a.EntityType == "journal_entries" || a.EntityType == "journal_lines");
            journalAudits = await journal.CountAsync(ct);
            allActorsNull = await journal.AllAsync(a => a.ActorUserId == null, ct);
        }, ct);

        // account_class is denormalized from the resolved account, never the caller.
        entry!.Lines.Single(l => l.Debit is not null).AccountClass.ShouldBe(AccountClass.TenantReceivable);
        entry.Lines.Single(l => l.Credit is not null).AccountClass.ShouldBe(AccountClass.OwnerEquity);

        // retro item 1: posting is the first real producer of audit rows — 1 entry + 2 lines, actor null.
        journalAudits.ShouldBe(3);
        allActorsNull.ShouldBeTrue();
    }

    [Fact]
    public async Task Entry_balanced_in_cash_but_off_in_accrual_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        var ex = await Should.ThrowAsync<UnbalancedEntryException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "RentCharged", null, null, null,
                [
                    new PostLineRequest(AccountCodes.TenantReceivable, new Money(100m), null, EntryBasis.Accrual, OwnerId: _ownerId),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(90m), EntryBasis.Accrual, OwnerId: _ownerId),
                ]), ct), ct));

        ex.Code.ShouldBe("unbalanced_entry");
        // The basis named is the one that is off, with that basis's own sums: cash holds no line here.
        (ex.Basis, ex.Debits, ex.Credits).ShouldBe(("Accrual", 100m, 90m));
    }

    [Fact]
    public async Task Posting_into_a_closed_period_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        await scope.RunAsync(() => AccountingTestHarness.Periods(scope).CloseAsync(2026, 2, ct), ct);

        var ex = await Should.ThrowAsync<PeriodClosedException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(Rent("rent-closed"), ct), ct));

        ex.Code.ShouldBe("period_closed");
        ex.Month.ShouldBe(2);
    }

    [Fact]
    public async Task Duplicate_source_ref_is_rejected_with_the_existing_entry_id()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        Guid firstId = default;
        await scope.RunAsync(async () =>
            firstId = await AccountingTestHarness.Posting(scope).PostAsync(Rent("dup-1"), ct), ct);

        var ex = await Should.ThrowAsync<DuplicateSourceRefException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(Rent("dup-1"), ct), ct));

        ex.Code.ShouldBe("duplicate_source_ref");
        ex.ExistingEntryId.ShouldBe(firstId);
    }

    [Fact]
    public async Task Unknown_account_code_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        var ex = await Should.ThrowAsync<UnknownAccountException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "RentCharged", null, null, null,
                [
                    new PostLineRequest("does_not_exist", new Money(10m), null, EntryBasis.Both),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(10m), EntryBasis.Both, OwnerId: _ownerId),
                ]), ct), ct));

        ex.AccountCode.ShouldBe("does_not_exist");
    }

    [Fact]
    public async Task Pm_income_line_carrying_an_owner_dim_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        var ex = await Should.ThrowAsync<PmIncomeOwnerDimException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "ManagementFeeAssessed", null, null, null,
                [
                    new PostLineRequest(AccountCodes.OwnerEquity, new Money(50m), null, EntryBasis.Both, OwnerId: _ownerId),
                    new PostLineRequest(AccountCodes.PmIncome, null, new Money(50m), EntryBasis.Both, OwnerId: _ownerId),
                ]), ct), ct));

        ex.Code.ShouldBe("pm_income_owner_dim");
    }

    [Fact]
    public async Task A_line_with_neither_debit_nor_credit_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        await Should.ThrowAsync<InvalidLineException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "RentCharged", null, null, null,
                [
                    new PostLineRequest(AccountCodes.OwnerEquity, null, null, EntryBasis.Both, OwnerId: _ownerId),
                ]), ct), ct));
    }

    [Fact]
    public async Task App_role_cannot_mutate_a_posted_entry()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);
        await scope.RunAsync(() => AccountingTestHarness.Posting(scope).PostAsync(Rent("ap-1"), ct), ct);

        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        var ex = await Should.ThrowAsync<PostgresException>(async () =>
        {
            await using var cmd = new NpgsqlCommand("UPDATE journal_entries SET description = 'tamper'", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        });
        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Posting_without_a_request_is_refused_by_name()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        var ex = await Should.ThrowAsync<ArgumentNullException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(null!, ct), ct));

        ex.ParamName.ShouldBe("request");
    }

    [Fact]
    public async Task Posting_outside_an_organization_context_is_refused_before_it_reads_anything()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        // Outside a unit of work the context carries no organization, as a background job that forgot to set one.
        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            AccountingTestHarness.Posting(scope).PostAsync(Rent("no-org"), ct));

        ex.Message.ShouldBe(
            "PostingService requires an ambient organization context (request middleware or OrgScopedExecutor).");
    }

    [Fact]
    public async Task Posting_with_no_declared_actor_is_refused_rather_than_stamped_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        // The organization is set but the service reads an actor context nobody wrote (ADR-039).
        var unattributed = new PostingService(
            scope.Db, scope.Tenant, AccountingTestHarness.Periods(scope), new ActorContext(),
            new ReconciliationLock(scope.Db));
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(() =>
            unattributed.PostAsync(Rent("no-actor"), ct), ct));

        ex.Message.ShouldBe(
            "PostingService requires a declared actor — Actor.User(id) for a signed-in user, " +
            "Actor.System(process) for a named process. The journal records who posted every entry " +
            "(ADR-039), so posting with no actor is refused rather than stamped null.");
        (await CountEntriesAsync(scope, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task An_entry_with_no_lines_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);

        var ex = await Should.ThrowAsync<InvalidLineException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "RentCharged", null, "nothing", null, []), ct), ct));

        ex.Reason.ShouldBe(InvalidLineReason.NoLines);
        (await CountEntriesAsync(scope, ct)).ShouldBe(0);
    }

    [Fact]
    public async Task A_database_refusal_that_is_no_duplicate_is_rethrown_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);
        await scope.RunAsync(() => AccountingTestHarness.Posting(scope).PostAsync(Rent(null), ct), ct);

        // A tenant this organization's directory has never held: the composite dimension key refuses the line
        // at SaveChanges, and that is no source-ref, reversal or assessment collision to map.
        var stranger = UuidV7.NewId();
        await Should.ThrowAsync<DbUpdateException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 1), "RentCharged", null, "unknown tenant", null,
                [
                    new PostLineRequest(AccountCodes.TenantReceivable, new Money(10m), null, EntryBasis.Accrual,
                        OwnerId: _ownerId, TenantId: stranger),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(10m), EntryBasis.Accrual, OwnerId: _ownerId),
                ]), ct), ct));

        (await CountEntriesAsync(scope, ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_reversal_the_database_refuses_for_another_reason_is_not_reported_as_already_reversed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);
        Guid original = default;
        await scope.RunAsync(async () => original = await AccountingTestHarness.Posting(scope).PostAsync(Rent("rent-1"), ct), ct);

        // Nothing has reversed the entry: the refusal is a tenant this organization has never held, and the
        // backstop must re-throw it rather than read it as a lost reversal race.
        var stranger = UuidV7.NewId();
        await Should.ThrowAsync<DbUpdateException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 10), EventTypes.EntryVoided, null, "Void — Feb rent", null,
                [
                    new PostLineRequest(AccountCodes.OwnerEquity, new Money(1450m), null, EntryBasis.Accrual,
                        PropertyId: _propertyId, OwnerId: _ownerId),
                    new PostLineRequest(AccountCodes.TenantReceivable, null, new Money(1450m), EntryBasis.Accrual,
                        PropertyId: _propertyId, OwnerId: _ownerId, TenantId: stranger),
                ],
                ReversesEntryId: original), ct), ct));
    }

    [Fact]
    public async Task Posting_records_an_entry_posted_event_on_the_current_trace()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);

        using var trace = new Activity("posting-test").Start();
        Guid entryId = default;
        await scope.RunAsync(async () => entryId = await AccountingTestHarness.Posting(scope)
            .PostAsync(Rent("traced") with { EventSubtype = "monthly" }, ct), ct);

        var posted = trace.Events.ShouldHaveSingleItem();
        posted.Name.ShouldBe("accounting.entry_posted");
        posted.Tags.ToDictionary(t => t.Key, t => t.Value).ShouldBe(new Dictionary<string, object?>
        {
            ["event_type"] = "RentCharged",
            ["event_subtype"] = "monthly",
            ["basis_line_count"] = 2,
            ["entry_id"] = entryId,
        });
    }

    [Fact]
    public async Task Lines_that_only_carry_a_locked_bank_as_attribution_still_post()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);
        await FinalizeEmptyMonthAsync(scope, scope.TrustBankId, ct);

        // A management fee moves no cash: both its lines name the trust bank only as attribution, so a
        // reconciled February on that bank does not bar it (P63).
        Guid entryId = default;
        await scope.RunAsync(async () => entryId = await AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
            new DateOnly(2026, 2, 27), "ManagementFeeAssessed", null, "fee", null,
            [
                new PostLineRequest(AccountCodes.OwnerEquity, new Money(50m), null, EntryBasis.Both,
                    PropertyId: _propertyId, OwnerId: _ownerId, BankAccountId: scope.TrustBankId),
                new PostLineRequest(AccountCodes.PmIncome, null, new Money(50m), EntryBasis.Both,
                    PropertyId: _propertyId, BankAccountId: scope.TrustBankId),
            ]), ct), ct);

        entryId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task A_trust_bank_line_into_a_reconciled_month_is_rejected_with_no_attribution_beside_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(
            fixture, ct, owners: [_ownerId], tenants: [_tenantId], properties: [_propertyId]);
        await FinalizeEmptyMonthAsync(scope, scope.TrustBankId, ct);

        var ex = await Should.ThrowAsync<AccountPeriodLockedException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 27), "OwnerContribution", null, "late deposit", null,
                [
                    new PostLineRequest(AccountCodes.TrustBank(scope.TrustBankId), new Money(50m), null, EntryBasis.Both,
                        BankAccountId: scope.TrustBankId),
                    new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(50m), EntryBasis.Both, OwnerId: _ownerId),
                ]), ct), ct));

        (ex.BankAccountId, ex.Year, ex.Month).ShouldBe((scope.TrustBankId, 2026, 2));
    }

    [Fact]
    public async Task A_pm_operating_bank_line_into_a_reconciled_month_is_rejected_with_no_attribution_beside_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await AccountingTestHarness.ProvisionedScopeAsync(fixture, ct);
        await FinalizeEmptyMonthAsync(scope, scope.OperatingBankId, ct);

        var ex = await Should.ThrowAsync<AccountPeriodLockedException>(() => scope.RunAsync(() =>
            AccountingTestHarness.Posting(scope).PostAsync(new PostEntryRequest(
                new DateOnly(2026, 2, 27), "BankFeeCharged", null, "late fee", null,
                [
                    new PostLineRequest(AccountCodes.PmIncome, new Money(5m), null, EntryBasis.Both),
                    new PostLineRequest(AccountCodes.PmOperatingBank(scope.OperatingBankId), null, new Money(5m), EntryBasis.Both,
                        BankAccountId: scope.OperatingBankId),
                ]), ct), ct));

        (ex.BankAccountId, ex.Year, ex.Month).ShouldBe((scope.OperatingBankId, 2026, 2));
    }

    // Finalizes February on one bank with nothing posted to it: a zero statement against a zero book.
    private static async Task FinalizeEmptyMonthAsync(OrgScope scope, Guid bankId, CancellationToken ct)
    {
        await scope.RunAsync(async () =>
        {
            var started = await new StartReconciliationHandler(scope.Db)
                .Handle(new StartReconciliation(bankId, 2026, 2, 0m), ct);
            await new FinalizeReconciliationHandler(scope.Db, scope.Actor)
                .Handle(new FinalizeReconciliation(started.Id), ct);
        }, ct);
    }

    private static async Task<int> CountEntriesAsync(OrgScope scope, CancellationToken ct)
    {
        var count = 0;
        await scope.RunAsync(async () => count = await scope.Db.Set<JournalEntry>().CountAsync(ct), ct);
        return count;
    }

    private PostEntryRequest Rent(string? sourceRef) => new(
        new DateOnly(2026, 2, 1), "RentCharged", null, "Feb rent", sourceRef,
        [
            new PostLineRequest(AccountCodes.TenantReceivable, new Money(1450m), null, EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId, TenantId: _tenantId),
            new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(1450m), EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId),
        ]);
}
