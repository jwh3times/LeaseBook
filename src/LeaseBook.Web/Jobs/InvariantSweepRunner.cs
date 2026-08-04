using System.Diagnostics;
using LeaseBook.Modules.Accounting.Contracts;
using LeaseBook.SharedKernel.Observability;
using LeaseBook.SharedKernel.Tenancy;
using LeaseBook.Web.Observability;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;

namespace LeaseBook.Web.Jobs;

/// <summary>
/// The shared sweep core (WP-11). Resolves the org list, then runs <see cref="IInvariantChecks"/>
/// once per org inside that org's own RLS transaction.
/// <para>
/// Takes the root <see cref="IServiceProvider"/> rather than scoped dependencies because a sweep
/// spans many orgs and each needs its own scope: <see cref="OrgScopedExecutor"/> sets
/// <c>app.org_id</c> with <c>SET LOCAL</c>, so reusing one scope across orgs would mean reusing one
/// transaction and one org context.
/// </para>
/// <para>
/// <b>Capability snapshots: at the outermost unit of work, never inside this loop (ADR-028).</b>
/// Nothing here gates on a capability today, and that is why no resolve appears below — not an
/// oversight. The rule for when one is needed: the sweep is ONE logical unit of work spanning many
/// per-org transactions, so a flag flipped between org 3 and org 4 would split a single night's
/// sweep across two behaviours, with nothing in the result saying which orgs got which. A resolve
/// placed inside the loop cannot detect that; it is what causes it.
/// </para>
/// <para>
/// A single sweep-wide <c>CapabilitySet</c> is not obtainable from the seam as it stands, and that
/// is a real constraint rather than a missing convenience: entitlements are per-org rows and
/// <c>ICapabilityGate</c> resolves against the ambient <c>app.org_id</c>, so one set cannot speak
/// for many orgs. Whoever adds the first sweep-level gate has to decide deliberately which of the
/// two it is — a deployment-wide kill switch, which belongs in a resolve taken once before the loop
/// and passed in explicitly (and needs a flag-plane read that does not exist yet), or a per-org
/// entitlement, which belongs inside each org's own unit of work and must then be recorded per org
/// in the result so a split sweep is legible. Do not resolve per org and treat the answer as though
/// it governed the sweep. <c>SweepCapabilityFreezeTests</c> fails the moment this file mentions the
/// seam, so that decision cannot be made silently.
/// </para>
/// </summary>
public sealed class InvariantSweepRunner(
    IServiceProvider services,
    ILogger<InvariantSweepRunner> logger) : ISweepRunner
{
    public async Task<SweepResult> RunAsync(
        IReadOnlyList<Guid>? orgIds = null, CancellationToken ct = default)
    {
        // The job runs with no HTTP request, so there is no ambient Activity and no correlation id
        // (ADR-025). This span is the correlation handle instead: every violation below hangs off it.
        using var activity = LeaseBookTelemetry.Source.StartActivity("jobs.invariant_sweep");

        var targets = orgIds ?? await ResolveAllOrgIdsAsync(ct);
        activity?.SetTag("org_count", targets.Count);

        var violations = new List<SweepViolation>();
        foreach (var orgId in targets)
        {
            ct.ThrowIfCancellationRequested();

            await using var scope = services.CreateAsyncScope();
            var executor = scope.ServiceProvider.GetRequiredService<OrgScopedExecutor>();
            var checks = scope.ServiceProvider.GetRequiredService<IInvariantChecks>();

            IReadOnlyList<InvariantViolation> found = [];
            await executor.RunAsync(orgId, async () => found = await checks.CheckCoreAsync(ct), ct);

            foreach (var violation in found)
            {
                violations.Add(new SweepViolation(orgId, violation.Invariant, violation.Detail));
                Emit(activity, orgId, violation);
            }
        }

        activity?.SetTag("violation_count", violations.Count);
        return new SweepResult(targets.Count, violations);
    }

    /// <summary>
    /// The narrow system path: <c>orgs</c> is a global-class table (no <c>org_id</c>, no RLS — the org
    /// IS the tenant), so enumerating it is the one read a sweep may legitimately make before any org
    /// context exists. Every read after this point is org-scoped.
    /// </summary>
    private async Task<IReadOnlyList<Guid>> ResolveAllOrgIdsAsync(CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.Orgs.Select(o => o.Id).ToListAsync(ct);
    }

    /// <summary>
    /// One violation, emitted twice on purpose: a structured log carrying the stable
    /// <see cref="LogEvents.InvariantViolation"/> id that Track B's alert rule keys on, and a span
    /// event so the violation is visible on the sweep's own trace. Neither carries the detail string
    /// into a tag — it is free text from the check and belongs in the log message only.
    /// </summary>
    private void Emit(Activity? activity, Guid orgId, InvariantViolation violation)
    {
        logger.LogError(
            LogEvents.InvariantViolation,
            "Trust invariant {Invariant} violated for org {OrgId}: {Detail}",
            violation.Invariant,
            orgId,
            violation.Detail);

        activity?.AddEvent(new ActivityEvent(
            "trust.invariant.violation",
            tags: new ActivityTagsCollection
            {
                { "org_id", orgId },
                { "invariant", violation.Invariant },
            }));
    }
}
