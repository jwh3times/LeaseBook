using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Directory.Features.BankAccounts;
using LeaseBook.Modules.Directory.Features.Leases;
using LeaseBook.Modules.Directory.Features.Owners;
using LeaseBook.Modules.Directory.Features.Properties;
using LeaseBook.Modules.Directory.Features.Tenants;
using LeaseBook.Modules.Directory.Features.Units;
using LeaseBook.SharedKernel;
using LeaseBook.SharedKernel.Cqrs;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Adapters;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Operations;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;
using OrgEntity = LeaseBook.Web.Persistence.Org;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The preview → confirm window (ADR-028 / Task 10). The freeze proven by
/// <see cref="RunCapabilityFreezeTests"/> makes one confirm internally consistent; it says nothing
/// about the gap BEFORE the confirm. An operator previews, reads amounts off the screen, and clicks
/// Confirm; if the capability set moved in between, the confirm would post under a set the operator
/// never saw. The operator selected target <i>ids</i> — the <i>amounts</i> they approved were the
/// preview's.
/// <para>
/// <b>Why an echoed token is not "trusting client input".</b> The client carries an opaque value back
/// and the SERVER compares it against what it resolves itself — the same shape as an ETag /
/// If-Match. A forged token can only cause the server to reject a confirm it would otherwise accept,
/// never the reverse: the guard's only privilege is to say no.
/// </para>
/// <para>
/// <b>Test-isolation hazard.</b> <c>feature_flags</c> is global — no <c>org_id</c> — and this
/// assembly shares one <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>, so
/// every flag mutation is undone in a <c>finally</c> that also notifies. The org-scoped half needs no
/// cleanup: each test mints its own org.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RunCapabilityVersionTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    private static readonly string Capability = CapabilityCatalog.ConsolidatedStatements.Name;

    /// <summary>
    /// The whole point of the task. The flip is a real capability change, not a fabricated token:
    /// it proves the version actually MOVES when the resolved set moves, which a hand-made bogus
    /// string could not.
    /// </summary>
    [Fact]
    public async Task Confirm_with_a_stale_capabilities_version_is_rejected_with_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        // ConsolidatedStatements is RequiresGrant: true, so without a live grant the resolver
        // short-circuits to false and the flip below could never move the version — this test would
        // pass vacuously as "unchanged".
        await GrantEntitlementAsync(setup.OrgId, ct);

        try
        {
            var preview = await client.GetFromJsonAsync<RunPreviewSpaResponse>(
                "/api/operations/runs/rent/preview?year=2026&month=8", ct);
            preview.ShouldNotBeNull();
            preview.CapabilitiesVersion.ShouldNotBeNullOrWhiteSpace(
                "the preview must hand the operator a token to echo back");

            // The change the operator never saw.
            await WriteFlagAsync(enabled: true, ct);

            var response = await client.PostAsJsonAsync(
                "/api/operations/runs/rent/confirm",
                new
                {
                    year = 2026,
                    month = 8,
                    selectedTargetIds = preview.Rows.Select(r => r.TargetId).ToArray(),
                    capabilitiesVersion = preview.CapabilitiesVersion,
                },
                ct);

            response.StatusCode.ShouldBe(
                HttpStatusCode.Conflict,
                "a capability set that moved between preview and confirm must reject the confirm");

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            problem.GetProperty("code").GetString().ShouldBe("capabilities_changed");
            problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace(
                "the rejection must travel on the ADR-025 contract, not a hand-rolled shape");

            // Nothing posted: the guard runs before the strategy, and the throw rolls the request
            // transaction back. A rejected confirm that half-posted would be worse than no guard.
            (await RunCountAsync(setup.OrgId, ct)).ShouldBe(0);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The other half, and the one that stops the guard from being a blanket "always reject": an
    /// unchanged set must confirm. Without this a comparison hard-wired to false would pass the test
    /// above.
    /// </summary>
    [Fact]
    public async Task Confirm_with_the_current_version_succeeds()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var preview = await client.GetFromJsonAsync<RunPreviewSpaResponse>(
            "/api/operations/runs/rent/preview?year=2026&month=9", ct);
        preview.ShouldNotBeNull();
        preview.Rows.Count.ShouldBeGreaterThan(0, "the fixture org must have something to post");

        var response = await client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new
            {
                year = 2026,
                month = 9,
                selectedTargetIds = preview.Rows.Select(r => r.TargetId).ToArray(),
                capabilitiesVersion = preview.CapabilitiesVersion,
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var result = await response.Content.ReadFromJsonAsync<RunResultSpaResponse>(ct);
        result.ShouldNotBeNull();
        result.Posted.ShouldBe(preview.Rows.Count);
    }

    /// <summary>
    /// A token the server never issued is rejected on the same path. This is the direction a forged
    /// or garbled value takes, and it costs the caller a re-preview rather than anything worse.
    /// </summary>
    [Fact]
    public async Task Confirm_with_a_token_the_server_never_issued_is_rejected_with_409()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var preview = await client.GetFromJsonAsync<RunPreviewSpaResponse>(
            "/api/operations/runs/rent/preview?year=2026&month=10", ct);
        preview.ShouldNotBeNull();

        var response = await client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new
            {
                year = 2026,
                month = 10,
                selectedTargetIds = preview.Rows.Select(r => r.TargetId).ToArray(),
                capabilitiesVersion = "v1.this-token-was-never-issued",
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await RunCountAsync(setup.OrgId, ct)).ShouldBe(0);
    }

    /// <summary>
    /// A confirm that carries no token at all is a client that never previewed — or one built against
    /// the pre-guard contract. It is rejected as a malformed request (400) rather than silently
    /// skipping the check, which would make the guard opt-out by omission. Distinct from
    /// <c>capabilities_changed</c> on purpose: "your client is out of date" and "the platform state
    /// moved" are different problems with different fixes.
    /// </summary>
    [Fact]
    public async Task Confirm_without_a_capabilities_version_is_rejected_with_400()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var response = await client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new { year = 2026, month = 11, selectedTargetIds = Array.Empty<Guid>() },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        problem.GetProperty("code").GetString().ShouldBe("capabilities_version_required");
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Setup(Guid OrgId, string Email);

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Run Version Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"run-version-{orgId:N}@example.com";
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser
            {
                Id = UuidV7.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                OrgId = orgId,
                DisplayName = "Run Versioner",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsync(orgId, async () =>
            {
                var ownerId = await sender.Send(new CreateOwner("Version Owner", null, null, null, null, 0m), ct);
                var propId = await sender.Send(new CreateProperty(ownerId, "1 Version St", "Raleigh", "NC", null, null), ct);
                var unitId = await sender.Send(new CreateUnit(propId, "#1", 1200m, "occupied"), ct);
                var tenantId = await sender.Send(new CreateTenant("Version Tenant", null, null, "current"), ct);
                await sender.Send(new CreateLease(tenantId, unitId, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31), 1200m, 1200m, "active"), ct);
                await sender.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);
            }, ct);
        }

        return new Setup(orgId, email);
    }

    private async Task<HttpClient> LoggedInClientAsync(Setup setup, CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new { email = setup.Email, password = Password }, ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, "login must succeed before testing operations endpoints");
        await client.PrimeCsrfAsync(ct); // XSRF token rotates on sign-in
        return client;
    }

    private async Task GrantEntitlementAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var platform = scope.ServiceProvider.GetRequiredService<IPlatformScope>();
        var id = UuidV7.NewId();

        await platform.RunAsync(
            async () => await db.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO entitlements (id, org_id, capability, granted, effective_at, actor)
                 VALUES ({id}, {orgId}, {Capability}, true, now(), 'version-test')
                 """, ct),
            ct);
    }

    /// <summary>The flip, committed between the two HTTP calls — that is the race under test.</summary>
    private async Task WriteFlagAsync(bool enabled, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "SELECT set_config('app.platform', 'on', true)", ct);
        await ExecAsync(
            conn, tx,
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, @enabled, now(), 'version-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """, ct, ("name", Capability), ("enabled", enabled));

        await tx.CommitAsync(ct);
    }

    /// <summary>Restores the shared, global flag state, notifying so no sibling test inherits it.</summary>
    private async Task RemoveFlagAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await ExecAsync(conn, tx, "SELECT set_config('app.platform', 'on', true)", ct);
        await ExecAsync(conn, tx, "DELETE FROM feature_flags WHERE name = @name", ct, ("name", Capability));
        await ExecAsync(
            conn, tx, $"SELECT pg_notify('{CapabilityNotificationListener.Channel}', @name)", ct,
            ("name", Capability));

        await tx.CommitAsync(ct);
    }

    private async Task<int> RunCountAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();

        var count = -1;
        await executor.RunAsync(
            orgId,
            async () => count = await db.Set<Modules.Operations.Domain.BulkRun>().CountAsync(ct),
            ct);

        return count;
    }

    private static async Task<int> ExecAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string sql, CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync(ct);
    }
}
