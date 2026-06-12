using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Periods;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Shouldly;

// Testcontainers pulls in BouncyCastle, whose root namespace `Org` shadows the entity type.
using OrgEntity = LeaseBook.Web.Persistence.Org;

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

    [Fact]
    public async Task GetOpenPeriod_is_concurrent_safe()
    {
        var ct = TestContext.Current.CancellationToken;
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Concurrency Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        // Two independent app-role connections racing to create the same month's period.
        var tenant1 = new TenantContext();
        await using var db1 = fixture.CreateContext(fixture.AppConnectionString, tenant1);
        var ex1 = new OrgScopedExecutor(db1, tenant1);

        var tenant2 = new TenantContext();
        await using var db2 = fixture.CreateContext(fixture.AppConnectionString, tenant2);
        var ex2 = new OrgScopedExecutor(db2, tenant2);

        var date = new DateOnly(2026, 3, 10);
        AccountingPeriod? p1 = null;
        AccountingPeriod? p2 = null;

        await Task.WhenAll(
            ex1.RunAsync(orgId, async () => p1 = await new AccountingPeriods(db1).GetOpenPeriodAsync(date, ct), ct),
            ex2.RunAsync(orgId, async () => p2 = await new AccountingPeriods(db2).GetOpenPeriodAsync(date, ct), ct));

        p1.ShouldNotBeNull();
        p2.ShouldNotBeNull();
        p1.Month.ShouldBe(3);
        p2.Month.ShouldBe(3);

        long count = 0;
        await ex1.RunAsync(orgId, async () =>
            count = await db1.Set<AccountingPeriod>().CountAsync(p => p.Year == 2026 && p.Month == 3, ct), ct);
        count.ShouldBe(1); // exactly one row survived the race
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
