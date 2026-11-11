using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Diagnostics;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Accounting.Features.Posting.Events;
using LeaseBook.Modules.Accounting.Features.Reconciliation;
using LeaseBook.Modules.Directory.Domain;
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
/// #656, the ADR-014 addendum of 2026-11-09: a month reconciles to its own statement, and an account's
/// months finalize in order. A month's cleared balance is the lines its account's reconciliations
/// stamped for that month or earlier, plus the cleared, unreconciled lines assigned to it while it is
/// finalized. Only a line dated through the month's last day can be assigned, and finalize stamps only
/// what it assigned.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ReconciliationMonthTests(PostgresFixture fixture)
{
    private readonly Guid _owner = UuidV7.NewId();
    private readonly Guid _property = UuidV7.NewId();

    // Noon UTC on 20 June 2026: June is the organization's current month in every supported zone.
    private static readonly TimeProvider June20 = new FixedClock(new DateTimeOffset(2026, 6, 20, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Re_finalizing_April_in_June_reaches_zero_at_Aprils_statement_and_stamps_none_of_May_or_June()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        var april = await DepositAsync(scope, 1_000m, new(2026, 4, 10), ct);
        var may = await DepositAsync(scope, 200m, new(2026, 5, 5), ct);
        var june = await DepositAsync(scope, 300m, new(2026, 6, 3), ct);
        await ClearAsync(scope, [april, may, june], ct);

        var aprilRecon = await FinalizeMonthAsync(scope, 4, 1_000m, ct);
        var mayRecon = await FinalizeMonthAsync(scope, 5, 1_200m, ct);
        var juneRecon = await FinalizeMonthAsync(scope, 6, 1_500m, ct);

        // The bank's April statement showed interest the register lacked: unlock June, May, then April.
        await SendAsync(scope, new UnlockReconciliation(juneRecon, "April statement re-issued"), ct);
        await SendAsync(scope, new UnlockReconciliation(mayRecon, "April statement re-issued"), ct);
        await SendAsync(scope, new UnlockReconciliation(aprilRecon, "April statement re-issued"), ct);
        var interest = await PostAsync(scope, new InterestEarned(new Money(5m), new(2026, 4, 30), scope.TrustBankId, "April interest"), ct);
        // Meanwhile a June deposit has cleared and waits for June: it is no part of April.
        var lateJune = await DepositAsync(scope, 50m, new(2026, 6, 15), ct);
        await ClearAsync(scope, [lateJune], ct);

        // April's own statement: 1,005. The interest is still uncleared; assigning it clears it.
        var restated = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 1_005m), ct);
        restated.Id.ShouldBe(aprilRecon);
        var refinalized = await SendAsync(scope, new FinalizeReconciliation(aprilRecon, [interest]), ct);

        (refinalized.Status, refinalized.ClearedBalance, refinalized.Difference).ShouldBe(("finalized", 1_005m, 0m));
        (await StampsAsync(scope, ct)).ShouldBe(ignoreOrder: true, expected: new Dictionary<Guid, Guid?>
        {
            [april] = aprilRecon,
            [interest] = aprilRecon,
            [may] = mayRecon,
            [june] = juneRecon,
            [lateJune] = null,
        });

        // Finalized again, April is locked again.
        await Should.ThrowAsync<AccountPeriodLockedException>(() => PostAsync(
            scope, new InterestEarned(new Money(1m), new(2026, 4, 29), scope.TrustBankId, "too late for April"), ct));
    }

    [Fact]
    public async Task A_check_dated_in_April_and_paid_in_May_counts_in_May_not_April()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        var deposit = await DepositAsync(scope, 1_000m, new(2026, 4, 1), ct);
        var check = await PostAsync(scope, new OwnerDisbursed(_owner, new Money(100m), new(2026, 4, 28), scope.TrustBankId, "Check 1001"), ct);

        // April's statement does not show the check: only the deposit is cleared when April closes.
        await ClearAsync(scope, [deposit], ct);
        var aprilRecon = await FinalizeMonthAsync(scope, 4, 1_000m, ct);

        // It clears on 3 May, and May's statement shows it paid.
        await ClearAsync(scope, [check], ct);
        var mayRecon = await FinalizeMonthAsync(scope, 5, 900m, ct);

        (await StampsAsync(scope, ct)).ShouldBe(ignoreOrder: true, expected: new Dictionary<Guid, Guid?> { [deposit] = aprilRecon, [check] = mayRecon });
        // Each month still nets to its own statement: April 1,000 without the check, May 900 with it.
        // A month bound by date ("lines dated through its end") would count the check in April.
        (await SweepAsync(scope, ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Assigning_a_line_dated_after_the_months_last_day_is_refused_and_stamps_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        var april = await DepositAsync(scope, 1_000m, new(2026, 4, 10), ct);
        var may = await DepositAsync(scope, 200m, new(2026, 5, 2), ct);
        await ClearAsync(scope, [april, may], ct);
        var aprilRecon = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 1_200m), ct)).Id;

        var refused = await Should.ThrowAsync<ReconciliationLineNotAssignableException>(
            () => SendAsync(scope, new FinalizeReconciliation(aprilRecon, [april, may]), ct));

        (refused.Code, refused.LineDate, refused.Year, refused.Month)
            .ShouldBe(("reconciliation_line_not_assignable", (DateOnly?)new DateOnly(2026, 5, 2), 2026, 4));
        (await StampsAsync(scope, ct)).ShouldBe(ignoreOrder: true, expected: new Dictionary<Guid, Guid?> { [april] = null, [may] = null });
    }

    /// <summary>
    /// Only the organization's current month may be finalized without naming its lines. For any other
    /// month the date-bounded default is the rule the addendum rejected: an April-dated check paid in May
    /// would count in April. Its live figure likewise counts only what is stamped, with nothing ticked.
    /// </summary>
    [Fact]
    public async Task Only_the_current_month_assigns_its_cleared_lines_by_default()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);
        var deposit = await DepositAsync(scope, 1_000m, new(2026, 4, 1), ct);
        var check = await PostAsync(scope, new OwnerDisbursed(_owner, new Money(100m), new(2026, 4, 28), scope.TrustBankId, "Check 1001"), ct);
        await ClearAsync(scope, [deposit, check], ct);

        var april = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 1_000m), ct);
        (april.ClearedBalance, april.Difference).ShouldBe((0m, 1_000m));
        var refused = await Should.ThrowAsync<ReconciliationLinesRequiredException>(
            () => SendAsync(scope, new FinalizeReconciliation(april.Id), ct));
        (refused.Code, refused.Year, refused.Month).ShouldBe(("reconciliation_lines_required", 2026, 4));
        (await StampsAsync(scope, ct)).ShouldBe(ignoreOrder: true, expected: new Dictionary<Guid, Guid?> { [deposit] = null, [check] = null });

        // June is the organization's current month: its cleared lines through 30 June are assigned.
        var june = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 6, 900m), ct);
        (june.ClearedBalance, june.Difference).ShouldBe((900m, 0m));
        (await SendAsync(scope, new FinalizeReconciliation(june.Id), ct)).Status.ShouldBe("finalized");
    }

    // ---- what a reopened month may take ----

    [Fact]
    public async Task A_reopened_month_offers_its_unreconciled_lines_through_its_last_day_and_what_it_already_stamped()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);

        var deposit = await DepositAsync(scope, 1_000m, new(2026, 4, 1), ct);
        var check = await PostAsync(scope, new OwnerDisbursed(_owner, new Money(100m), new(2026, 4, 28), scope.TrustBankId, "Check 1001"), ct);
        await ClearAsync(scope, [deposit], ct);
        var april = await FinalizeMonthAsync(scope, 4, 1_000m, ct);
        await SendAsync(scope, new UnlockReconciliation(april, "Statement showed interest"), ct);
        var interest = await PostAsync(scope, new InterestEarned(new Money(5m), new(2026, 4, 30), scope.TrustBankId, "April interest"), ct);
        await ClearAsync(scope, [interest], ct);
        var may = await DepositAsync(scope, 50m, new(2026, 5, 2), ct);
        await ClearAsync(scope, [may], ct);

        var lines = await QueryAsync(scope, new GetReconciliationLines(april), ct);

        (lines!.Reconciliation.Id, lines.Reconciliation.Status, lines.Reconciliation.StatementEndingBalance)
            .ShouldBe((april, "reopened", 1_000m));
        lines.ReconciledThrough.ShouldBe(1_000m);
        lines.Lines.Select(l => (l.JournalLineId, l.EntryDate, l.Description, l.Amount, l.Status)).ShouldBe(
        [
            (check, new DateOnly(2026, 4, 28), "Check 1001", -100m, "uncleared"),
            (interest, new DateOnly(2026, 4, 30), "April interest", 5m, "cleared"),
        ]);
        (await QueryAsync(scope, new GetReconciliationLines(UuidV7.NewId()), ct)).ShouldBeNull();
    }

    // ---- invariant I12 ----

    [Fact]
    public async Task The_I12_sweep_catches_a_finalized_month_whose_stamped_lines_no_longer_net_to_its_statement()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);
        var deposit = await DepositAsync(scope, 1_000m, new(2026, 4, 10), ct);
        await ClearAsync(scope, [deposit], ct);
        var april = await FinalizeMonthAsync(scope, 4, 1_000m, ct);
        (await SweepAsync(scope, ct)).ShouldBeEmpty();

        // No command un-stamps a reconciled line; a stray write does.
        await scope.RunAsync(() => scope.Db.Database.ExecuteSqlAsync(
            $"UPDATE bank_line_status SET status = 'cleared', reconciliation_id = NULL WHERE journal_line_id = {deposit}", ct), ct);

        var violation = (await SweepAsync(scope, ct)).ShouldHaveSingleItem();
        (violation.Invariant, violation.Detail).ShouldBe(("I12",
            $"reconciliation {april} for bank {scope.TrustBankId} 2026-04 is finalized at a statement of 1000.00, "
            + "but the lines stamped for it and the months before it net to 0.00"));
    }

    [Fact]
    public async Task The_I12_sweep_catches_a_month_that_stamped_a_line_dated_after_its_last_day()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);
        var april = await DepositAsync(scope, 1_000m, new(2026, 4, 10), ct);
        var may = await DepositAsync(scope, 200m, new(2026, 5, 2), ct);
        await ClearAsync(scope, [april], ct);
        var aprilRecon = await FinalizeMonthAsync(scope, 4, 1_000m, ct);

        // What the engine did before #656: a re-finalized April stamped May's line, and balanced only at
        // a statement that counted it.
        await scope.RunAsync(async () =>
        {
            await scope.Db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO bank_line_status (journal_line_id, org_id, status, cleared_at, reconciliation_id, created_at, updated_at)
                VALUES ({may}, {scope.OrgId}, 'reconciled', now(), {aprilRecon}, now(), now())
                """, ct);
            await scope.Db.Database.ExecuteSqlAsync(
                $"UPDATE bank_reconciliations SET statement_ending_balance = 1200.00 WHERE id = {aprilRecon}", ct);
        }, ct);

        var violation = (await SweepAsync(scope, ct)).ShouldHaveSingleItem();
        (violation.Invariant, violation.Detail).ShouldBe(("I12",
            $"reconciliation {aprilRecon} for bank {scope.TrustBankId} 2026-04 stamped 1 line(s) dated after "
            + $"the month's last day, e.g. line {may} dated 2026-05-02"));
    }

    private static async Task<IReadOnlyList<InvariantViolation>> SweepAsync(OrgScope scope, CancellationToken ct)
    {
        IReadOnlyList<InvariantViolation> result = [];
        await scope.RunAsync(async () => result = await new InvariantChecks(scope.Db).CheckCoreAsync(ct), ct);
        return result;
    }

    // ---- the report ----

    [Fact]
    public async Task Finalizing_again_replaces_the_report_with_its_history_and_the_audit_keeps_the_one_it_replaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct, owners: [_owner], properties: [_property]);
        var (bookkeeper, administrator) = (UuidV7.NewId(), UuidV7.NewId());

        var deposit = await DepositAsync(scope, 1_000m, new(2026, 4, 10), ct);
        await ClearAsync(scope, [deposit], ct);
        var april = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 1_000m), ct)).Id;
        await SendAsAsync(scope, bookkeeper, new FinalizeReconciliation(april, [deposit]), ct);
        var (firstReport, firstFinalizedAt) = await ReportAsync(scope, april, ct);

        await SendAsAsync(scope, administrator, new UnlockReconciliation(april, "Statement showed interest"), ct);
        var interest = await PostAsync(scope, new InterestEarned(new Money(5m), new(2026, 4, 30), scope.TrustBankId, "April interest"), ct);
        await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 1_005m), ct);
        await SendAsAsync(scope, bookkeeper, new FinalizeReconciliation(april, [interest]), ct);
        var (report, finalizedAgainAt) = await ReportAsync(scope, april, ct);

        using (var doc = System.Text.Json.JsonDocument.Parse(report))
        {
            var root = doc.RootElement;
            (root.GetProperty("statementEndingBalance").GetDecimal(), root.GetProperty("clearedBalance").GetDecimal(),
                    root.GetProperty("reconciledItemCount").GetInt32())
                .ShouldBe((1_005m, 1_005m, 1));
            root.GetProperty("firstFinalizedAt").GetDateTime().ShouldBe(firstFinalizedAt, TimeSpan.FromMilliseconds(1));
            root.GetProperty("reopenedBy").GetGuid().ShouldBe(administrator);
            root.GetProperty("reopenReason").GetString().ShouldBe("Statement showed interest");
            root.GetProperty("finalizedAgainAt").GetDateTime().ShouldBe(finalizedAgainAt, TimeSpan.FromMilliseconds(1));
        }

        finalizedAgainAt.ShouldBeGreaterThan(firstFinalizedAt);
        // jsonb stores the report normalized (key order, spacing), so compare it as JSON, not as text.
        (await ReplacedReportsAsync(scope, april, ct))
            .ShouldContain(before => System.Text.Json.Nodes.JsonNode.DeepEquals(
                System.Text.Json.Nodes.JsonNode.Parse(before), System.Text.Json.Nodes.JsonNode.Parse(firstReport)));
    }

    /// <summary>
    /// A month reopened before the first-finalized time was kept has none recorded. Its last finalize
    /// time is the first one it can still name, so finalizing it again reports that, never null.
    /// </summary>
    [Fact]
    public async Task A_month_reopened_before_its_first_finalize_was_kept_reports_its_last_finalize_as_the_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var april = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 0m), ct)).Id;
        await SendAsync(scope, new FinalizeReconciliation(april, []), ct);
        var (_, lastFinalizedAt) = await ReportAsync(scope, april, ct);

        // As the unlock wrote it before #656: reopened, with no first-finalized time or reopener.
        await scope.RunAsync(() => scope.Db.Database.ExecuteSqlAsync(
            $"""
            UPDATE bank_reconciliations
            SET status = 'reopened', reopen_reason = 'Statement re-issued', first_finalized_at = NULL
            WHERE id = {april}
            """, ct), ct);
        scope.Db.ChangeTracker.Clear();

        await SendAsync(scope, new FinalizeReconciliation(april, []), ct);
        var (report, _) = await ReportAsync(scope, april, ct);

        using var doc = System.Text.Json.JsonDocument.Parse(report);
        doc.RootElement.GetProperty("firstFinalizedAt").GetDateTime().ShouldBe(lastFinalizedAt, TimeSpan.FromMilliseconds(1));
        doc.RootElement.GetProperty("reopenReason").GetString().ShouldBe("Statement re-issued");
    }

    // ---- an account's months finalize in order ----

    [Fact]
    public async Task Finalizing_a_month_while_a_later_one_is_finalized_is_refused_naming_the_latest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var march = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 3, 0m), ct)).Id;
        await FinalizeMonthAsync(scope, 4, 0m, ct);
        await FinalizeMonthAsync(scope, 5, 0m, ct);

        var refused = await Should.ThrowAsync<ReconciliationOrderException>(
            () => SendAsync(scope, new FinalizeReconciliation(march), ct));

        (refused.Code, refused.Year, refused.Month).ShouldBe(("reconciliation_later_month_finalized", 2026, 5));
        refused.Message.ShouldBe("2026-05 is finalized for this account; unlock it before finalizing an earlier month.");
    }

    [Fact]
    public async Task Starting_a_month_while_a_later_one_is_finalized_is_refused_and_a_never_reconciled_month_blocks_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        // April was never reconciled; May finalizes without it.
        await FinalizeMonthAsync(scope, 5, 0m, ct);

        var refused = await Should.ThrowAsync<ReconciliationOrderException>(
            () => SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 4, 0m), ct));
        (refused.Code, refused.Year, refused.Month).ShouldBe(("reconciliation_later_month_finalized", 2026, 5));

        // Another account's months are its own.
        (await SendAsync(scope, new StartReconciliation(scope.OperatingBankId, 2026, 4, 0m), ct)).Status.ShouldBe("in_progress");
    }

    [Fact]
    public async Task Finalizing_a_month_while_an_earlier_one_is_reopened_is_refused_naming_the_earliest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var march = await FinalizeMonthAsync(scope, 3, 0m, ct);
        var april = await FinalizeMonthAsync(scope, 4, 0m, ct);
        await SendAsync(scope, new UnlockReconciliation(april, "re-issued"), ct);
        await SendAsync(scope, new UnlockReconciliation(march, "re-issued"), ct);

        // May can be prepared, but not finalized ahead of March.
        var may = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 5, 0m), ct)).Id;
        var refused = await Should.ThrowAsync<ReconciliationOrderException>(
            () => SendAsync(scope, new FinalizeReconciliation(may), ct));

        (refused.Code, refused.Year, refused.Month).ShouldBe(("reconciliation_earlier_month_reopened", 2026, 3));
        refused.Message.ShouldBe("2026-03 is reopened for this account; finalize it again before a later month.");

        // March first, then April, then May.
        await SendAsync(scope, new FinalizeReconciliation(march, []), ct);
        await SendAsync(scope, new FinalizeReconciliation(april, []), ct);
        (await SendAsync(scope, new FinalizeReconciliation(may, []), ct)).Status.ShouldBe("finalized");
    }

    [Fact]
    public async Task Unlocking_a_month_while_a_later_one_is_finalized_is_refused_naming_the_latest()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        var april = await FinalizeMonthAsync(scope, 4, 0m, ct);
        var may = await FinalizeMonthAsync(scope, 5, 0m, ct);
        await FinalizeMonthAsync(scope, 6, 0m, ct);

        foreach (var earlier in new[] { april, may })
        {
            var refused = await Should.ThrowAsync<ReconciliationOrderException>(
                () => SendAsync(scope, new UnlockReconciliation(earlier, "re-issued"), ct));
            (refused.Code, refused.Year, refused.Month).ShouldBe(("reconciliation_later_month_finalized", 2026, 6));
            refused.Message.ShouldBe("2026-06 is finalized for this account; unlock it before unlocking an earlier month.");
        }
    }

    // ---- no future month ----

    [Fact]
    public async Task Starting_or_finalizing_a_month_after_the_organizations_current_month_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);

        // 20 June is the organization's today: July has not begun.
        var start = await Should.ThrowAsync<ReconciliationPeriodInFutureException>(
            () => SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 7, 0m), ct));
        (start.Code, start.Year, start.Month, start.Today).ShouldBe(("reconciliation_period_in_future", 2026, 7, new DateOnly(2026, 6, 20)));
        start.Message.ShouldBe("2026-07 is after the organization's current month, 2026-06, so it cannot be reconciled yet.");

        // A row started for July when July was current cannot be finalized from an earlier today.
        var july = (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 7, 0m), ct, July2)).Id;
        await Should.ThrowAsync<ReconciliationPeriodInFutureException>(
            () => SendAsync(scope, new FinalizeReconciliation(july), ct));
    }

    [Fact]
    public async Task The_current_month_is_the_organizations_not_the_UTC_one()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await ProvisionedScopeAsync(fixture, ct);
        // 05:00 UTC on 1 July is 01:00 on 1 July in New York, and still 30 June in Los Angeles.
        var pacificJune30 = new FixedClock(new DateTimeOffset(2026, 7, 1, 5, 0, 0, TimeSpan.Zero));

        await Should.ThrowAsync<ReconciliationPeriodInFutureException>(() => SendAsync(
            scope, new StartReconciliation(scope.TrustBankId, 2026, 7, 0m), ct, pacificJune30, "America/Los_Angeles"));
        (await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, 7, 0m), ct, pacificJune30, "America/New_York"))
            .Status.ShouldBe("in_progress");
    }

    private async Task<Guid> DepositAsync(OrgScope scope, decimal amount, DateOnly date, CancellationToken ct) =>
        await PostAsync(scope, new OwnerContribution(_owner, _property, new Money(amount), date, scope.TrustBankId, $"deposit {date}"), ct);

    /// <summary>Posts an event and returns its one trust-bank line.</summary>
    private static async Task<Guid> PostAsync(OrgScope scope, AccountingEvent @event, CancellationToken ct)
    {
        Guid entryId = default;
        await scope.RunAsync(async () => entryId = await Events(scope).PostAsync(@event, ct), ct);
        Guid lineId = default;
        await scope.RunAsync(async () => lineId = await scope.Db.Set<JournalLine>()
            .Where(l => l.EntryId == entryId && l.BankAccountId == scope.TrustBankId && l.AccountClass == AccountClass.TrustBank)
            .Select(l => l.Id).SingleAsync(ct), ct);
        return lineId;
    }

    /// <summary>
    /// Finalizes a month as the re-finalize view will: read its lines, tick every cleared one, and check
    /// that what is stamped plus what is ticked meets the statement before finalizing with those lines.
    /// </summary>
    private static async Task<Guid> FinalizeMonthAsync(OrgScope scope, int month, decimal statement, CancellationToken ct)
    {
        var view = await SendAsync(scope, new StartReconciliation(scope.TrustBankId, 2026, month, statement), ct);
        var lines = (await QueryAsync(scope, new GetReconciliationLines(view.Id), ct))!;
        var ticked = lines.Lines.Where(l => l.Status == "cleared").ToList();
        (lines.ReconciledThrough + ticked.Sum(l => l.Amount)).ShouldBe(statement, $"2026-{month:D2} should tick to its statement");
        return (await SendAsync(scope, new FinalizeReconciliation(view.Id, [.. ticked.Select(l => l.JournalLineId)]), ct)).Id;
    }

    /// <summary>Every trust-bank line on the account, with the reconciliation that stamped it (null when none has).</summary>
    private static async Task<Dictionary<Guid, Guid?>> StampsAsync(OrgScope scope, CancellationToken ct)
    {
        Dictionary<Guid, Guid?> stamps = [];
        await scope.RunAsync(async () => stamps = await (
            from line in scope.Db.Set<JournalLine>()
            where line.BankAccountId == scope.TrustBankId && line.AccountClass == AccountClass.TrustBank
            join state in scope.Db.Set<BankLineState>() on line.Id equals state.JournalLineId into states
            from state in states.DefaultIfEmpty()
            select new { line.Id, Stamp = state == null || state.Status != BankLineStatus.Reconciled ? null : state.ReconciliationId })
            .ToDictionaryAsync(x => x.Id, x => x.Stamp, ct), ct);
        return stamps;
    }

    private static Task ClearAsync(OrgScope scope, Guid[] ids, CancellationToken ct) =>
        SendAsync(scope, new ApplyClearances(ids), ct);

    private static readonly Leases NoLeases = new(new Dictionary<Guid, TenantPostingDimensions>());

    private static async Task<T> QueryAsync<T>(OrgScope scope, IQuery<T> query, CancellationToken ct)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases, June20).Query(query, ct), ct);
        return result;
    }

    /// <summary>Sends as a signed-in user, so the actor columns have a value to carry (ADR-039).</summary>
    private static async Task<T> SendAsAsync<T>(OrgScope scope, Guid user, ICommand<T> command, CancellationToken ct)
    {
        T result = default!;
        await scope.Executor.RunAsync(scope.OrgId, Actor.User(user), Persona.Staff, async () =>
            result = await AccountingSender.Create(scope, NoLeases, June20).Send(command, ct), ct);
        return result;
    }

    /// <summary>The stored report verbatim, and the reconciliation's own finalized time.</summary>
    private static async Task<(string Report, DateTime FinalizedAt)> ReportAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        (string Report, DateTime FinalizedAt) row = default;
        await scope.RunAsync(async () =>
        {
            var found = await scope.Db.Set<BankReconciliation>().AsNoTracking()
                .Where(r => r.Id == id).Select(r => new { r.ReportSnapshot, r.FinalizedAt }).SingleAsync(ct);
            row = (found.ReportSnapshot!, found.FinalizedAt!.Value);
        }, ct);
        return row;
    }

    /// <summary>Every report the audit trail's before-images hold for the reconciliation.</summary>
    private static async Task<List<string>> ReplacedReportsAsync(OrgScope scope, Guid id, CancellationToken ct)
    {
        List<string?> befores = [];
        await scope.RunAsync(async () => befores = await scope.Db.AuditEvents.AsNoTracking()
            .Where(a => a.EntityType == "bank_reconciliations" && a.EntityId == id && a.Action == "update")
            .Select(a => a.Before).ToListAsync(ct), ct);
        return befores
            .Select(b => System.Text.Json.JsonDocument.Parse(b!).RootElement.GetProperty("ReportSnapshot"))
            .Where(s => s.ValueKind == System.Text.Json.JsonValueKind.String)
            .Select(s => s.GetString()!)
            .ToList();
    }

    private static readonly TimeProvider July2 = new FixedClock(new DateTimeOffset(2026, 7, 2, 12, 0, 0, TimeSpan.Zero));

    private static async Task<T> SendAsync<T>(
        OrgScope scope, ICommand<T> command, CancellationToken ct, TimeProvider? clock = null, string zone = SupportedTimeZones.Default)
    {
        T result = default!;
        await scope.RunAsync(async () => result = await AccountingSender.Create(scope, NoLeases, clock ?? June20, zone).Send(command, ct), ct);
        return result;
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
