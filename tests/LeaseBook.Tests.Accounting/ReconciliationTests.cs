using FluentValidation;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Accounting.Support;
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
            doc.RootElement.GetProperty("reconciliationId").GetGuid().ShouldBe(view.Id);
            doc.RootElement.GetProperty("bankAccountId").GetGuid().ShouldBe(scope.TrustBankId);
            doc.RootElement.GetProperty("period").GetString().ShouldBe("2026-02");
            doc.RootElement.GetProperty("statementEndingBalance").GetDecimal().ShouldBe(750m);
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

    /// <summary>
    /// Finalizing locks the (account, month), so who did it is part of the money record (ADR-039).
    /// Nothing asserted that until now: the suite finalized through a handler built with no actor
    /// context at all, which recorded a null finalizer and could not tell that apart from a system
    /// actor legitimately having no human. Finalize as a signed-in user so the column has a value
    /// to carry, and pin it.
    /// </summary>
    [Fact]
    public async Task Finalize_records_the_acting_user()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        await scope.RunAsync(() => Events(scope).PostAsync(
            new OwnerContribution(_owner, _property, new Money(1000m), Feb(1), scope.TrustBankId, "attribution"), ct), ct);
        await ClearAsync(scope, [await TrustLineAsync(scope, ct)], ct);

        var view = await StartAsync(scope, scope.TrustBankId, 2026, 2, 1000m, ct);
        view.Difference.ShouldBe(0m);

        // Executor directly with an explicit actor, per OrgScope: RunAsync attributes to a system
        // actor, whose null UserId would make this assertion vacuous.
        var user = UuidV7.NewId();
        await scope.Executor.RunAsync(scope.OrgId, Actor.User(user), Persona.Staff, () =>
            new FinalizeReconciliationHandler(scope.Db, scope.Actor)
                .Handle(new FinalizeReconciliation(view.Id), ct), ct);

        Guid? finalizedBy = null;
        await scope.RunAsync(async () => finalizedBy = await scope.Db.Set<BankReconciliation>().AsNoTracking()
            .Where(r => r.Id == view.Id).Select(r => r.FinalizedBy).SingleAsync(ct), ct);
        finalizedBy.ShouldBe(user);
    }

    // ---- validation: each rule refuses its own field, and nothing else does ----

    private static readonly StartReconciliation Start = new(UuidV7.NewId(), 2026, 2, 100m);

    private static readonly Dictionary<string, (Func<ISender, Task> Send, string[] Properties, string? Message)> Cases = new()
    {
        ["StartReconciliation.BankAccountId"] = Case(Start with { BankAccountId = Guid.Empty }, "BankAccountId"),
        ["StartReconciliation.Year.Low"] = Case(Start with { Year = 1999 }, "Year"),
        ["StartReconciliation.Year.High"] = Case(Start with { Year = 2101 }, "Year"),
        ["StartReconciliation.Month.Low"] = Case(Start with { Month = 0 }, "Month"),
        ["StartReconciliation.Month.High"] = Case(Start with { Month = 13 }, "Month"),
        ["StartReconciliation.StatementEndingBalance"] = Case(Start with { StatementEndingBalance = 100.005m },
            "StatementEndingBalance", "The statement ending balance must have at most 2 decimal places."),
        ["UnlockReconciliation.Reason"] = Case(new UnlockReconciliation(UuidV7.NewId(), ""), "Reason"),
    };

    public static TheoryData<string> Refusals() => new(Cases.Keys);

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_malformed_command_is_refused_by_its_own_rule_before_the_handler_runs(string name)
    {
        var (send, properties, message) = Cases[name];
        // A context already disposed: a command that got past validation would fail on its first read.
        var db = fixture.CreateContext(fixture.AppConnectionString, new OrgContext(), new ActorContext());
        await db.DisposeAsync();
        var sender = AccountingSender.Create(db, new OrgContext(), new ActorContext(), NoLeases);

        var refused = await Should.ThrowAsync<ValidationException>(() => send(sender));

        refused.Errors.Select(e => e.PropertyName).ShouldBe(properties);
        if (message is not null)
        {
            refused.Errors.First().ErrorMessage.ShouldBe(message);
        }
    }

    [Theory]
    [InlineData(2000, 1)]
    [InlineData(2100, 12)]
    public async Task The_first_and_last_month_in_range_can_be_started(int year, int month)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var view = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, year, month, -12.5m), ct);

        (view.Year, view.Month, view.StatementEndingBalance, view.Difference).ShouldBe((year, month, -12.5m, -12.5m));
    }

    // ---- the states a reconciliation moves through ----

    [Fact]
    public async Task An_open_month_is_re_entered_and_a_finalized_one_is_refused_until_unlocked()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        var first = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 100m), ct);
        var again = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct);
        (again.Id, again.Status, again.StatementEndingBalance).ShouldBe((first.Id, "in_progress", 0m));
        (await SendAsync(scope, new FinalizeReconciliation(first.Id), ct)).Status.ShouldBe("finalized");

        var refused = await Should.ThrowAsync<ReconciliationStateException>(
            () => SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct));
        (refused.Problem, refused.ReconciliationId, refused.Year, refused.Month)
            .ShouldBe((ReconciliationStateProblem.PeriodFinalized, (Guid?)null, (int?)2026, (int?)2));

        var reopened = await SendAsync(scope, new UnlockReconciliation(first.Id, "statement re-issued"), ct);
        reopened.Status.ShouldBe("reopened");
        (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct)).Id.ShouldBe(first.Id);
    }

    [Fact]
    public async Task Finalizing_twice_and_unlocking_an_open_reconciliation_are_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var finalized = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct);
        await SendAsync(scope, new FinalizeReconciliation(finalized.Id), ct);
        var open = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 3, 0m), ct);

        var twice = await Should.ThrowAsync<ReconciliationStateException>(
            () => SendAsync(scope, new FinalizeReconciliation(finalized.Id), ct));
        (twice.Problem, twice.ReconciliationId).ShouldBe((ReconciliationStateProblem.AlreadyFinalized, (Guid?)finalized.Id));

        var notFinalized = await Should.ThrowAsync<ReconciliationStateException>(
            () => SendAsync(scope, new UnlockReconciliation(open.Id, "nothing to unlock"), ct));
        (notFinalized.Problem, notFinalized.ReconciliationId).ShouldBe((ReconciliationStateProblem.NotFinalized, (Guid?)open.Id));
    }

    [Fact]
    public async Task Finalizing_without_a_declared_actor_is_refused_and_leaves_the_reconciliation_open()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var view = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 0m), ct);

        var refused = await Should.ThrowAsync<InvalidOperationException>(() => scope.RunAsync(() =>
            new FinalizeReconciliationHandler(scope.Db, new ActorContext()).Handle(new FinalizeReconciliation(view.Id), ct), ct));

        refused.Message.ShouldBe(
            "Finalizing a reconciliation requires a declared actor — Actor.User(id) for a signed-in " +
            "user, Actor.System(process) for a named process. Finalizing locks the (account, month) " +
            "against further bank postings (ADR-039), so it is refused rather than recorded null.");
        (await ReportAsync(scope, view.Id, ct)).ShouldBe(new ReconciliationReportResponse(view.Id, "in_progress", null));
    }

    // ---- reading reconciliations back ----

    [Fact]
    public async Task History_is_newest_month_first_then_newest_started_and_can_be_narrowed_to_one_account()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var december = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2025, 12, 0m), ct);
        await SendAsync(scope, new FinalizeReconciliation(december.Id), ct);
        var february = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 2, 20m), ct);
        var january = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 1, 10m), ct);
        // The same month on another account, started after the trust account's.
        var operating = await SendAsync(scope, new StartReconciliation(scope.OperatingBankId, 2026, 2, 30m), ct);

        var all = await QueryAsync(scope, new GetReconciliationHistory(), ct);
        var trust = await QueryAsync(scope, new GetReconciliationHistory(scope.TrustBankId), ct);

        all.Rows.Select(r => r.Id).ShouldBe([operating.Id, february.Id, january.Id, december.Id]);
        trust.Rows.Select(r => r.Id).ShouldBe([february.Id, january.Id, december.Id]);
        trust.Rows.Select(r => (r.Status, r.BankAccountId, r.Year, r.Month, r.StatementEndingBalance, r.FinalizedAt is null, r.HasReport))
            .ShouldBe(
            [
                ("in_progress", scope.TrustBankId, 2026, 2, 20m, true, false),
                ("in_progress", scope.TrustBankId, 2026, 1, 10m, true, false),
                ("finalized", scope.TrustBankId, 2025, 12, 0m, false, true),
            ]);
    }

    [Fact]
    public async Task A_report_for_no_such_reconciliation_is_null()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        (await QueryAsync(scope, new GetReconciliationReport(UuidV7.NewId()), ct)).ShouldBeNull();
    }

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    private static (Func<ISender, Task>, string[], string?) Case<T>(ICommand<T> command, string property, string? message = null) =>
        (sender => sender.Send(command), [property], message);

    private static async Task<T> SendAsync<T>(OrgScope scope, ICommand<T> command, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases).Send(command, ct), ct);
        return result;
    }

    private static async Task<T> QueryAsync<T>(OrgScope scope, IQuery<T> query, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases).Query(query, ct), ct);
        return result;
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
        await scope.RunAsync(async () => view = await new FinalizeReconciliationHandler(scope.Db, scope.Actor)
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
