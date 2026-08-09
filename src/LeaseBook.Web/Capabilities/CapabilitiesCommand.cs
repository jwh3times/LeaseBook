using System.Globalization;
using System.Text.Json;
using LeaseBook.Modules.Capabilities.Caching;
using LeaseBook.Modules.Capabilities.Contracts;
using LeaseBook.Modules.Capabilities.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Capabilities;

/// <summary>
/// Executes a parsed <see cref="CapabilitiesAction"/> (ADR-028). This is the only write surface for
/// capability state — there is no endpoint and no UI — and every mutation it performs lands with a
/// <c>platform_audit_events</c> row in the SAME transaction, so the platform audit trail exists from
/// the first write rather than being retrofitted later.
/// <para>
/// <b>It is also the only READ surface for three of the four tables</b>, which is why
/// <see cref="ListAsync"/> reports entitlement and cohort state and not just flags: an operator told
/// to go and check whether a grant landed must have something that can answer that.
/// </para>
/// <para>
/// <b>Everything mutating runs inside <see cref="PlatformScopedExecutor"/>.</b> It is the single call
/// site that sets <c>app.platform</c> (<c>PlatformScopeCallSiteTests</c> enforces exactly one), and
/// without it the writes here do not fail loudly in any uniform way: an INSERT raises 42501, but an
/// UPDATE or DELETE on <c>feature_flags</c> is filtered by the write policy's USING and simply
/// affects <b>zero rows</b>. That silence is why the EF write path is used for the flag toggle and the
/// cohort removal — EF turns a zero-row UPDATE or DELETE into
/// <see cref="DbUpdateConcurrencyException"/> — and why any raw statement here would have to assert
/// its own affected-row count.
/// </para>
/// <para>
/// <b><c>NOTIFY</c> is issued inside that same transaction.</b> Postgres queues notifications and
/// delivers them after commit, preserving order, so a listener can never be woken before the row it
/// must read is visible. Issued after the commit instead, the race is back. See
/// <see cref="CapabilityNotificationListener"/>, which owns the channel name.
/// </para>
/// </summary>
public static class CapabilitiesCommand
{
    /// <summary>
    /// Environment variable naming the human or system accountable for a change. See
    /// <see cref="Actor"/>.
    /// </summary>
    public const string OperatorVariable = "LEASEBOOK_OPERATOR";

    /// <summary>
    /// Runs one invocation and returns the process exit code: 0 on success, 1 on any parse or write
    /// failure.
    /// </summary>
    public static async Task<int> RunAsync(IServiceProvider services, string[] args)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!CapabilitiesVerb.TryResolve(args, out var action, out var error))
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var ct = CancellationToken.None;

        // Read ONCE per invocation, then threaded through every row this command writes. The value is
        // an environment read; taking it separately for the state row and its audit row would let the
        // two disagree, and both are append-only, so a mismatch could never be corrected afterwards.
        var configuredOperator = Environment.GetEnvironmentVariable(OperatorVariable);

        // BEFORE the scope and before any of THIS command's database work, so a refusal writes nothing
        // and opens no transaction. It is not the first database call the PROCESS makes — Program.cs
        // attempts role seeding before dispatching any verb — but do NOT read that as proof the
        // connection works: that call is TryEnsureRolesAsync, which reports an unreachable server and
        // continues so the host can bind. So this verb may well be the first thing to actually reach
        // the database, and the first place an outage surfaces. Verified: `capabilities list` against a
        // dead endpoint now dies with an unhandled NpgsqlException from executor.RunAsync below rather
        // than from the role seeder — a different frame, the same loud non-zero exit, and deliberately
        // NOT summarized (see the catch filter: only operator-actionable rejections are). The guarantee
        // being made here is about writes, not about the process touching nothing. Resolved from the
        // root provider, where IHostEnvironment is a singleton.
        var environment = services.GetRequiredService<IHostEnvironment>();
        if (AttributionRefusal(action.Kind, environment.IsDevelopment(), configuredOperator) is { } gap)
        {
            Console.Error.WriteLine(gap);
            return 1;
        }

        // A scope of our own: this runs from the root provider, where no request or job scope exists.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();
        var executor = scope.ServiceProvider.GetRequiredService<PlatformScopedExecutor>();
        var admin = scope.ServiceProvider.GetRequiredService<ICapabilityAdmin>();

        var actor = BuildActor(configuredOperator);

        try
        {
            // Inside the try like every other path: `list` reads entitlements and capability_cohorts,
            // which are platform-gated, so a 42501 here deserves the same explanation a write gets
            // rather than a raw stack. It was outside while the listing read only feature_flags.
            if (action.Kind == CapabilitiesActionKind.List)
            {
                await executor.RunAsync(() => ListAsync(db, action, ct), ct);

                // AFTER the transaction has committed and released its connection. The age report
                // touches no DbContext — it reads git history — and it spawns one subprocess per
                // capability at up to 15s each. Inside the executor that time would be spent
                // idle-in-transaction on a pooled connection for no benefit whatsoever.
                if (action.Stale)
                {
                    await ReportAgeAsync(ct);
                }

                return 0;
            }

            // No executor here: the module member opens its own platform-scoped transaction. That is
            // what makes this path unreachable from inside a request transaction — see ICapabilityAdmin.
            var applied = await DispatchAsync(admin, action, actor, ct);

            // Printed AFTER the transaction commits, never inside it. A success line followed by a
            // commit-time stack trace would tell the operator the opposite of what happened.
            Console.WriteLine(Summarize(action, applied, actor));
        }
        catch (CapabilityRefusedException refusal)
        {
            // A deliberate in-transaction refusal: nothing to remove, or an org that does not exist.
            // Throwing is what rolled the transaction back, so no state row and no audit row survive.
            Console.Error.WriteLine(refusal.Message);
            return 1;
        }
        catch (Exception ex) when (Describe(ex) is { } message)
        {
            // A known, operator-actionable database rejection. Anything else propagates with its
            // stack: an unexpected failure on the platform plane is not something to summarize away.
            Console.Error.WriteLine(message);
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// The operator identity recorded on every row this verb writes.
    /// <para>
    /// <b>Not a user id, deliberately.</b> This process runs as <c>leasebook_app</c> with no
    /// authenticated principal — locally an engineer's shell, in production the capabilities ACA job
    /// (ADR-027) — so there is no <c>asp_net_users</c> row to point at, and inventing one would put a
    /// fiction in an append-only audit trail. Whether a platform admin eventually lives in
    /// <c>asp_net_users</c> under a new role or in a separate store is Project 2's question.
    /// </para>
    /// <para>
    /// <b><see cref="OperatorVariable"/> is what makes production rows attributable.</b> Process
    /// identity alone degrades to <c>cli:root@&lt;ephemeral-container-id&gt;</c> for every row the ACA
    /// job writes, which attributes a change to nobody. The job sets the variable to the human or
    /// system on the hook; the process identity is still appended, because the two answer different
    /// questions — who decided, versus where it ran from. These rows are append-only, so nothing
    /// written under the weaker convention can ever be corrected; that is why the variable exists from
    /// the first write rather than being added once someone notices — and why, outside Development,
    /// <see cref="AttributionRefusal"/> refuses the mutation instead of falling back to it.
    /// </para>
    /// <para>
    /// Recomputed per read rather than cached in a static, so a value exported after process start
    /// still applies and no test needs a static reset.
    /// </para>
    /// </summary>
    public static string Actor => BuildActor(Environment.GetEnvironmentVariable(OperatorVariable));

    /// <summary>
    /// Refuses a MUTATING invocation that would be recorded against nobody, or returns null when the
    /// write may proceed.
    /// <para>
    /// <b>This is the difference between wiring <see cref="OperatorVariable"/> and relying on someone
    /// to remember it.</b> Unset, <see cref="BuildActor"/> falls back to process identity — which is a
    /// real answer in a developer's shell and no answer at all in the capabilities ACA job, where it
    /// is a container user with no passwd entry at an ephemeral pod name. Because every row this verb
    /// writes is append-only in both planes, an unattributed row is not a small defect to tidy up
    /// later: it is permanent. Refusing costs the operator one re-run; accepting costs the audit trail
    /// a row that can never be corrected.
    /// </para>
    /// <para>
    /// <b>Gated on Development rather than on a container check</b>, because the question is not "am I
    /// in a container" but "is process identity a person". In Development it is — an engineer's own
    /// shell, machine and username — so requiring the variable there would be ceremony with no
    /// attribution gained, and would make every local invocation and every test set an env var.
    /// Everywhere else it is not, so the variable is the only thing that can answer "who decided".
    /// </para>
    /// <para>
    /// <see cref="CapabilitiesActionKind.List"/> is exempt because it writes nothing — including no
    /// audit row. An operator diagnosing an incident must always be able to READ capability state,
    /// and making the read path refusable would be the one way this guard could make an outage worse.
    /// </para>
    /// <para>
    /// <b>To exercise this locally, pass <c>--no-launch-profile</c>.</b> <c>dotnet run</c> otherwise
    /// applies <c>Properties/launchSettings.json</c>, which pins <c>ASPNETCORE_ENVIRONMENT</c> to
    /// Development and silently overrides the variable you set on the command line — so the guard
    /// looks broken when it is not. The published container carries no launch profile, so nothing in
    /// Azure is affected.
    /// </para>
    /// </summary>
    public static string? AttributionRefusal(
        CapabilitiesActionKind kind, bool isDevelopment, string? configuredOperator)
    {
        if (kind == CapabilitiesActionKind.List
            || isDevelopment
            || !string.IsNullOrWhiteSpace(configuredOperator))
        {
            return null;
        }

        return
            $"capabilities: refusing to apply this change because {OperatorVariable} is not set, and " +
            "nothing was written. Outside Development there is no person behind this process, so the " +
            $"actor would be recorded as '{BuildActor(null)}' — which attributes the change to nobody " +
            "— and platform_audit_events is append-only in both planes, so that row could never be " +
            "corrected. Name the accountable party and re-run.\n" +
            "In production: edit the copy of infra/jobs/capabilities-exec.yaml you started this with, " +
            $"set the {OperatorVariable} entry's `value:` to your name, and re-run " +
            "`az containerapp job start ... --yaml <file>`. That file ships the variable EMPTY on " +
            "purpose, so this refusal is what a forgotten edit looks like rather than an audit row " +
            "attributed to nobody.\n" +
            "Do not rebuild the invocation out of `--env-vars`/`--args` flags instead. That form does " +
            "not merge with the job's template — it sends only what you pass — so it has to restate " +
            "the container name, image, every variable and the resources block, and it cannot express " +
            "a dash-prefixed argument at all (`--org`, `--stale`), which rules out grant, revoke and " +
            "cohort outright. docs/runbooks/diagnostics.md has the full procedure. " +
            "`capabilities list` needs none of this: it writes nothing.";
    }

    /// <summary>The pure half of <see cref="Actor"/>, so the convention is testable without env state.</summary>
    public static string BuildActor(string? configuredOperator)
    {
        static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();

        var process = $"cli:{OrUnknown(Environment.UserName)}@{OrUnknown(Environment.MachineName)}";

        return string.IsNullOrWhiteSpace(configuredOperator)
            ? process
            : $"operator:{configuredOperator.Trim()} ({process})";
    }

    // ── Read ────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Prints the registry joined to its stored state: the flag, plus how many orgs hold a live
    /// entitlement and how many cohort rules exist. With <c>--org</c> it adds that tenant's own
    /// entitlement and cohort state. <c>--stale</c> appends the age report, which the caller runs after
    /// this transaction commits — see <see cref="ReportAgeAsync"/>.
    /// <para>
    /// <b>Platform-scoped, and it has to be.</b> <c>feature_flags</c> alone would not need it
    /// (<c>feature_flags_read</c> is <c>FOR SELECT USING (true)</c>, which is why
    /// <c>CapabilityRegistryValidator</c> reads it bare), but <c>entitlements</c> and
    /// <c>capability_cohorts</c> are visible across orgs only under <c>app.platform</c> — without it
    /// their <c>_org_read</c> policy filters to zero rows and this listing would report "nobody is
    /// entitled to anything" with no error anywhere. Writing no audit row is correct: a read changes
    /// nothing.
    /// </para>
    /// <para>
    /// Entitlement state is the LATEST event per <c>(org, capability)</c>, ordered exactly as
    /// <c>CapabilityStateReader</c> orders it — <c>effective_at DESC, granted ASC, id DESC</c>, under
    /// <c>effective_at &lt;= now()</c> — so the listing and the resolver can never disagree about what
    /// is live. A future-dated row is pending in both.
    /// </para>
    /// </summary>
    private static async Task ListAsync(DbContext db, CapabilitiesAction action, CancellationToken ct)
    {
        // Before anything is printed, so a bad --org fails cleanly instead of emitting a valid
        // deployment-wide table and then erroring — see AssertOrgExistsAsync for why it is checked.
        if (action.OrgId is { } target)
        {
            await AssertOrgExistsAsync(db, target, ct);
        }

        var flags = await db.Set<FeatureFlag>().AsNoTracking().ToDictionaryAsync(
            f => f.Name, StringComparer.Ordinal, ct);

        var entitled = (await db.Database
            .SqlQuery<CapabilityCount>(
                $"""
                 SELECT capability, count(*) AS count
                 FROM (
                     SELECT DISTINCT ON (org_id, capability) capability, granted
                     FROM entitlements
                     WHERE effective_at <= now()
                     ORDER BY org_id, capability, effective_at DESC, granted ASC, id DESC
                 ) latest
                 WHERE granted
                 GROUP BY capability
                 """)
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, r => r.Count, StringComparer.Ordinal);

        var cohorts = (await db.Database
            .SqlQuery<CapabilityCount>(
                $"SELECT capability, count(*) AS count FROM capability_cohorts GROUP BY capability")
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, r => r.Count, StringComparer.Ordinal);

        Console.WriteLine(
            $"{"CAPABILITY",-26} {"FLAG",-9} {"DEFAULT",-8} {"GRANT",-6} {"MONEY",-7} " +
            $"{"ENTITLED",-9} {"COHORTS",-8} UPDATED");

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            flags.TryGetValue(capability.Name, out var flag);

            // "(none)" is not cosmetic: an ABSENT row and an explicit `enabled = false` resolve
            // differently — a cohort ORs over an absent row but never over an explicit kill.
            var state = flag is null ? "(none)" : flag.Enabled ? "enabled" : "killed";
            var updated = flag is null
                ? "—"
                : $"{flag.UpdatedAt.ToUniversalTime():u} by {flag.UpdatedBy}";
            var money = capability.IsMoneyPath ? capability.IsFixture ? "fixture" : "yes" : "no";

            Console.WriteLine(
                $"{capability.Name,-26} {state,-9} " +
                $"{(capability.DefaultEnabled ? "on" : "off"),-8} " +
                $"{(capability.RequiresGrant ? "yes" : "no"),-6} {money,-7} " +
                $"{entitled.GetValueOrDefault(capability.Name),-9} " +
                $"{cohorts.GetValueOrDefault(capability.Name),-8} {updated}");
        }

        if (action.OrgId is { } orgId)
        {
            await ListForOrgAsync(db, orgId, ct);
        }
    }

    /// <summary>
    /// The age half of the listing (ADR-028): when each capability entered the registry, how old it is,
    /// and whether a money-path one has outlived <see cref="CapabilityAge.PolicyWindow"/>.
    /// <para>
    /// <b>The same verdict CI enforces, from the same code.</b> <c>CapabilityAgeTests</c> fails the
    /// build on exactly what this prints as STALE — both call <see cref="CapabilityAge.IsStale"/> — so
    /// an operator can never be told "within window" by the tool that CI is about to contradict.
    /// </para>
    /// <para>
    /// <b>Run outside the platform-scoped transaction</b>, by <see cref="RunAsync"/> once the listing
    /// has committed: it needs no database at all, and one subprocess per capability at up to 15s each
    /// is not something to hold a pooled connection open through.
    /// </para>
    /// <para>
    /// <b>Age comes from git history, so it is UNKNOWN wherever the source tree or history is not.</b>
    /// That includes the production ACA job (ADR-027), which runs from an image. The unavailable case is
    /// stated in full, at the top, before any row is printed: a staleness report that renders an empty
    /// or dashed column reads as "nothing is stale", which is the one conclusion this must never invite.
    /// </para>
    /// </summary>
    private static async Task ReportAgeAsync(CancellationToken ct)
    {
        var report = await CapabilityAge.ResolveAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var window = (int)CapabilityAge.PolicyWindow.TotalDays;

        Console.WriteLine();
        Console.WriteLine($"capability age (money-path policy window: {window} days):");

        if (!report.IsAvailable)
        {
            Console.WriteLine(
                $"  UNKNOWN — {report.UnavailableReason} Nothing below is a staleness verdict; read " +
                "every age as unknown rather than as fresh.");
        }

        Console.WriteLine($"  {"CAPABILITY",-26} {"INTRODUCED",-12} {"AGE",-9} POLICY");

        var stale = new List<string>();

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var introduced = report.IntroducedAt.TryGetValue(capability.Name, out var when)
                ? (DateTimeOffset?)when
                : null;

            var age = introduced is { } at ? $"{(now - at).Days}d" : "unknown";
            var date = introduced is { } start
                ? start.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : "unknown";

            string policy;
            if (!capability.IsMoneyPath)
            {
                policy = "n/a (not money-path)";
            }
            else if (capability.IsFixture)
            {
                // The exemption is stated on the row rather than left blank: a money-path entry showing
                // no verdict at all is indistinguishable from a gate that failed to evaluate it.
                policy = "exempt (Capability.IsFixture)";
            }
            else if (introduced is not { } known)
            {
                policy = "UNKNOWN (age unreadable — not a pass)";
            }
            else if (CapabilityAge.IsStale(capability, known, now))
            {
                policy = $"STALE — past {window} days";
                stale.Add($"{capability.Name} ({(now - known).Days} days old)");
            }
            else
            {
                policy = $"ok ({window - (now - known).Days}d left)";
            }

            Console.WriteLine($"  {capability.Name,-26} {date,-12} {age,-9} {policy}");
        }

        Console.WriteLine();

        if (stale.Count > 0)
        {
            Console.WriteLine(
                $"capabilities: {stale.Count} money-path capability(ies) past the {window}-day window: " +
                string.Join(", ", stale) + ". These are short-lived by policy — each one is standing " +
                "risk on the books. Delete the capability and its gate, or record the extension in " +
                "ADR-028. CI fails on this (CapabilityAgeTests).");
        }
        else if (report.IsAvailable)
        {
            Console.WriteLine(
                $"capabilities: no money-path capability is past the {window}-day window.");
        }
    }

    /// <summary>
    /// One tenant's own state. This is the view the entitlement-collision message sends an operator
    /// to: it answers "did the earlier grant land, and when", which the flag table cannot.
    /// </summary>
    private static async Task ListForOrgAsync(DbContext db, Guid orgId, CancellationToken ct)
    {
        var entitlements = (await db.Database
            .SqlQuery<OrgEntitlementRow>(
                $"""
                 SELECT DISTINCT ON (capability) capability, granted, effective_at, actor
                 FROM entitlements
                 WHERE org_id = {orgId}
                   AND effective_at <= now()
                 ORDER BY capability, effective_at DESC, granted ASC, id DESC
                 """)
            .ToListAsync(ct))
            .ToDictionary(r => r.Capability, StringComparer.Ordinal);

        var rules = await db.Database
            .SqlQuery<OrgCohortRow>(
                $"""
                 SELECT capability, user_id
                 FROM capability_cohorts
                 WHERE org_id = {orgId}
                 ORDER BY capability, user_id NULLS FIRST
                 """)
            .ToListAsync(ct);

        Console.WriteLine();
        Console.WriteLine($"org {orgId}:");

        foreach (var capability in CapabilityCatalog.All.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            var entitlement = entitlements.GetValueOrDefault(capability.Name) is { } row
                ? $"{(row.Granted ? "granted" : "revoked")} {row.EffectiveAt.ToUniversalTime():u} by {row.Actor}"
                : "(no entitlement event)";

            var mine = rules
                .Where(r => string.Equals(r.Capability, capability.Name, StringComparison.Ordinal))
                .ToList();
            var cohort = mine.Count == 0
                ? "none"
                : string.Join(", ", mine.Select(r => r.UserId is { } user ? $"user {user}" : "org-wide"));

            Console.WriteLine($"  {capability.Name,-26} entitlement: {entitlement}");
            Console.WriteLine($"  {string.Empty,-26} cohort:      {cohort}");
        }
    }

    /// <summary>
    /// Refuses a listing for an org that is not there.
    /// <para>
    /// <b>This is the read path's version of the FK the write paths get for free.</b> A capability
    /// with no rows for a real org prints "(no entitlement event)" — which is also exactly what a
    /// MISTYPED org id would print, for every capability at once. Since this listing is the remedy the
    /// entitlement-collision message names, a confidently wrong answer here would reopen the very loop
    /// that remedy closes: the operator reads "the grant did not land" and re-issues a grant that in
    /// fact succeeded. Wording mirrors the FK-violation branch in <see cref="Describe"/> on purpose,
    /// so the same mistake reads the same way whichever verb surfaced it.
    /// </para>
    /// <para>
    /// <c>orgs</c> is global-class — no <c>org_id</c>, no RLS, the org IS the tenant — so this read
    /// needs no context of its own and cannot itself return a misleading empty.
    /// </para>
    /// </summary>
    private static async Task AssertOrgExistsAsync(DbContext db, Guid orgId, CancellationToken ct)
    {
        var exists = await db.Database
            .SqlQuery<int>($"""SELECT 1 AS "Value" FROM orgs WHERE id = {orgId}""")
            .AnyAsync(ct);

        if (!exists)
        {
            throw new CapabilityRefusedException(
                $"capabilities: org {orgId} does not exist, so there is nothing to list for it. An org " +
                "with no capability rows and an org that is not there both look like " +
                "'(no entitlement event)', and reporting the second as the first is how a grant that " +
                "landed gets issued twice. Pass a real tenant id, or one of 'demo', 'cutover', 'load', " +
                "or 'scenario'.");
        }
    }

    // ── Dispatch ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Translates one parsed action into the module member that performs it.
    /// <para>
    /// The switch lives here, on the parse result, rather than inside the module. The parser already
    /// knew which command it read; re-deriving that on the far side of the seam from an action object
    /// whose fields are mostly null for any given kind would be the CLI's shape leaking into an
    /// interface. Everything the write itself needs — platform scope, the Postgres timestamp, the
    /// audit row, the notification — is now the module's, and this file no longer names any of it.
    /// </para>
    /// </summary>
    private static Task<DateTime> DispatchAsync(
        ICapabilityAdmin admin, CapabilitiesAction action, string actor, CancellationToken ct)
    {
        var capability = action.Capability!;

        return action.Kind switch
        {
            CapabilitiesActionKind.FlagEnable => admin.EnableFlagAsync(capability, actor, ct),
            CapabilitiesActionKind.FlagDisable => admin.DisableFlagAsync(capability, actor, ct),
            CapabilitiesActionKind.FlagClear => admin.ClearFlagAsync(capability, actor, ct),
            CapabilitiesActionKind.Grant => admin.GrantAsync(capability, action.OrgId!.Value, actor, ct),
            CapabilitiesActionKind.Revoke => admin.RevokeAsync(capability, action.OrgId!.Value, actor, ct),
            CapabilitiesActionKind.CohortAdd =>
                admin.AddToCohortAsync(capability, action.OrgId!.Value, action.UserId, actor, ct),
            CapabilitiesActionKind.CohortRemove =>
                admin.RemoveFromCohortAsync(capability, action.OrgId!.Value, action.UserId, actor, ct),
            _ => throw new InvalidOperationException($"Unhandled action kind {action.Kind}."),
        };
    }


    // ── Reporting ───────────────────────────────────────────────────────────────────────────────

    private static string Summarize(CapabilitiesAction action, DateTime now, string actor)
    {
        var capability = action.Capability;
        var org = action.OrgId;

        return action.Kind switch
        {
            CapabilitiesActionKind.FlagEnable =>
                $"capabilities: flag '{capability}' is now ENABLED deployment-wide (by {actor}).",
            CapabilitiesActionKind.FlagDisable =>
                $"capabilities: flag '{capability}' is now KILLED deployment-wide (by {actor}). An " +
                "explicit kill beats a cohort match; `flag clear` restores cohort/default resolution.",
            CapabilitiesActionKind.FlagClear =>
                $"capabilities: flag override for '{capability}' is CLEARED deployment-wide (by {actor}); " +
                "resolution now falls through to cohort state and the registry default.",
            CapabilitiesActionKind.Grant =>
                $"capabilities: granted '{capability}' to org {org} effective {now.ToUniversalTime():u}.",
            CapabilitiesActionKind.Revoke =>
                $"capabilities: revoked '{capability}' from org {org} effective {now.ToUniversalTime():u} " +
                "(appended as a new event — entitlements are append-only).",
            CapabilitiesActionKind.CohortAdd => action.UserId is { } user
                ? $"capabilities: added user {user} in org {org} to the '{capability}' cohort."
                : $"capabilities: added org {org} to the '{capability}' cohort.",
            CapabilitiesActionKind.CohortRemove => action.UserId is { } removed
                ? $"capabilities: removed user {removed} in org {org} from the '{capability}' cohort."
                : $"capabilities: removed org {org}'s org-wide '{capability}' cohort rule.",
            _ => $"capabilities: {action.Kind} applied.",
        };
    }

    /// <summary>
    /// Maps the database rejections an operator can actually act on onto a message that says what to
    /// do. Anything unrecognized returns null and propagates — swallowing an unexpected failure on
    /// the platform plane would be worse than a stack trace.
    /// </summary>
    private static string? Describe(Exception exception)
    {
        // Checked BEFORE the Postgres unwrap, because a zero-row UPDATE/DELETE produces no server
        // error at all — there is nothing underneath to match on. This is the W3 case (RLS filtering
        // a write to zero rows, silently) surfacing as the one exception EF raises for it. Relying on
        // the batch's audit INSERT to raise 42501 first would be relying on EF's statement ordering.
        if (Find<DbUpdateConcurrencyException>(exception) is not null)
        {
            return
                "capabilities: the write matched no rows and was rolled back. On the platform tables " +
                "that means RLS filtered it out rather than rejecting it — an UPDATE or DELETE without " +
                "app.platform succeeds affecting zero rows instead of raising, which is exactly the " +
                "silence EF turns into this error. Nothing was written, including the audit row. If " +
                "this reproduces, the platform escape is not opening (PlatformScopedExecutor).";
        }

        var postgres = Find<PostgresException>(exception);
        if (postgres is null)
        {
            return null;
        }

        return postgres.SqlState switch
        {
            // 23505 on the entitlements uniqueness index: two grant events for one (org, capability)
            // at the same instant. `id` is NOT a usable tie-break (UUIDv7's low bits are random), so
            // the resolver could not order them — the index rejects the second rather than leaving
            // "current state" undefined. In practice this is a double-invocation, and the remedy names
            // the per-org listing because the flag table cannot answer whether the first one landed.
            PostgresErrorCodes.UniqueViolation
                when postgres.ConstraintName == "ux_entitlements_org_capability_effective_at" =>
                "capabilities: an entitlement event for this org and capability already exists at this " +
                "exact instant, so this one was rejected and nothing was written. Two events at one " +
                "timestamp have no defined order, which would leave current entitlement state " +
                "ambiguous. This is almost always a command run twice — run " +
                "`capabilities list --org <id>` to see whether the earlier one landed, and re-run only " +
                "if it really is a second change.",

            PostgresErrorCodes.ForeignKeyViolation =>
                "capabilities: that org does not exist. Entitlements and cohort rules reference `orgs`, " +
                "so the id has to name a real tenant — check it with `check-invariants --all` or pass " +
                "one of 'demo', 'cutover', 'load', or 'scenario'.",

            PostgresErrorCodes.InsufficientPrivilege =>
                "capabilities: the database refused the write (42501). Platform-plane writes require " +
                "app.platform, which only PlatformScopedExecutor sets — if this verb reached here, the " +
                "escape did not open. " + postgres.MessageText,

            _ => null,
        };
    }

    /// <summary>
    /// EF wraps provider failures (in <see cref="DbUpdateException"/>, and transient ones in an
    /// <see cref="InvalidOperationException"/>), so the chain is walked rather than the top frame
    /// matched — the same trap <c>CapabilityRegistryValidator.IsUnreachable</c> documents.
    /// </summary>
    private static T? Find<T>(Exception exception) where T : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }


    private sealed record CapabilityCount(string Capability, long Count);

    private sealed record OrgEntitlementRow(string Capability, bool Granted, DateTime EffectiveAt, string Actor);

    private sealed record OrgCohortRow(string Capability, Guid? UserId);
}
