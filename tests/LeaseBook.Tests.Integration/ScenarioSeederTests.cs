using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Accounting.Features.Banking;
using LeaseBook.Modules.Directory.Domain;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Seeding;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-13: the scenario seeder provisions the all-scenario org — post-sign-off through the real M7
/// import path, four months through the engine — idempotently, with the live role matrix (2 PMAdmin,
/// one TOTP-enrolled + 2 PMStaff), recorded run exclusions, the Reopened → re-finalized April, every
/// statement-delivery outcome, a demoable locked-period rejection, and the PMAdmin-only compliance pack.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ScenarioSeederTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Seeding_twice_is_idempotent_with_the_designed_directory_shape()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var entries = await QueryAsync(db => db.Set<JournalEntry>().CountAsync(ct), ct);
        entries.ShouldBeGreaterThan(0);

        // Re-run: the journal anchor short-circuits, and the pins re-verify inside the seeder.
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);
        (await QueryAsync(db => db.Set<JournalEntry>().CountAsync(ct), ct)).ShouldBe(entries);

        (await QueryAsync(db => db.Set<Owner>().CountAsync(o => !o.IsSystem, ct), ct)).ShouldBe(5);
        (await QueryAsync(db => db.Set<Tenant>().CountAsync(t => !t.IsSystem, ct), ct)).ShouldBe(8);
        (await QueryAsync(db => db.Set<Unit>().CountAsync(u => !u.IsSystem, ct), ct)).ShouldBe(11);
        (await QueryAsync(db => db.Set<Property>().CountAsync(p => !p.IsSystem, ct), ct)).ShouldBe(6);
        (await QueryAsync(db => db.Set<BankAccount>().CountAsync(ct), ct)).ShouldBe(3);
    }

    [Fact]
    public async Task Role_matrix_is_live_admin2_is_totp_enrolled_and_staff_can_log_in()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();

            var admin2 = await users.FindByEmailAsync(ScenarioSeeder.Admin2Email);
            admin2.ShouldNotBeNull();
            (await users.GetTwoFactorEnabledAsync(admin2)).ShouldBeTrue();
            (await users.GetAuthenticatorKeyAsync(admin2)).ShouldBe(ScenarioSeeder.Admin2TotpSecret);

            foreach (var staffEmail in new[] { ScenarioSeeder.StaffEmail, ScenarioSeeder.Staff2Email })
            {
                var staff = await users.FindByEmailAsync(staffEmail);
                staff.ShouldNotBeNull();
                staff.OrgId.ShouldBe(ScenarioSeeder.ScenarioOrgId);
                (await users.IsInRoleAsync(staff, Roles.PMStaff)).ShouldBeTrue();
                (await users.IsInRoleAsync(staff, Roles.PMAdmin)).ShouldBeFalse();
            }
        }

        // The first live PMStaff session: the seeded staff credential authenticates for real.
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(ScenarioSeeder.StaffEmail, ScenarioSeeder.StaffPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await login.Content.ReadFromJsonAsync<LoginResponse>(ct))!.Status.ShouldBe(LoginStatus.Ok);
    }

    [Fact]
    public async Task Run_history_records_every_run_and_both_disbursement_exclusion_reasons()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var runs = await QueryAsync(db => db.Set<BulkRun>().ToListAsync(ct), ct);
        runs.Count.ShouldBe(12); // 4 months × (rent, late fee, disbursement); the April re-run confirms nothing
        runs.Count(r => r.RunType == RunType.Disbursement).ShouldBe(4);

        var excluded = await QueryAsync(db =>
            (from item in db.Set<BulkRunItem>()
             join run in db.Set<BulkRun>() on item.RunId equals run.Id
             where run.RunType == RunType.Disbursement && item.Status == RunItemStatus.Excluded
             select item.SnapshotJson).ToListAsync(ct), ct);

        // O-S3 (below the floor, at exactly the floor) + O-S4 (negative equity) every month; O-S5
        // joins in April when its idle month parks it under its own reserve.
        excluded.Count.ShouldBe(9);
        excluded.Count(s => s!.Contains("below_reserve_floor")).ShouldBe(5);
        excluded.Count(s => s!.Contains("non_positive_equity")).ShouldBe(4);
    }

    [Fact]
    public async Task Reconciliations_lock_three_months_and_April_carries_the_reopen_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var recons = await QueryAsync(db => db.Set<BankReconciliation>().ToListAsync(ct), ct);
        recons.Count.ShouldBe(9); // 3 banks × Mar/Apr/May; June deliberately open
        recons.ShouldAllBe(r => r.Status == ReconciliationStatus.Finalized);

        var april = recons.Single(r =>
            r.BankAccountId == ScenarioSeeder.OperatingTrustId && r.PeriodMonth == 4);
        april.ReopenReason.ShouldNotBeNull("April was unlocked with a reason and re-finalized");
    }

    [Fact]
    public async Task Posting_into_a_finalized_month_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();

        // March is finalized — a bank-touching posting dated inside it must 409. The failed
        // transaction rolls back, so the fixture is untouched.
        await Should.ThrowAsync<AccountPeriodLockedException>(
            executor.RunAsSystemAsync(ScenarioSeeder.ScenarioOrgId, "test-harness", async () =>
                await sender.Send(new RecordBankAdjustment(
                    "fee", 1.00m, new DateOnly(2026, 3, 15), ScenarioSeeder.OperatingTrustId, null,
                    "Locked-period probe", "scenario-test:locked-period-probe"), ct), ct));
    }

    /// <summary>
    /// The scenario org carries every <see cref="DeliveryEventKind"/> in real rows (ADR-040), including
    /// the two histories the flat model could not express: an acceptance followed by a bounce, and the
    /// retry that then delivered <b>the same artifact</b>.
    /// </summary>
    [Fact]
    public async Task Statement_deliveries_carry_every_outcome_with_stored_artifacts()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var artifacts = await QueryAsync(
            db => db.Set<StatementArtifact>()
                .Include(a => a.Attempts).ThenInclude(t => t.Events)
                .ToListAsync(ct),
            ct);

        artifacts.Count.ShouldBe(3, "O-S1 May, O-S2 April, O-S5 April");
        artifacts.ShouldAllBe(a => a.ArtifactKey.EndsWith(".pdf"));
        artifacts.ShouldAllBe(a => a.Basis == "accrual");

        var attempts = artifacts.SelectMany(a => a.Attempts).ToList();
        attempts.Count.ShouldBe(4, "the bounced O-S2 send was retried");

        // Every kind is represented, so a demo can show each one against real rows.
        attempts.SelectMany(t => t.Events).Select(e => e.Kind).Distinct().ShouldBe(
            [
                DeliveryEventKind.Queued, DeliveryEventKind.Accepted, DeliveryEventKind.Delivered,
                DeliveryEventKind.Bounced, DeliveryEventKind.Failed,
            ],
            ignoreOrder: true);

        // The retried artifact: two attempts, the first bounced, the second delivered — and both
        // point at one artifact, so the owner received exactly the document that first bounced.
        var retried = artifacts.Single(a => a.Attempts.Count == 2);
        retried.Attempts
            .OrderBy(t => t.CreatedAt).ThenBy(t => t.Id)
            .Select(DeliveryStatus.Of)
            .ShouldBe([DeliveryEventKind.Bounced, DeliveryEventKind.Delivered]);

        // The other two: one still pending, one that never reached the provider.
        artifacts.Where(a => a.Attempts.Count == 1)
            .Select(a => DeliveryStatus.Of(a.Attempts[0]))
            .ShouldBe([DeliveryEventKind.Queued, DeliveryEventKind.Failed], ignoreOrder: true);
    }

    [Fact]
    public async Task Compliance_pack_is_admin_only_and_generates_for_the_locked_quarter()
    {
        var ct = TestContext.Current.CancellationToken;
        await ScenarioSeeder.SeedAsync(fixture.Api.Services, ct);

        var url = $"/api/reports/compliance-pack?bankAccountId={ScenarioSeeder.OperatingTrustId}" +
            "&from=2026-03-01&to=2026-05-31";

        var staff = fixture.Api.CreateClient();
        await staff.PrimeCsrfAsync(ct);
        (await staff.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(ScenarioSeeder.StaffEmail, ScenarioSeeder.StaffPassword), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await staff.GetAsync(url, ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var admin = fixture.Api.CreateClient();
        await admin.PrimeCsrfAsync(ct);
        (await admin.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(ScenarioSeeder.AdminEmail, ScenarioSeeder.AdminPassword), ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        var pack = await admin.GetAsync(url, ct);
        pack.StatusCode.ShouldBe(HttpStatusCode.OK);
        pack.Content.Headers.ContentType!.MediaType.ShouldBe("application/zip");
    }

    private async Task<T> QueryAsync<T>(Func<AppDbContext, Task<T>> query, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        T result = default!;
        await executor.RunAsSystemAsync(ScenarioSeeder.ScenarioOrgId, "test-harness", async () => result = await query(db), ct);
        return result;
    }
}
