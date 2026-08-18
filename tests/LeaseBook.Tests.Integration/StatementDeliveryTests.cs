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
/// WP-05 (M5), reshaped by ADR-040: the statement delivery seam — tie-out gate, the artifact /
/// attempt / event history it writes, and the <c>POST /api/statements/{ownerId}/deliver</c> endpoint.
/// <list type="bullet">
/// <item>Unbalanced view → <see cref="StatementNotBalancedException"/> thrown, no rows, no artifact.</item>
/// <item>Balanced view → artifact + attempt + <see cref="DeliveryEventKind.Queued"/> event, PDF retrievable.</item>
/// <item>Acceptance followed by a bounce keeps both facts and reports the bounce (#185's scenario).</item>
/// <item>A retry is a new attempt against the <b>same</b> artifact, never a new event on the old one.</item>
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
    /// Empirical no-write probe: after the throw, a raw SQL COUNT on <c>statement_artifacts</c>
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
        // Resolve the demo org id (needed to satisfy the RLS policy on statement_artifacts —
        // FORCE ROW LEVEL SECURITY applies even to the migrator/owner role, so the app role
        // connection must set app.org_id via SET LOCAL before any SELECT will return rows).
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        var adminUser = migratorDb.Users.FirstOrDefault(u => u.Email == DemoSeeder.AdminEmail);
        adminUser.ShouldNotBeNull("demo org must be seeded before the probe");
        var orgId = adminUser!.OrgId;

        // Open an app-role connection (subject to RLS) and set the organization context, exactly as the
        // happy-path test does — same probe pattern.
        await using var probeConn = new NpgsqlConnection(fixture.AppConnectionString);
        await probeConn.OpenAsync(ct);
        await using var tx = await probeConn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(probeConn, tx, orgId, ct);

        // COUNT(*) for the unique owner id — must be 0: the gate threw before any DB write.
        await using var countCmd = new NpgsqlCommand(
            "SELECT COUNT(*) FROM statement_artifacts WHERE owner_id = @ownerId",
            probeConn, tx);
        countCmd.Parameters.AddWithValue("ownerId", uniqueOwnerId);
        var count = (long)(await countCmd.ExecuteScalarAsync(ct))!;
        await tx.RollbackAsync(ct);

        count.ShouldBe(0L,
            "tie-out gate must not write any artifact row before throwing; " +
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

        // Use a fresh scope so organization context is wired up through the DI chain.
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

        // Set the in-process organization context (EF global filter).
        tenantContext.OrgId = orgId;

        var executor = scope.ServiceProvider.GetRequiredService<LeaseBook.SharedKernel.Tenancy.OrgScopedExecutor>();
        var delivery = scope.ServiceProvider.GetRequiredService<IStatementDelivery>();
        var store = scope.ServiceProvider.GetRequiredService<IArtifactStore>();

        DeliveryAttemptResult? result = null;

        // Run delivery inside an org-scoped executor so SET LOCAL app.org_id is active for the
        // SaveChangesAsync call inside LocalStatementDelivery (RLS requires app.org_id in transaction).
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            var balancedView = BuildBalancedView();
            result = await delivery.DeliverAsync(balancedView, "owner@example.com", ct);
        }, ct);

        result.ShouldNotBeNull("delivery should have returned a result");
        result!.Status.ShouldBe(DeliveryEventKind.Queued, "a new attempt is always Queued");
        result.ArtifactId.ShouldNotBe(Guid.Empty);
        result.AttemptId.ShouldNotBe(Guid.Empty);

        // Read back the three rows via the app role connection with organization context set (RLS
        // applies via FORCE ROW LEVEL SECURITY even to the migrator/owner role, so we must set
        // app.org_id in a transaction, just like the app does at runtime).
        await using var probeConn = new NpgsqlConnection(fixture.AppConnectionString);
        await probeConn.OpenAsync(ct);
        await using var tx = await probeConn.BeginTransactionAsync(ct);
        // SET LOCAL app.org_id so the RLS policies on the delivery tables allow the SELECT.
        await RlsProbe.SetOrgAsync(probeConn, tx, orgId, ct);

        await using var selectCmd = new NpgsqlCommand(
            """
            SELECT art.owner_id, art.period_year, art.period_month, art.basis, art.artifact_key,
                   att.id, att.to_email, ev.sequence, ev.kind
            FROM statement_artifacts art
            JOIN statement_delivery_attempts att
              ON att.org_id = art.org_id AND att.artifact_id = art.id
            JOIN statement_delivery_events ev
              ON ev.org_id = att.org_id AND ev.attempt_id = att.id
            WHERE art.id = @id
            ORDER BY ev.sequence
            """,
            probeConn, tx);
        selectCmd.Parameters.AddWithValue("id", result.ArtifactId);
        await using var reader = await selectCmd.ExecuteReaderAsync(ct);

        (await reader.ReadAsync(ct)).ShouldBeTrue("artifact + attempt + queued event must be persisted");
        reader.GetGuid(0).ShouldBe(DemoIds.O5);
        reader.GetInt32(1).ShouldBe(2026);
        reader.GetInt32(2).ShouldBe(5);
        reader.GetString(3).ShouldBe("cash", "the artifact records which rendering it is");
        var artifactKey = reader.GetString(4);
        artifactKey.ShouldNotBeNullOrEmpty();
        reader.GetGuid(5).ShouldBe(result.AttemptId);
        reader.GetString(6).ShouldBe("owner@example.com");
        reader.GetInt32(7).ShouldBe(1, "the Queued event is always sequence 1");
        reader.GetString(8).ShouldBe("queued");
        (await reader.ReadAsync(ct)).ShouldBeFalse("a fresh attempt has exactly one event");
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

    // ─── ADR-040: the history model (#185's two scenarios) ──────────────────

    /// <summary>
    /// #185's concrete scenario: the provider accepts the message and the recipient's server then
    /// bounces it. Both facts must survive and the attempt must report the bounce.
    /// <para>
    /// This is the test the old model could not pass. It had one <c>state</c> column, so recording
    /// the bounce meant overwriting <c>sent</c> — erasing the acceptance — and the row had said
    /// "sent" in the meantime, which read as success when nothing had reached the owner.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Acceptance_then_bounce_keeps_both_facts_and_the_attempt_reports_bounced()
    {
        var ct = TestContext.Current.CancellationToken;

        var (orgId, outcome) = await RunInOrgAsync(async delivery =>
        {
            var queued = await delivery.DeliverAsync(BuildBalancedView(), "owner@example.com", ct);

            var accepted = await delivery.RecordEventAsync(
                queued.AttemptId, DeliveryEventKind.Accepted,
                new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc), "provider-msg-1", null, ct);
            accepted.Status.ShouldBe(DeliveryEventKind.Accepted,
                "provider acceptance is its own status, never 'sent'");

            var bounced = await delivery.RecordEventAsync(
                queued.AttemptId, DeliveryEventKind.Bounced,
                new DateTime(2026, 6, 1, 12, 4, 0, DateTimeKind.Utc), "provider-msg-1",
                "550 5.1.1 mailbox unavailable", ct);

            return (queued.AttemptId, bounced.Status);
        }, ct);

        outcome.Status.ShouldBe(DeliveryEventKind.Bounced,
            "the latest recorded event decides the current status");

        var history = await ReadHistoryAsync(orgId, outcome.AttemptId, ct);

        history.Select(e => e.Kind).ShouldBe(["queued", "accepted", "bounced"]);
        history.Select(e => e.Sequence).ShouldBe([1, 2, 3]);

        // The acceptance is still there — the whole point. An auditor can see that the provider took
        // the message and that it still never reached the owner.
        history[1].ProviderMessageId.ShouldBe("provider-msg-1");
        history[2].Detail.ShouldBe("550 5.1.1 mailbox unavailable");
    }

    /// <summary>
    /// A retry is a new attempt against the <b>same</b> artifact: the owner is sent the identical
    /// PDF, and the bounced attempt keeps its own history rather than being reopened.
    /// </summary>
    [Fact]
    public async Task Retry_opens_a_new_attempt_against_the_same_artifact()
    {
        var ct = TestContext.Current.CancellationToken;

        var (orgId, outcome) = await RunInOrgAsync(async delivery =>
        {
            var first = await delivery.DeliverAsync(BuildBalancedView(), "typo@example.com", ct);
            await delivery.RecordEventAsync(
                first.AttemptId, DeliveryEventKind.Bounced,
                new DateTime(2026, 6, 1, 12, 4, 0, DateTimeKind.Utc), "provider-msg-1",
                "550 5.1.1 mailbox unavailable", ct);

            var retry = await delivery.RetryAsync(first.ArtifactId, "owner@example.com", ct);
            return (First: first, Retry: retry);
        }, ct);

        outcome.Retry.ArtifactId.ShouldBe(outcome.First.ArtifactId,
            "a retry sends the artifact that was already issued — it is not re-rendered");
        outcome.Retry.AttemptId.ShouldNotBe(outcome.First.AttemptId,
            "a retry is a new attempt, not a reopened one");
        outcome.Retry.Status.ShouldBe(DeliveryEventKind.Queued, "the new attempt starts queued");

        // Exactly one artifact, two attempts against it, oldest first.
        var attempts = await ReadAttemptsAsync(orgId, outcome.First.ArtifactId, ct);
        attempts.Count.ShouldBe(2);
        attempts[0].ShouldBe(("typo@example.com", outcome.First.AttemptId));
        attempts[1].ShouldBe(("owner@example.com", outcome.Retry.AttemptId));

        // The bounced attempt is untouched: queued → bounced, with nothing appended by the retry.
        var bouncedHistory = await ReadHistoryAsync(orgId, outcome.First.AttemptId, ct);
        bouncedHistory.Select(e => e.Kind).ShouldBe(["queued", "bounced"]);

        var retryHistory = await ReadHistoryAsync(orgId, outcome.Retry.AttemptId, ct);
        retryHistory.Select(e => e.Kind).ShouldBe(["queued"]);
        retryHistory[0].Sequence.ShouldBe(1, "each attempt numbers its own events from 1");
    }

    /// <summary>
    /// An attempt is queued exactly once, when it is opened. A second <c>Queued</c> event would make
    /// a retry indistinguishable from a status change — the ambiguity #185 exists to remove — so the
    /// seam refuses it and points at <c>RetryAsync</c>.
    /// </summary>
    [Fact]
    public async Task Recording_a_second_queued_event_is_refused()
    {
        var ct = TestContext.Current.CancellationToken;

        var (_, thrown) = await RunInOrgAsync(async delivery =>
        {
            var queued = await delivery.DeliverAsync(BuildBalancedView(), "owner@example.com", ct);

            return await Should.ThrowAsync<InvalidOperationException>(async () =>
                await delivery.RecordEventAsync(
                    queued.AttemptId, DeliveryEventKind.Queued, DateTime.UtcNow, null, null, ct));
        }, ct);

        thrown.Message.ShouldContain("RetryAsync");
    }

    /// <summary>
    /// The projection orders on the recorded sequence, not the reporter's clock. A provider webhook
    /// that arrives late carries an earlier <c>OccurredAt</c>, and ordering on that column would let a
    /// stale acceptance callback overwrite a bounce that was already recorded.
    /// </summary>
    [Fact]
    public void Status_is_the_highest_sequence_event_not_the_latest_timestamp()
    {
        var attemptId = Guid.NewGuid();
        List<StatementDeliveryEvent> events =
        [
            new()
            {
                AttemptId = attemptId, Sequence = 1, Kind = DeliveryEventKind.Queued,
                OccurredAt = new DateTime(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc),
            },
            new()
            {
                AttemptId = attemptId, Sequence = 2, Kind = DeliveryEventKind.Bounced,
                OccurredAt = new DateTime(2026, 6, 1, 12, 9, 0, DateTimeKind.Utc),
            },
            new()
            {
                // Recorded last, but the provider stamped it before the bounce above.
                AttemptId = attemptId, Sequence = 3, Kind = DeliveryEventKind.Accepted,
                OccurredAt = new DateTime(2026, 6, 1, 12, 1, 0, DateTimeKind.Utc),
            },
        ];

        DeliveryStatus.Of(events, attemptId).ShouldBe(DeliveryEventKind.Accepted);
    }

    [Fact]
    public void Status_of_an_attempt_with_no_loaded_events_throws_rather_than_reporting_queued()
    {
        var attempt = new StatementDeliveryAttempt { Id = Guid.NewGuid() };

        var ex = Should.Throw<InvalidOperationException>(() => DeliveryStatus.Of(attempt));
        ex.Message.ShouldContain("was not loaded");
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

        var result = await response.Content.ReadFromJsonAsync<DeliveryAttemptResult>(ct);
        result.ShouldNotBeNull();
        result!.Status.ShouldBe(DeliveryEventKind.Queued);
        result.ArtifactId.ShouldNotBe(Guid.Empty);
        result.AttemptId.ShouldNotBe(Guid.Empty);
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

    /// <summary>
    /// Seeds demo, resolves its org id, and runs <paramref name="work"/> against the real
    /// <see cref="IStatementDelivery"/> inside an <c>OrgScopedExecutor</c> transaction — the same
    /// <c>SET LOCAL app.org_id</c> mechanism <c>OrgContextMiddleware</c> gives an HTTP request, which
    /// the RLS policies on the delivery tables require before any write.
    /// </summary>
    private async Task<(Guid OrgId, T Result)> RunInOrgAsync<T>(
        Func<IStatementDelivery, Task<T>> work, CancellationToken ct)
    {
        await DemoSeeder.SeedAsync(fixture.Api.Services, ct);

        using var scope = fixture.Api.Services.CreateScope();
        var tenantContext = scope.ServiceProvider
            .GetRequiredService<LeaseBook.SharedKernel.Tenancy.TenantContext>();

        // Resolve the demo org id via the migrator connection (bypasses the EF filter, not RLS).
        await using var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString);
        var adminUser = migratorDb.Users.FirstOrDefault(u => u.Email == DemoSeeder.AdminEmail);
        adminUser.ShouldNotBeNull("demo admin user must be seeded");
        var orgId = adminUser!.OrgId;

        tenantContext.OrgId = orgId;

        var executor = scope.ServiceProvider
            .GetRequiredService<LeaseBook.SharedKernel.Tenancy.OrgScopedExecutor>();
        var delivery = scope.ServiceProvider.GetRequiredService<IStatementDelivery>();

        T result = default!;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
        {
            result = await work(delivery);
        }, ct);

        return (orgId, result);
    }

    /// <summary>
    /// One attempt's persisted event history, oldest first, read back through the <b>app</b> role with
    /// org context set — so these assertions also prove the rows are reachable under RLS rather than
    /// only present in the change tracker.
    /// </summary>
    private async Task<IReadOnlyList<(int Sequence, string Kind, string? ProviderMessageId, string? Detail)>>
        ReadHistoryAsync(Guid orgId, Guid attemptId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT sequence, kind, provider_message_id, detail FROM statement_delivery_events " +
            "WHERE attempt_id = @attemptId ORDER BY sequence",
            conn, tx);
        cmd.Parameters.AddWithValue("attemptId", attemptId);

        var rows = new List<(int, string, string?, string?)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((
                    reader.GetInt32(0),
                    reader.GetString(1),
                    await reader.IsDBNullAsync(2, ct) ? null : reader.GetString(2),
                    await reader.IsDBNullAsync(3, ct) ? null : reader.GetString(3)));
            }
        }

        await tx.RollbackAsync(ct);
        return rows;
    }

    /// <summary>Every attempt against one artifact, oldest first.</summary>
    private async Task<IReadOnlyList<(string ToEmail, Guid Id)>> ReadAttemptsAsync(
        Guid orgId, Guid artifactId, CancellationToken ct)
    {
        await using var conn = new NpgsqlConnection(fixture.AppConnectionString);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await RlsProbe.SetOrgAsync(conn, tx, orgId, ct);

        await using var cmd = new NpgsqlCommand(
            "SELECT to_email, id FROM statement_delivery_attempts " +
            "WHERE artifact_id = @artifactId ORDER BY created_at, id",
            conn, tx);
        cmd.Parameters.AddWithValue("artifactId", artifactId);

        var rows = new List<(string, Guid)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetGuid(1)));
            }
        }

        await tx.RollbackAsync(ct);
        return rows;
    }

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
        public Task<DeliveryAttemptResult> DeliverAsync(
            StatementView view, string toEmail, CancellationToken ct)
            => throw new StatementNotBalancedException(ownerId, year, month, variance);

        public Task<DeliveryAttemptResult> RetryAsync(
            Guid artifactId, string toEmail, CancellationToken ct)
            => throw new NotSupportedException("The 409-mapping double only exercises DeliverAsync.");

        public Task<DeliveryAttemptResult> RecordEventAsync(
            Guid attemptId, DeliveryEventKind kind, DateTime occurredAt,
            string? providerMessageId, string? detail, CancellationToken ct)
            => throw new NotSupportedException("The 409-mapping double only exercises DeliverAsync.");
    }
}
