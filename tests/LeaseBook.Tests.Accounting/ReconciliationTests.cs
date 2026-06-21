using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Accounting.Support.AccountingTestHarness;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-04 (M4 / ADR-014): the reconciliation engine — finalize requires a zero difference and then locks
/// the (account, month) against further bank postings; the report is immutable; unlock (admin) releases
/// the lock.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReconciliationTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();

    [Fact]
    public async Task Finalize_requires_zero_difference_then_locks_until_unlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        // A 1000 deposit into the trust bank in February.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new OwnerContribution(_owner, _property, new Money(1000m), Feb(1), scope.TrustBankId, "seed"), ct), ct);
        var depositLine = await TrustLineAsync(scope, ct);

        // Statement says 1000, but nothing is cleared yet → difference 1000.
        var view = await StartAsync(scope, scope.TrustBankId, 2026, 2, 1000m, ct);
        view.Difference.ShouldBe(1000m);

        // Finalizing with a non-zero difference is rejected.
        await Should.ThrowAsync<ReconciliationUnbalancedException>(() => FinalizeAsync(scope, view.Id, ct));

        // Clear the deposit → difference 0 → finalize succeeds and the line becomes reconciled.
        await ClearAsync(scope, [depositLine], ct);
        var finalized = await FinalizeAsync(scope, view.Id, ct);
        finalized.Status.ShouldBe("finalized");
        finalized.Difference.ShouldBe(0m);

        // A new bank line into the locked February is rejected…
        await Should.ThrowAsync<AccountPeriodLockedException>(() => scope.RunAsync(() =>
            Events(scope).PostAsync(new InterestEarned(new Money(5m), Feb(20), scope.TrustBankId, "feb interest"), ct), ct));

        // …but March (different month) and February-on-another-account both post.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new InterestEarned(new Money(5m), new DateOnly(2026, 3, 1), scope.TrustBankId, "mar interest"), ct), ct);
        await scope.RunAsync(() => Events(scope).PostAsync(
            new InterestEarned(new Money(5m), Feb(20), scope.OperatingBankId, "feb interest (operating)"), ct), ct);

        // Unlock (admin path here is just the handler) releases the lock — the February post now succeeds.
        await UnlockAsync(scope, view.Id, "correcting a miskeyed amount", ct);
        await scope.RunAsync(() => Events(scope).PostAsync(
            new InterestEarned(new Money(5m), Feb(21), scope.TrustBankId, "feb interest 2"), ct), ct);
    }

    [Fact]
    public async Task The_finalized_report_is_stored_and_immutable()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        await scope.RunAsync(() => Events(scope).PostAsync(
            new OwnerContribution(_owner, _property, new Money(750m), Feb(1), scope.TrustBankId, "seed"), ct), ct);
        var line = await TrustLineAsync(scope, ct);

        var view = await StartAsync(scope, scope.TrustBankId, 2026, 2, 750m, ct);
        await ClearAsync(scope, [line], ct);
        await FinalizeAsync(scope, view.Id, ct);

        var before = await ReportAsync(scope, view.Id, ct);
        before!.ReportJson.ShouldNotBeNull();
        using (var doc = System.Text.Json.JsonDocument.Parse(before.ReportJson!))
        {
            doc.RootElement.GetProperty("reconciledItemCount").GetInt32().ShouldBe(1);
            doc.RootElement.GetProperty("clearedBalance").GetDecimal().ShouldBe(750m);
            doc.RootElement.GetProperty("difference").GetDecimal().ShouldBe(0m);
        }

        // Later activity (in March, the open month) must not change the finalized report.
        await scope.RunAsync(() => Events(scope).PostAsync(
            new InterestEarned(new Money(9m), new DateOnly(2026, 3, 2), scope.TrustBankId, "later"), ct), ct);

        var after = await ReportAsync(scope, view.Id, ct);
        after!.ReportJson.ShouldBe(before.ReportJson);
    }

    private static async Task<Guid> TrustLineAsync(OrgScope scope, CancellationToken ct)
    {
        Guid id = default;
        await scope.RunAsync(async () => id = await scope.Db.Set<JournalLine>()
            .Where(l => l.BankAccountId == scope.TrustBankId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id).SingleAsync(ct), ct);
        return id;
    }

    private static async Task<ReconciliationView> StartAsync(
        OrgScope scope, Guid bank, int year, int month, decimal statement, CancellationToken ct)
    {
        ReconciliationView view = null!;
        await scope.RunAsync(async () => view = await new StartReconciliationHandler(scope.Db)
            .Handle(new StartReconciliation(bank, year, month, statement), ct), ct);
        return view;
    }

    private static async Task<ReconciliationView> FinalizeAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        ReconciliationView view = null!;
        await scope.RunAsync(async () => view = await new FinalizeReconciliationHandler(scope.Db)
            .Handle(new FinalizeReconciliation(id), ct), ct);
        return view;
    }

    private static Task ClearAsync(OrgScope scope, Guid[] ids, CancellationToken ct) =>
        scope.RunAsync(() => new ApplyClearancesHandler(scope.Db, scope.Tenant).Handle(new ApplyClearances(ids), ct), ct);

    private static Task UnlockAsync(OrgScope scope, Guid id, string reason, CancellationToken ct) =>
        scope.RunAsync(() => new UnlockReconciliationHandler(scope.Db).Handle(new UnlockReconciliation(id, reason), ct), ct);

    private static async Task<ReconciliationReportResponse?> ReportAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        ReconciliationReportResponse? report = null;
        await scope.RunAsync(async () => report = await new GetReconciliationReportHandler(scope.Db)
            .Handle(new GetReconciliationReport(id), ct), ct);
        return report;
    }

    private static DateOnly Feb(int day) => new(2026, 2, day);
}
