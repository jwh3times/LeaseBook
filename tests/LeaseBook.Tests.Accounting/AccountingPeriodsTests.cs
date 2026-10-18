using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.Modules.Accounting.Provisioning;
using LeaseBook.Tests.Accounting.Support;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Accounting;

/// <summary>
/// WP-03: monthly periods are created lazily-open on first reference, idempotently and race-safely
/// (P32), and close flips open→closed once.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AccountingPeriodsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetOpenPeriod_lazily_creates_then_returns_the_same_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);
        var date = new DateOnly(2026, 2, 15);

        Guid first = default;
        Guid second = default;
        await scope.RunAsync(async () =>
        {
            var periods = new AccountingPeriods(scope.Db);
            var p1 = await periods.GetOpenPeriodAsync(date, ct);
            first = p1.Id;
            p1.Year.ShouldBe(2026);
            p1.Month.ShouldBe(2);
            p1.Status.ShouldBe(PeriodStatus.Open);

            second = (await periods.GetOpenPeriodAsync(new DateOnly(2026, 2, 1), ct)).Id; // any day in Feb
        }, ct);

        second.ShouldBe(first); // same month → same period, not a second row

        long count = 0;
        await scope.RunAsync(async () =>
            count = await scope.Db.Set<AccountingPeriod>().CountAsync(p => p.Year == 2026 && p.Month == 2, ct), ct);
        count.ShouldBe(1);
    }

    /// <summary>
    /// Two requests open the same new month at once. The first inserts the period and holds its
    /// transaction open; the second, which saw no period, inserts too and waits on the first's
    /// uncommitted key. Only then does the first commit, so the second always loses the insert and
    /// takes the path that reads the winner back. It must return the winner, not a period that shares
    /// only the year or only the month: the organization already holds one of each, ahead of the
    /// winner in both the unique index and the heap.
    /// </summary>
    [Fact]
    public async Task The_request_that_loses_the_race_to_open_a_month_returns_the_winners_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);
        await scope.RunAsync(async () =>
        {
            var periods = new AccountingPeriods(scope.Db);
            await periods.GetOpenPeriodAsync(new DateOnly(2025, 3, 1), ct);
            await periods.GetOpenPeriodAsync(new DateOnly(2026, 1, 1), ct);
        }, ct);

        var date = new DateOnly(2026, 3, 10);
        var race = await Lanes.RaceAsync(
            fixture, scope,
            lane => new AccountingPeriods(lane.Db).GetOpenPeriodAsync(date, ct),
            lane => new AccountingPeriods(lane.Db).GetOpenPeriodAsync(date, ct),
            ct);

        race.SecondError.ShouldBeNull();
        race.Second.ShouldNotBeNull();
        race.Second.Id.ShouldBe(race.First.Id);
        (race.Second.Year, race.Second.Month, race.Second.Status).ShouldBe((2026, 3, PeriodStatus.Open));

        long count = 0;
        await scope.RunAsync(async () =>
            count = await scope.Db.Set<AccountingPeriod>().CountAsync(p => p.Year == 2026 && p.Month == 3, ct), ct);
        count.ShouldBe(1); // exactly one row survived the race
    }

    [Fact]
    public async Task The_same_month_of_another_year_is_another_period()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var periods = new AccountingPeriods(scope.Db);
            var earlier = await periods.GetOpenPeriodAsync(new DateOnly(2025, 2, 10), ct);
            var later = await periods.GetOpenPeriodAsync(new DateOnly(2026, 2, 10), ct);

            later.Id.ShouldNotBe(earlier.Id);
            (later.Year, later.Month).ShouldBe((2026, 2));
            (earlier.Year, earlier.Month).ShouldBe((2025, 2));
        }, ct);
    }

    [Fact]
    public async Task Reading_a_month_that_is_already_open_writes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);
        await scope.RunAsync(() => new AccountingPeriods(scope.Db).GetOpenPeriodAsync(new DateOnly(2026, 5, 1), ct), ct);

        var saves = 0;
        scope.Db.SavingChanges += (_, _) => saves++;
        await scope.RunAsync(async () =>
            (await new AccountingPeriods(scope.Db).GetOpenPeriodAsync(new DateOnly(2026, 5, 20), ct)).Month.ShouldBe(5), ct);

        saves.ShouldBe(0); // found, so no insert is attempted to collide with the row already there
    }

    [Fact]
    public async Task A_failure_to_open_a_month_that_is_not_a_lost_race_is_surfaced()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);
        await scope.RunAsync(() => new ChartOfAccounts(scope.Db).ProvisionAsync([], ct), ct);

        // The caller's own pending write fails the save the period rides on: an account whose code the
        // organization already has. No other request opened the month, so there is no winner to return.
        await Should.ThrowAsync<DbUpdateException>(() => scope.RunAsync(async () =>
        {
            scope.Db.Add(Account.Create(AccountCodes.PmIncome, AccountClass.PmIncome, "Duplicate", null));
            await new AccountingPeriods(scope.Db).GetOpenPeriodAsync(new DateOnly(2026, 6, 1), ct);
        }, ct));

        scope.Db.ChangeTracker.Clear();
        long count = 0;
        await scope.RunAsync(async () =>
            count = await scope.Db.Set<AccountingPeriod>().CountAsync(p => p.Year == 2026 && p.Month == 6, ct), ct);
        count.ShouldBe(0);
    }

    [Fact]
    public async Task Closing_a_period_is_saved()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        await scope.RunAsync(() => new AccountingPeriods(scope.Db).CloseAsync(2026, 7, ct), ct);

        scope.Db.ChangeTracker.Clear();
        PeriodStatus status = default;
        await scope.RunAsync(async () => status = await scope.Db.Set<AccountingPeriod>().AsNoTracking()
            .Where(p => p.Year == 2026 && p.Month == 7).Select(p => p.Status).SingleAsync(ct), ct);
        status.ShouldBe(PeriodStatus.Closed);
    }

    [Fact]
    public void Closing_a_closed_period_keeps_the_time_it_was_first_closed()
    {
        var period = AccountingPeriod.Open(2026, 8);
        var first = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

        period.Close(first);
        period.Close(first.AddDays(3));

        period.Status.ShouldBe(PeriodStatus.Closed);
        period.ClosedAt.ShouldBe(first);
    }

    [Fact]
    public async Task CloseAsync_flips_open_to_closed_and_is_idempotent()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = await OrgScope.CreateAsync(fixture, ct);

        await scope.RunAsync(async () =>
        {
            var periods = new AccountingPeriods(scope.Db);
            await periods.GetOpenPeriodAsync(new DateOnly(2026, 4, 1), ct);

            var closed = await periods.CloseAsync(2026, 4, ct);
            closed.Status.ShouldBe(PeriodStatus.Closed);
            closed.ClosedAt.ShouldNotBeNull();

            var again = await periods.CloseAsync(2026, 4, ct); // no-op
            again.Id.ShouldBe(closed.Id);
            again.Status.ShouldBe(PeriodStatus.Closed);
        }, ct);
    }
}
