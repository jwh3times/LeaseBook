using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.SharedKernel;
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

    private PostEntryRequest Rent(string? sourceRef) => new(
        new DateOnly(2026, 2, 1), "RentCharged", null, "Feb rent", sourceRef,
        [
            new PostLineRequest(AccountCodes.TenantReceivable, new Money(1450m), null, EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId, TenantId: _tenantId),
            new PostLineRequest(AccountCodes.OwnerEquity, null, new Money(1450m), EntryBasis.Accrual,
                PropertyId: _propertyId, OwnerId: _ownerId),
        ]);
}
