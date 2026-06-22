using System.Net;
using System.Net.Http.Json;
using LeaseBook.Modules.Reporting.Delivery;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Integration.Fixtures;
using LeaseBook.Web.Auth;
using LeaseBook.Web.Reporting;
using LeaseBook.Web.Seeding;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Shouldly;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// WP-05 (M5): statement delivery seam — tie-out gate, happy-path artifact + record, and the
/// <c>POST /api/statements/{ownerId}/deliver</c> endpoint.
/// <list type="bullet">
/// <item>Unbalanced view → <see cref="StatementNotBalancedException"/> thrown, no record, no artifact.</item>
/// <item>Balanced view → <see cref="DeliveryState.Queued"/> record created, artifact retrievable.</item>
/// <item>Endpoint 409 on unbalanced + 200 on balanced + 401 for anon.</item>
/// <item><see cref="SchemaGuardTests"/> passes (migration applied + EnableOrgRls called).</item>
/// </list>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class StatementDeliveryTests(PostgresFixture fixture)
{
    // ─── IArtifactStore round-trip (unit-style, no HTTP) ─────────────────────

    [Fact]
    public async Task ArtifactStore_put_then_get_returns_equal_bytes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = fixture.Api.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        var original = new byte[] { 0x25, 0x50, 0x44, 0x46, 0x01, 0x02, 0x03 }; // fake "%PDF..."
        var key = $"test-{Guid.NewGuid():N}.pdf";

        await store.PutAsync(original, key, ct);
        var retrieved = await store.GetAsync(key, ct);

        retrieved.ShouldNotBeNull("artifact should be retrievable after put");
        retrieved!.ShouldBe(original, "retrieved bytes must equal stored bytes");
    }

    [Fact]
    public async Task ArtifactStore_get_for_missing_key_returns_null()
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = fixture.Api.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        var result = await store.GetAsync($"nonexistent-{Guid.NewGuid():N}.pdf", ct);
        result.ShouldBeNull();
    }

    // ─── IStatementDelivery — unit-style (direct service, no HTTP) ──────────

    /// <summary>
    /// TDD tie-out gate test (spec §4.1 — "non-zero variance blocks issuance").
    /// An unbalanced view must throw <see cref="StatementNotBalancedException"/> before any
    /// artifact is written or any delivery record is created.
    /// <para>
    /// Empirical no-write probe: after the throw, a raw SQL COUNT on <c>statement_deliveries</c>
    /// (app role, SET LOCAL app.org_id) must return 0 for the unique owner id used in this test.
    /// This test will FAIL if a future edit adds a speculative DB write before the gate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Delivery_unbalanced_view_throws_and_writes_no_record_or_artifact()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        using var scope = fixture.Api.Services.CreateScope();
        var delivery = scope.ServiceProvider.GetRequiredService<IStatementDelivery>();

        // Use a unique owner id so the COUNT probe is unambiguous — no other test can produce a
        // row for this id (even if the gate regresses in a concurrent test run, uniqueness isolates).
        var uniqueOwnerId = Guid.NewGuid();
        var unbalancedView = BuildUnbalancedViewWithOwner(uniqueOwnerId);

        // Should throw — no side effects.
        var ex = await Should.ThrowAsync<StatementNotBalancedException>(async () =>
            await delivery.DeliverAsync(unbalancedView, "owner@example.com", ct));

        ex.OwnerId.ShouldBe(uniqueOwnerId);
        ex.Variance.ShouldBe(100.00m);

        // ── Empirical no-write probe ──────────────────────────────────────────────
        // Resolve the demo org id (needed to satisfy the RLS policy on statement_deliveries —
        // FORCE ROW LEVEL SECURITY applies even to the migrator/owner role, so the app role
        // connection must set app.org_id via SET LOCAL before any SELECT will return rows).
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        var adminUser = migratorDb.Users.FirstOrDefault(u => u.Email == DemoSeeder.AdminEmail);
        adminUser.ShouldNotBeNull("demo org must be seeded before the probe");
        var orgId = adminUser!.OrgId;

        // Open an app-role connection (subject to RLS) and set the org context, exactly as the
        // happy-path test does — same probe pattern.
        await using var probeConn = new NpgsqlConnection(fixture.AppConnectionString);
        await probeConn.OpenAsync(ct);
        await using var tx = await probeConn.BeginTransactionAsync(ct);
        await using var setOrgCmd = new NpgsqlCommand(
            "SELECT set_config('app.org_id', @org, true)", probeConn, tx);
        setOrgCmd.Parameters.AddWithValue("org", orgId.ToString());
        await setOrgCmd.ExecuteNonQueryAsync(ct);

        // COUNT(*) for the unique owner id — must be 0: the gate threw before any DB write.
        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM statement_deliveries WHERE owner_id = @ownerId",
            probeConn, tx);
        countCmd.Parameters.AddWithValue("ownerId", uniqueOwnerId);
        var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;
        await tx.RollbackAsync(ct);

        count.ShouldBe(0L,
            "tie-out gate must not write any delivery record before throwing; " +
            $"found {count} row(s) for uniqueOwnerId={uniqueOwnerId}");
    }

    /// <summary>
    /// TDD happy-path test: a balanced view produces a Queued delivery record and a retrievable artifact.
    /// </summary>
    [Fact]
    public async Task Delivery_balanced_view_creates_queued_record_and_retrievable_artifact()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        // Use a fresh scope so tenant context is wired up through the DI chain.
        // The OrgContextMiddleware normally sets this for HTTP requests; for direct service
        // calls we drive the org scope manually via OrgScopedExecutor (which issues SET LOCAL
        // app.org_id, the same mechanism the middleware uses for HTTP requests).
        using var scope = fixture.Api.Services.CreateScope();
        var tenantContext = scope.ServiceProvider.GetRequiredService<LeaseBook.SharedKernel.Tenancy.TenantContext>();

        // Resolve the demo org id from the seeded admin user via migrator connection (bypasses RLS).
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        var adminUser = migratorDb.Users.FirstOrDefault(u => u.Email == DemoSeeder.AdminEmail);
        adminUser.ShouldNotBeNull("demo admin user must be seeded");
        var orgId = adminUser!.OrgId;

        // Set the in-process tenant context (EF global filter).
        tenantContext.OrgId = orgId;

        var executor = scope.ServiceProvider.GetRequiredService<LeaseBook.SharedKernel.Tenancy.OrgScopedExecutor>();
        var delivery = scope.ServiceProvider.GetRequiredService<IStatementDelivery>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        DeliveryResult? result = null;

        // Run delivery inside an org-scoped executor so SET LOCAL app.org_id is active for the
        // SaveChangesAsync call inside LocalStatementDelivery (RLS requires app.org_id in transaction).
        await executor.RunAsync(orgId, async () =>
        {
            var balancedView = BuildBalancedView();
            result = await delivery.DeliverAsync(balancedView, "owner@example.com", ct);
        }, ct);

        result.ShouldNotBeNull("delivery should have returned a result");
        result!.State.ShouldBe(DeliveryState.Queued, "new delivery is always Queued");
        result.Id.ShouldNotBe(Guid.Empty);

        // Read back the delivery record via the app role connection with org context set (RLS
        // applies via FORCE ROW LEVEL SECURITY even to the migrator/owner role, so we must set
        // app.org_id in a transaction, just like the app does at runtime).
        await using var probeConn = new NpgsqlConnection(fixture.AppConnectionString);
        await probeConn.OpenAsync(ct);
        await using var tx = await probeConn.BeginTransactionAsync(ct);
        // SET LOCAL app.org_id so the RLS policy on statement_deliveries allows the SELECT.
        await using var setOrgCmd = new NpgsqlCommand(
            "SELECT set_config('app.org_id', @org, true)", probeConn, tx);
        setOrgCmd.Parameters.AddWithValue("org", orgId.ToString());
        await setOrgCmd.ExecuteNonQueryAsync(ct);

        await using var selectCmd = new NpgsqlCommand(
            "SELECT id, state, to_email, owner_id, period_year, period_month, artifact_key " +
            "FROM statement_deliveries WHERE id = @id",
            probeConn, tx);
        selectCmd.Parameters.AddWithValue("id", result.Id);
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        (await reader.ReadAsync(ct)).ShouldBeTrue("delivery record must be persisted");
        reader.GetGuid(0).ShouldBe(result.Id);
        reader.GetString(1).ShouldBe("queued");
        reader.GetString(2).ShouldBe("owner@example.com");
        reader.GetGuid(3).ShouldBe(DemoIds.O5);
        reader.GetInt32(4).ShouldBe(2026);
        reader.GetInt32(5).ShouldBe(5);
        var artifactKey = reader.GetString(6);
        artifactKey.ShouldNotBeNullOrEmpty();
        await reader.DisposeAsync();
        await tx.RollbackAsync(ct); // read-only probe — roll back so nothing persists

        // Artifact must be retrievable (file system — outside the DB/RLS boundary).
        var artifactBytes = await store.GetAsync(artifactKey, ct);
        artifactBytes.ShouldNotBeNull("artifact must be retrievable by key");
        artifactBytes!.Length.ShouldBeGreaterThan(8_000, "PDF artifact must be non-trivially sized");

        // PDF magic bytes.
        artifactBytes[0].ShouldBe((byte)'%');
        artifactBytes[1].ShouldBe((byte)'P');
        artifactBytes[2].ShouldBe((byte)'D');
        artifactBytes[3].ShouldBe((byte)'F');
    }

    // ─── POST /api/statements/{ownerId}/deliver (HTTP) ───────────────────────

    [Fact]
    public async Task Deliver_endpoint_balanced_statement_returns_200_with_queued_state()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        // O5 May 2026 is balanced (Fiduciary.Balanced = true per WP-3 golden test).
        var url = $"/api/statements/{DemoIds.O5}/deliver" +
                  "?year=2026&month=5&basis=cash&toEmail=owner%40example.com";
        var response = await client.PostAsync(url, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK,
            "balanced statement deliver should return 200: " +
            await response.Content.ReadAsStringAsync(ct));

        var result = await response.Content.ReadFromJsonAsync<DeliveryResult>(ct);
        result.ShouldNotBeNull();
        result!.State.ShouldBe(DeliveryState.Queued);
        result.Id.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Deliver_endpoint_missing_toEmail_returns_400()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);
        var client = await DemoClientAsync(ct);

        var url = $"/api/statements/{DemoIds.O5}/deliver?year=2026&month=5&basis=cash";
        var response = await client.PostAsync(url, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Deliver_endpoint_requires_auth()
    {
        var ct = TestContext.Current.CancellationToken;
        // Prime CSRF so the antiforgery check passes; the request should still be rejected at
        // the authorization layer (401) because no login occurred.
        var anonClient = fixture.Api.CreateClient();
        await anonClient.PrimeCsrfAsync(ct);
        var url = $"/api/statements/{DemoIds.O5}/deliver" +
                  "?year=2026&month=5&basis=cash&toEmail=owner%40example.com";
        var response = await anonClient.PostAsync(url, null, ct);
        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    /// <summary>
    /// Exercises the endpoint's <c>catch (StatementNotBalancedException) → 409</c> mapping.
    /// <para>
    /// With real seed data the assembler always produces a balanced view (all golden figures
    /// reconcile), so 409 is unreachable through a real data path. A WebApplicationFactory
    /// <c>WithWebHostBuilder</c> override replaces <see cref="IStatementDelivery"/> with a fake
    /// that unconditionally throws <see cref="StatementNotBalancedException"/>, driving the
    /// endpoint's catch block empirically through the full HTTP stack.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Deliver_endpoint_unbalanced_statement_returns_409()
    {
        var ct = TestContext.Current.CancellationToken;
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        // Override IStatementDelivery for this test only: throw the exception the endpoint maps
        // to 409. WithWebHostBuilder creates an isolated factory that does not affect other tests.
        var throwingFactory = fixture.Api.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IStatementDelivery>();
                services.AddScoped<IStatementDelivery>(_ =>
                    new ThrowingStatementDelivery(DemoIds.O5, 2026, 5, 42.50m));
            });
        });

        var client = throwingFactory.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK, "login must succeed to reach the deliver endpoint");
        await client.PrimeCsrfAsync(ct);

        var url = $"/api/statements/{DemoIds.O5}/deliver" +
                  "?year=2026&month=5&basis=cash&toEmail=owner%40example.com";
        var response = await client.PostAsync(url, null, ct);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict,
            "deliver endpoint must return 409 when IStatementDelivery throws StatementNotBalancedException: " +
            await response.Content.ReadAsStringAsync(ct));

        // Verify the problem body carries the expected discriminator code so callers can
        // distinguish this 409 from other conflict responses.
        var body = await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(ct);
        body.GetProperty("title").GetString().ShouldBe("statement_not_balanced",
            "409 problem title must be 'statement_not_balanced'");
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private async Task<HttpClient> DemoClientAsync(CancellationToken ct)
    {
        var client = fixture.Api.CreateClient();
        await client.PrimeCsrfAsync(ct);
        var login = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest(DemoSeeder.AdminEmail, DemoSeeder.AdminPassword), ct);
        login.StatusCode.ShouldBe(HttpStatusCode.OK);
        await client.PrimeCsrfAsync(ct);
        return client;
    }

    /// <summary>
    /// Builds a <see cref="StatementView"/> where <c>Fiduciary.Balanced</c> is false, so the
    /// tie-out gate rejects delivery. Variance is non-zero.
    /// </summary>
    private static StatementView BuildUnbalancedView() =>
        BuildUnbalancedViewWithOwner(DemoIds.O5);

    private static StatementView BuildUnbalancedViewWithOwner(Guid ownerId)
    {
        var branding = new LeaseBook.Modules.Reporting.Contracts.PmBrandingRow(
            CompanyName: "Test PM",
            LogoBlobRef: null,
            ParenthesizedNegatives: false);

        var fiduciary = new FiduciaryPanel(
            Balanced: false,
            Variance: 100.00m,    // non-zero variance — triggers the tie-out gate
            PmIncomeExcluded: true,
            DepositsRecognizedOnApplication: true,
            LatestReconciledBank: null);

        return new StatementView(
            OwnerId: ownerId,
            OwnerName: "Test Owner",
            PropertyAddress: null,
            Basis: "cash",
            Year: 2026,
            Month: 5,
            Beginning: 100m,
            Sections: [],
            Ending: 200m,  // deliberately inconsistent — variance != 0
            Fiduciary: fiduciary,
            Branding: branding);
    }

    /// <summary>
    /// Builds a <see cref="StatementView"/> where <c>Fiduciary.Balanced</c> is true (O5 May 2026
    /// golden figures). Passes the tie-out gate so delivery proceeds.
    /// </summary>
    private static StatementView BuildBalancedView()
    {
        var branding = new LeaseBook.Modules.Reporting.Contracts.PmBrandingRow(
            CompanyName: "Harbour Front PM",
            LogoBlobRef: null,
            ParenthesizedNegatives: false);

        var incomeLines = new List<StatementLineView>
        {
            new(Guid.NewGuid(), new DateOnly(2026, 5, 1), "RentCharged", null,
                "May rent — 204 Elm St", "204 Elm St", 1_950.00m),
            new(Guid.NewGuid(), new DateOnly(2026, 5, 15), "DepositApplied", null,
                "Applied deposit — Okonkwo", "204 Elm St", 950.00m),
        };

        var sections = new List<StatementSectionView>
        {
            new("income", "Income — rent collected", incomeLines, 2_900.00m),
        };

        var fiduciary = new FiduciaryPanel(
            Balanced: true,
            Variance: 0m,
            PmIncomeExcluded: true,
            DepositsRecognizedOnApplication: true,
            LatestReconciledBank: null);

        return new StatementView(
            OwnerId: DemoIds.O5,
            OwnerName: "Ridgeline Investments",
            PropertyAddress: "204 Elm St, Chapel Hill, NC",
            Basis: "cash",
            Year: 2026,
            Month: 5,
            Beginning: 20_345.30m,
            Sections: sections,
            Ending: 22_640.30m,
            Fiduciary: fiduciary,
            Branding: branding);
    }

    /// <summary>
    /// Test double for <see cref="IStatementDelivery"/> used by
    /// <see cref="Deliver_endpoint_unbalanced_statement_returns_409"/>.
    /// Unconditionally throws <see cref="StatementNotBalancedException"/> so the endpoint's
    /// exception→409 mapping is exercised through the full HTTP stack without requiring seed data
    /// that happens to be unbalanced (the assembler always produces a balanced view for valid owners).
    /// </summary>
    private sealed class ThrowingStatementDelivery(
        Guid ownerId, int year, int month, decimal variance) : IStatementDelivery
    {
        public Task<DeliveryResult> DeliverAsync(StatementView view, string toEmail, CancellationToken ct)
            => throw new StatementNotBalancedException(ownerId, year, month, variance);
    }
}
