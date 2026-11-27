using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.Modules.Operations.Domain;
using LeaseBook.Modules.Operations.Runs;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Operations;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// #573: <c>GET /api/operations/runs/{id}</c> says what a confirmed run did to each target — its run
/// outcome, its reason when it did not post, and who the target is today. Runs are confirmed through
/// the real run engine in a new organization, so the demo org's golden figures are undisturbed and
/// every reason below is one the engine and the run types actually record.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RunDetailEndpointTests(PostgresFixture fixture)
{
    private static readonly RunPeriod March = new(2026, 3);

    [Fact]
    public async Task A_rent_run_lists_unposted_targets_first_then_posted_ones_by_tenant_name()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, org) = await OrgWithLeasesAsync(ct);
        var missingLease = UuidV7.NewId();

        // Plan order is Bob, Alice, then a lease the schedule does not hold — neither the order the
        // response must give nor the order the rows were written in.
        var runId = await ConfirmAsync(org.OrgId, RunType.Rent, [org.BobLease, org.AliceLease, missingLease], ct);

        var detail = await DetailAsync(client, runId, ct);

        detail.Run.Id.ShouldBe(runId);
        detail.Run.RunType.ShouldBe("Rent");
        (detail.Run.PeriodYear, detail.Run.PeriodMonth).ShouldBe((2026, 3));
        detail.Items.Select(i => (i.TargetId, i.Outcome, i.Reason, i.TargetName, i.UnitLabel, i.TenantId, i.Amount))
            .ShouldBe(
            [
                (missingLease, "Excluded", "lease_not_in_schedule", null, null, null, 0m),
                (org.AliceLease, "Posted", null, "Alice Full", "#A", org.AliceTenant, 1450m),
                (org.BobLease, "Posted", null, "Bob Full", "#B", org.BobTenant, 1380m),
            ]);
        detail.Items.ShouldAllBe(i => i.TargetKind == "Lease");
    }

    [Fact]
    public async Task A_repeated_rent_run_reports_each_target_skipped_because_rent_was_already_charged()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, org) = await OrgWithLeasesAsync(ct);
        await ConfirmAsync(org.OrgId, RunType.Rent, [org.BobLease, org.AliceLease], ct);

        var repeat = await ConfirmAsync(org.OrgId, RunType.Rent, [org.BobLease, org.AliceLease], ct);

        var detail = await DetailAsync(client, repeat, ct);
        detail.Items.Select(i => (i.TargetName, i.Outcome, i.Reason, i.Amount)).ShouldBe(
        [
            ("Alice Full", "Skipped", "already_charged_in_period", 0m),
            ("Bob Full", "Skipped", "already_charged_in_period", 0m),
        ]);
    }

    [Fact]
    public async Task A_disbursement_run_names_its_owners_and_reports_one_it_cannot_find_after_them()
    {
        var ct = TestContext.Current.CancellationToken;
        var (client, org) = await OrgWithLeasesAsync(ct);
        var missingOwner = UuidV7.NewId();

        // No rent has been received, so the owner's cash-basis equity is zero.
        var runId = await ConfirmAsync(org.OrgId, RunType.Disbursement, [missingOwner, org.OwnerId], ct);

        var detail = await DetailAsync(client, runId, ct);
        detail.Run.RunType.ShouldBe("Disbursement");
        detail.Items.Select(i => (i.TargetKind, i.TargetId, i.Outcome, i.Reason, i.TargetName, i.UnitLabel, i.TenantId))
            .ShouldBe(
            [
                ("Owner", org.OwnerId, "Excluded", "non_positive_equity", "Test Owner", null, null),
                ("Owner", missingOwner, "Excluded", "owner_not_found", null, null, null),
            ]);
    }

    [Fact]
    public async Task Another_organizations_run_is_not_found()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, org) = await OrgWithLeasesAsync(ct);
        var runId = await ConfirmAsync(org.OrgId, RunType.Rent, [org.AliceLease], ct);
        var (_, stranger) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);

        var response = await stranger.GetAsync($"/api/operations/runs/{runId}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("run_not_found");
    }

    [Fact]
    public async Task A_tenant_cannot_read_a_run()
    {
        var ct = TestContext.Current.CancellationToken;
        var (_, org) = await OrgWithLeasesAsync(ct);
        var runId = await ConfirmAsync(org.OrgId, RunType.Rent, [org.AliceLease], ct);

        var tenant = fixture.Api.CreateClient();
        var email = $"tenant-{UuidV7.NewId():N}@run-detail.test";
        await AuthTestSupport.CreateUserAsync(fixture, org.OrgId, email, "A tenant", Roles.Tenant, ct);
        await tenant.PrimeCsrfAsync(ct);
        (await AuthTestSupport.LoginAsync(tenant, email, ct)).Status.ShouldBe("ok");
        await tenant.PrimeCsrfAsync(ct);
        await AuthTestSupport.EnrollMfaAsync(tenant, ct);

        var response = await tenant.GetAsync($"/api/operations/runs/{runId}", ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Org(
        Guid OrgId, Guid OwnerId, Guid AliceLease, Guid AliceTenant, Guid BobLease, Guid BobTenant);

    private static async Task<BulkRunDetailResponse> DetailAsync(HttpClient client, Guid runId, CancellationToken ct)
    {
        var response = await client.GetAsync($"/api/operations/runs/{runId}", ct);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(ct));
        return (await response.Content.ReadFromJsonAsync<BulkRunDetailResponse>(ct))!;
    }

    /// <summary>A staff member signed in to a new organization with one owner and two full-month leases.</summary>
    private async Task<(HttpClient Client, Org Org)> OrgWithLeasesAsync(CancellationToken ct)
    {
        var (orgId, client) = await AuthTestSupport.SignedInToNewOrgAsync(fixture, Roles.PMStaff, ct);
        Org? org = null;

        await DispatchAsync(orgId, async s =>
        {
            await s.Send(new CreateBankAccount("Operating Trust", null, null, "trust"), ct);
            var ownerId = await s.Send(new CreateOwner("Test Owner", null, null, null, 800, 0m), ct);
            var propertyId = await s.Send(new CreateProperty(ownerId, "100 Main St", "Asheville", "NC", null, null), ct);
            var unitA = await s.Send(new CreateUnit(propertyId, "#A", 1450m, "available"), ct);
            var unitB = await s.Send(new CreateUnit(propertyId, "#B", 1380m, "available"), ct);
            var alice = await s.Send(new CreateTenant("Alice Full", null, null, "current"), ct);
            var bob = await s.Send(new CreateTenant("Bob Full", null, null, "current"), ct);
            var aliceLease = await s.Send(new CreateLease(alice, unitA,
                new DateOnly(2025, 6, 1), new DateOnly(2027, 5, 31), 1450m, 1450m, "active"), ct);
            var bobLease = await s.Send(new CreateLease(bob, unitB,
                new DateOnly(2025, 6, 1), new DateOnly(2027, 5, 31), 1380m, 1380m, "active"), ct);
            org = new Org(orgId, ownerId, aliceLease, alice, bobLease, bob);
        }, ct);

        return (client, org!);
    }

    private async Task<Guid> ConfirmAsync(Guid orgId, RunType type, IReadOnlyList<Guid> targets, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var engine = scope.ServiceProvider.GetRequiredService<RunEngine>();
        RunResult? result = null;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var preview = await engine.PreviewAsync(type, March, ct);
            result = await engine.ConfirmAsync(
                type, March, targets, preview.CapabilitiesVersion, acknowledgeCapabilityChange: false, ct);
        }, ct);
        return result!.RunId;
    }

    private async Task DispatchAsync(Guid orgId, Func<ISender, Task> work, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var sender = scope.ServiceProvider.GetRequiredService<ISender>();
        await executor.RunAsSystemAsync(orgId, "test-harness", () => work(sender), ct);
    }
}
