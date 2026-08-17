using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Capabilities.Caching;
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
/// The CROSS-RUN window (ADR-028 / Task 11). The freeze (<see cref="RunCapabilityFreezeTests"/>) makes
/// one run internally consistent, and the preview guard (<see cref="RunCapabilityVersionTests"/>) makes
/// it the run the operator authorized. Neither says anything about a period built by MORE THAN ONE
/// run — which is the normal case, because the designed recovery path is a re-run: source_ref
/// uniqueness lands already-posted items as Skipped (ADR-019 §2).
/// <para>
/// So: run 1 confirms a selection while a money-path capability is off, the flag flips, run 2 confirms
/// the remainder while it is on. Both runs are internally consistent. The period is not. Recording the
/// state in summary_json explains that afterwards; only refusing run 2 prevents it.
/// </para>
/// <para>
/// <b>Why the fixture capability.</b> The guard compares the MONEY-PATH subset of a resolved set, not
/// the whole version token — a token comparison would reject the recovery re-run every time a deploy
/// added an unrelated capability or an org was granted a paid one. That makes a money-path capability
/// the precondition for testing it at all, and the registry's real MoneyPathFixture is the only thing
/// that can move the recorded state: a Capability built inside a test never enters the resolver, so
/// both sides would compare an empty subset and the suite would pass vacuously.
/// </para>
/// <para>
/// <b>Test-isolation hazard.</b> feature_flags is global — no org_id — and this assembly shares one
/// <see cref="PostgresFixture"/> through <see cref="DatabaseCollection"/>, so every flag mutation is
/// undone in a finally that also notifies. The org-scoped half needs no cleanup: each test mints its
/// own org.
/// </para>
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RunCapabilityPeriodTests(PostgresFixture fixture)
{
    private const string Password = "Tarheel-Trust-2026!";

    /// <summary>
    /// The registry constant, never a literal. RunCapabilities.IsEnabled throws on a name it never
    /// resolved, so a stale literal here would be an exception on the money path rather than a quiet
    /// "off" — and this suite is the last place that should keep its own copy of the string it pivots
    /// on.
    /// </summary>
    private static readonly string Capability = CapabilityCatalog.MoneyPathFixture.Name;

    [Fact]
    public async Task Same_period_lock_serializes_two_real_transactions_without_blocking_another_period()
    {
        var ct = TestContext.Current.CancellationToken;
        var org = UuidV7.NewId();
        var period = new RunPeriod(2026, 10);

        await using var firstScope = fixture.Api.Services.CreateAsyncScope();
        await using var secondScope = fixture.Api.Services.CreateAsyncScope();
        await using var otherPeriodScope = fixture.Api.Services.CreateAsyncScope();

        var firstExecutor = firstScope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var secondExecutor = secondScope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var otherExecutor = otherPeriodScope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
        var firstLock = new RunPeriodLock(firstScope.ServiceProvider.GetRequiredService<DbContext>());
        var secondLock = new RunPeriodLock(secondScope.ServiceProvider.GetRequiredService<DbContext>());
        var otherLock = new RunPeriodLock(otherPeriodScope.ServiceProvider.GetRequiredService<DbContext>());

        var firstAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondAcquired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = firstExecutor.RunAsSystemAsync(
            org, "test-harness",
            async () =>
            {
                await firstLock.AcquireAsync(RunType.Rent, period, ct);
                firstAcquired.SetResult();
                await releaseFirst.Task.WaitAsync(ct);
            },
            ct);

        await firstAcquired.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

        var second = secondExecutor.RunAsSystemAsync(
            org, "test-harness",
            async () =>
            {
                await secondLock.AcquireAsync(RunType.Rent, period, ct);
                secondAcquired.SetResult();
            },
            ct);

        try
        {
            await Should.ThrowAsync<TimeoutException>(
                async () => await secondAcquired.Task.WaitAsync(TimeSpan.FromMilliseconds(500)));

            await otherExecutor.RunAsSystemAsync(
                org, "test-harness",
                async () => await otherLock.AcquireAsync(RunType.Rent, new RunPeriod(2026, 11), ct),
                ct).WaitAsync(TimeSpan.FromSeconds(5), ct);
        }
        finally
        {
            releaseFirst.TrySetResult();
        }

        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), ct);
        secondAcquired.Task.IsCompletedSuccessfully.ShouldBeTrue();
    }

    /// <summary>
    /// The whole point of the task. The flip is a real capability change, not a fabricated summary
    /// row: it proves the recorded money-path state actually MOVES when the resolved set moves.
    /// </summary>
    [Fact]
    public async Task A_second_run_for_the_same_period_under_changed_money_path_state_is_rejected()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        try
        {
            // Run 1: a SUBSET of the period's targets, under the resting (off) state.
            var first = await PreviewAsync(client, 2026, 10, ct);
            first.Rows.Count.ShouldBeGreaterThan(1, "the period needs a remainder for run 2 to post");

            var firstResponse = await ConfirmAsync(
                client, 2026, 10, [first.Rows[0].TargetId], first.CapabilitiesVersion, ct);
            firstResponse.StatusCode.ShouldBe(HttpStatusCode.OK);

            // The flag flips BETWEEN the two runs — committed, so the second confirm resolves it.
            await WriteFlagAsync(enabled: true, ct);

            // Run 2: the remainder. Run 1's item skips on source_ref; the rest would post under the
            // new state, computing one accounting period two ways.
            var second = await PreviewAsync(client, 2026, 10, ct);
            second.CapabilitiesVersion.ShouldNotBe(
                first.CapabilitiesVersion,
                "control: the flip must move the resolved set, or nothing below is being tested");

            var response = await ConfirmAsync(
                client, 2026, 10, second.Rows.Select(r => r.TargetId).ToArray(),
                second.CapabilitiesVersion, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            problem.GetProperty("code").GetString().ShouldBe("capabilities_changed_since_prior_run");
            problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrWhiteSpace(
                "the rejection must travel on the ADR-025 contract, not a hand-rolled shape");

            // Run 1 committed; run 2 posted nothing at all. The guard runs before the strategy and the
            // throw rolls the request transaction back — a rejected confirm that half-posted would be
            // worse than no guard.
            (await RunCountAsync(setup.OrgId, ct)).ShouldBe(1);
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// The other half, and the one that stops the guard from being a blanket "one run per period":
    /// the re-run recovery path (ADR-019 §2) must still work when nothing moved. Without this, a
    /// comparison hard-wired to "differs" would pass the test above.
    /// </summary>
    [Fact]
    public async Task A_second_run_under_the_same_money_path_state_is_accepted()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var first = await PreviewAsync(client, 2026, 9, ct);
        (await ConfirmAsync(client, 2026, 9, [first.Rows[0].TargetId], first.CapabilitiesVersion, ct))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        var second = await PreviewAsync(client, 2026, 9, ct);
        var response = await ConfirmAsync(
            client, 2026, 9, second.Rows.Select(r => r.TargetId).ToArray(),
            second.CapabilitiesVersion, ct);

        response.StatusCode.ShouldBe(
            HttpStatusCode.OK, "an unchanged money-path state must not block the designed re-run");

        var result = await response.Content.ReadFromJsonAsync<RunResultSpaResponse>(ct);
        result.ShouldNotBeNull();
        result.Posted.ShouldBeGreaterThan(0, "the remainder of the period must actually post");
    }

    /// <summary>
    /// The override, and the fact that it is never silent. Folded into the summary BEFORE the first
    /// save: SetSummaryJson is valid only in the Added state and RevokeAppendOnly removes UPDATE on
    /// bulk_runs entirely, so a later patch is impossible rather than merely awkward.
    /// </summary>
    [Fact]
    public async Task An_explicit_override_is_accepted_and_recorded_in_summary_json()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        try
        {
            var first = await PreviewAsync(client, 2026, 11, ct);
            (await ConfirmAsync(
                client, 2026, 11, [first.Rows[0].TargetId], first.CapabilitiesVersion, ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            await WriteFlagAsync(enabled: true, ct);

            var second = await PreviewAsync(client, 2026, 11, ct);
            var response = await client.PostAsJsonAsync(
                "/api/operations/runs/rent/confirm",
                new
                {
                    year = 2026,
                    month = 11,
                    selectedTargetIds = second.Rows.Select(r => r.TargetId).ToArray(),
                    capabilitiesVersion = second.CapabilitiesVersion,
                    acknowledgeCapabilityChange = true,
                },
                ct);

            response.StatusCode.ShouldBe(HttpStatusCode.OK);

            using var summary = JsonDocument.Parse(await LatestSummaryJsonAsync(setup.OrgId, 2026, 11, ct));
            var root = summary.RootElement;

            root.GetProperty("capabilityChangeAcknowledged").GetBoolean().ShouldBeTrue();
            root.GetProperty("capabilitiesMoneyPath").EnumerateArray()
                .Select(e => e.GetString())
                .ShouldBe([Capability + "=on"], "the state this half of the period ran under");
            root.GetProperty("capabilityChangeFrom").EnumerateArray()
                .Select(e => e.GetString())
                .ShouldBe(
                    [Capability + "=off"],
                    "and the state it overrode — an auditor must read both halves of the period off " +
                    "the run rows without replaying history");
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    /// <summary>
    /// A run that overrode nothing must say so. The acknowledgement field is always present, so "the
    /// override was not used" is a recorded fact rather than the absence of one — and an operator who
    /// passes the flag defensively on every confirm does not thereby stamp every run as an override.
    /// </summary>
    [Fact]
    public async Task An_unnecessary_override_is_not_recorded_as_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        var preview = await PreviewAsync(client, 2026, 8, ct);
        var response = await client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new
            {
                year = 2026,
                month = 8,
                selectedTargetIds = preview.Rows.Select(r => r.TargetId).ToArray(),
                capabilitiesVersion = preview.CapabilitiesVersion,
                acknowledgeCapabilityChange = true,
            },
            ct);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        using var summary = JsonDocument.Parse(await LatestSummaryJsonAsync(setup.OrgId, 2026, 8, ct));
        summary.RootElement.GetProperty("capabilityChangeAcknowledged").GetBoolean().ShouldBeFalse(
            "nothing was overridden, so nothing may be recorded as overridden");
        summary.RootElement.GetProperty("capabilityChangeFrom").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    /// <summary>
    /// ORDERING, pinned. A confirm that trips BOTH guards is answered with capabilities_changed — the
    /// auto-recoverable one — not the cross-run conflict.
    /// <para>
    /// The cross-run rejection asks the operator to make a money decision: acknowledge, or restore the
    /// earlier state. That decision has to be made holding a preview that matches the CURRENT state,
    /// because acknowledging on a stale preview authorizes amounts they never saw. The stale token, by
    /// contrast, clears itself — the SPA refetches the preview and the operator clicks again. So the
    /// cheap, self-service rejection goes first, and the operator arrives at the real decision already
    /// looking at the right numbers.
    /// </para>
    /// </summary>
    [Fact]
    public async Task A_confirm_that_trips_both_guards_reports_the_recoverable_one()
    {
        var ct = TestContext.Current.CancellationToken;
        var setup = await SetupAsync(ct);
        var client = await LoggedInClientAsync(setup, ct);

        try
        {
            var first = await PreviewAsync(client, 2026, 7, ct);
            (await ConfirmAsync(
                client, 2026, 7, [first.Rows[0].TargetId], first.CapabilitiesVersion, ct))
                .StatusCode.ShouldBe(HttpStatusCode.OK);

            // One flip trips both: the prior run recorded the pre-flip money-path state, and the token
            // the operator is still holding is the pre-flip one.
            await WriteFlagAsync(enabled: true, ct);

            var response = await ConfirmAsync(
                client, 2026, 7, first.Rows.Select(r => r.TargetId).ToArray(),
                first.CapabilitiesVersion, ct);

            response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

            var problem = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            problem.GetProperty("code").GetString().ShouldBe(
                "capabilities_changed",
                "the stale-token rejection is the recoverable one and must be reported first");
        }
        finally
        {
            await RemoveFlagAsync(ct);
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private sealed record Setup(Guid OrgId, string Email);

    private async Task<Setup> SetupAsync(CancellationToken ct)
    {
        var orgId = UuidV7.NewId();
        await using (var migratorDb = fixture.CreateContext(fixture.MigratorConnectionString))
        {
            migratorDb.Orgs.Add(new OrgEntity { Id = orgId, Name = $"Run Period Org {orgId:N}" });
            await migratorDb.SaveChangesAsync(ct);
        }

        var email = $"run-period-{orgId:N}@example.com";
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
                DisplayName = "Run Perioder",
            };
            (await userManager.CreateAsync(user, Password)).Succeeded.ShouldBeTrue();
            (await userManager.AddToRoleAsync(user, Roles.PMStaff)).Succeeded.ShouldBeTrue();
        }

        // Two leases, so a run can confirm a SUBSET of the period and leave a remainder — the shape of
        // the hazard, not an incidental detail of the fixture.
        await using (var scope = fixture.Api.Services.CreateAsyncScope())
        {
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var sender = scope.ServiceProvider.GetRequiredService<ISender>();
            await executor.RunAsSystemAsync(orgId, "test-harness", async () =>
            {
                var ownerId = await sender.Send(new CreateOwner("Period Owner", null, null, null, null, 0m), ct);
                var propId = await sender.Send(
                    new CreateProperty(ownerId, "1 Period St", "Raleigh", "NC", null, null), ct);
                await sender.Send(new CreateBankAccount("Trust", null, null, "trust"), ct);

                foreach (var n in new[] { 1, 2 })
                {
                    var unitId = await sender.Send(new CreateUnit(propId, $"#{n}", 1200m, "available"), ct);
                    var tenantId = await sender.Send(
                        new CreateTenant($"Period Tenant {n}", null, null, "current"), ct);
                    await sender.Send(
                        new CreateLease(
                            tenantId, unitId, new DateOnly(2025, 1, 1), new DateOnly(2027, 12, 31),
                            1200m, 1200m, "active"),
                        ct);
                }
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

    private static async Task<RunPreviewSpaResponse> PreviewAsync(
        HttpClient client, int year, int month, CancellationToken ct)
    {
        var preview = await client.GetFromJsonAsync<RunPreviewSpaResponse>(
            $"/api/operations/runs/rent/preview?year={year}&month={month}", ct);

        preview.ShouldNotBeNull();
        return preview;
    }

    private static Task<HttpResponseMessage> ConfirmAsync(
        HttpClient client, int year, int month, IReadOnlyList<Guid> targetIds, string version,
        CancellationToken ct) =>
        client.PostAsJsonAsync(
            "/api/operations/runs/rent/confirm",
            new { year, month, selectedTargetIds = targetIds, capabilitiesVersion = version },
            ct);

    /// <summary>The flip, committed between two HTTP calls — that is the race under test.</summary>
    private async Task WriteFlagAsync(bool enabled, CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await RlsProbe.SetPlatformAsync(conn, tx, ct);
        await ExecAsync(
            conn, tx,
            """
            INSERT INTO feature_flags (name, enabled, updated_at, updated_by)
            VALUES (@name, @enabled, now(), 'period-test')
            ON CONFLICT (name) DO UPDATE SET enabled = EXCLUDED.enabled, updated_at = EXCLUDED.updated_at
            """, ct, ("name", Capability), ("enabled", enabled));

        await tx.CommitAsync(ct);
    }

    /// <summary>Restores the shared, global flag state, notifying so no sibling test inherits it.</summary>
    private async Task RemoveFlagAsync(CancellationToken ct)
    {
        await using var conn = await fixture.OpenAppConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await RlsProbe.SetPlatformAsync(conn, tx, ct);
        await ExecAsync(conn, tx, "DELETE FROM feature_flags WHERE name = @name", ct, ("name", Capability));
        await ExecAsync(
            conn, tx, $"SELECT pg_notify('{CapabilityNotifications.Channel}', @name)", ct,
            ("name", Capability));

        await tx.CommitAsync(ct);
    }

    private async Task<int> RunCountAsync(Guid orgId, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();

        var count = -1;
        await executor.RunAsSystemAsync(orgId, "test-harness", async () => count = await db.Set<BulkRun>().CountAsync(ct), ct);
        return count;
    }

    /// <summary>
    /// Read through the org-scoped executor, not a bare connection: bulk_runs is RLS'd, and a
    /// context-free read fails closed rather than returning another org's row.
    /// </summary>
    private async Task<string> LatestSummaryJsonAsync(
        Guid orgId, int year, int month, CancellationToken ct)
    {
        await using var scope = fixture.Api.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();

        var summary = string.Empty;
        await executor.RunAsSystemAsync(
            orgId, "test-harness",
            async () => summary = await db.Set<BulkRun>()
                .Where(r => r.PeriodYear == year && r.PeriodMonth == month)
                .OrderByDescending(r => r.CreatedAt)
                .ThenByDescending(r => r.Id)
                .Select(r => r.SummaryJson)
                .FirstAsync(ct),
            ct);

        return summary;
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
