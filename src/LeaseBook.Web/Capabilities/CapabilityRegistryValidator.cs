using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Capabilities;

/// <summary>
/// Startup guard for the capability seam (ADR-028): every <c>feature_flags</c> row must name a
/// capability that exists in the registry.
/// <para>
/// <b>The database stores STATE; the registry defines what EXISTS.</b> A row whose name is not in
/// <c>Capabilities.All</c> is drift — an operator typo on the toggle CLI, or a capability deleted from
/// code while its row survived the deployment. Neither is visible at runtime: the resolver iterates the
/// registry and simply ignores unmatched rows, so an operator who flips <c>consolidated-statments</c>
/// sees a successful write, no error anywhere, and a feature that never turns on. This is what turns
/// that silence into a signal — a boot failure where one is affordable, a logged error where it is not.
/// </para>
/// <para>
/// <b>An unknown row is inert, so this throws everywhere EXCEPT Production, where it logs and
/// continues.</b> Resolution is registry-driven — <c>CapabilityStateReader</c> iterates
/// <c>Capabilities.All</c> and <c>CapabilitySet.From</c> asserts completeness against it — so nothing
/// ever reads a row whose name is not registered. Drift is a signal, not a correctness hazard, and that
/// asymmetry decides the policy: a hard failure in Production would make rollback impossible. Deploy N
/// adds a capability, an operator flips it and creates the row, something unrelated regresses, and the
/// rollback target is N-1 — whose registry does not name that row. Every N-1 replica would then fail to
/// start, turning a routine rollback into a manual <c>DELETE</c> against production Postgres. Being
/// roll-forward-only is a far worse failure mode than a flag that quietly does nothing.
/// </para>
/// <para>
/// <b>Local dev and CI</b> are where drift is cheap and actionable, so they throw. Note the boundary is
/// the ASPNETCORE_ENVIRONMENT value, not the tier name: <c>infra/modules/containerapp.bicep</c> sets no
/// environment variable, so <b>both</b> <c>lb-dev-app</c> and <c>lb-prod-app</c> resolve as Production
/// and tolerate-and-log. That is intended — a deployed dev tier is prod-like and gets rollback safety
/// too — but it does mean "the dev environment throws" is only true of a developer's machine and the CI
/// test host. The operator-typo case is caught earlier still: the toggle CLI rejects a name that is not
/// in the registry at write time, which is the point at which the typo can be corrected in one step.
/// </para>
/// <para>
/// <b>Every unknown name, not the first</b> — in both branches. A rename usually lands as a pair (the
/// new row inserted, the old one left behind) and a bad deploy can strand several at once. Reporting
/// only the first would turn one fix into N boot-fix-boot cycles, each costing a full deployment.
/// </para>
/// <para>
/// <b>An unreachable database is a different failure and gets the opposite answer: log and continue,
/// everywhere.</b> See the remarks on <see cref="ValidateAsync"/> — swallowing it is what lets the
/// readiness gate do its job at all.
/// </para>
/// </summary>
public static class CapabilityRegistryValidator
{
    /// <summary>
    /// Reads every <c>feature_flags</c> name and reports the ones the registry does not know: one throw
    /// listing all of them outside Production, one logged error listing all of them in Production.
    /// </summary>
    /// <remarks>
    /// <b>No platform scope, deliberately.</b> <c>feature_flags</c> carries the
    /// <c>feature_flags_read</c> policy — <c>FOR SELECT USING (true)</c> from
    /// <c>Rls.EnableGlobalReadPlatformWriteRls</c> — because a flag is a property of the deployment and
    /// has no <c>org_id</c> to key on, so a context-free read returns every row rather than zero
    /// (<c>CapabilityTenancyTests.Feature_flags_are_tenant_readable_but_only_platform_writable</c> pins
    /// exactly that). Opening <see cref="LeaseBook.Web.Tenancy.PlatformScopedExecutor"/> here would add
    /// a transaction and the seam's only privilege escape for no gain. The other three platform tables
    /// would need it; this one does not.
    /// <para>
    /// <b>A database that cannot be reached must not stop the host from binding.</b> Two distinct
    /// failures live in this method and they get opposite answers. A query that SUCCEEDS and finds
    /// unknown rows is drift, handled per environment above. A query that cannot CONNECT is an outage,
    /// and refusing to boot on it would defeat the very design this task exists to build: the readiness
    /// gate's premise is that a replica which comes up while Postgres is degraded stays out of rotation
    /// instead of serving "everything off". That premise only holds while the host actually binds. If
    /// this threw, the process would die before <c>app.Run()</c>, ACA would crash-loop the revision, and
    /// no probe tuning could recover it — the replica would never be alive long enough to be judged
    /// not-ready. So connection-level failures are logged and swallowed in EVERY environment, and the
    /// readiness probe takes it from there.
    /// </para>
    /// <para>
    /// Caught narrowly, and by walking the exception chain rather than matching the thrown type — see
    /// <see cref="IsUnreachable"/>, which explains why the obvious version of this filter silently never
    /// fires. A <see cref="PostgresException"/> anywhere in the chain means the server answered and
    /// rejected us — a missing table or a revoked grant — which is a deployment defect that should still
    /// surface, not a transient outage to ride out.
    /// </para>
    /// </remarks>
    /// <param name="environment">
    /// Decides the reaction, not the detection: Production logs, everything else throws. See the class
    /// remarks for why rollback safety wins over fail-fast here specifically.
    /// </param>
    /// <exception cref="InvalidOperationException">
    /// One or more rows name no registered capability, outside Production.
    /// </exception>
    public static async Task ValidateAsync(
        IServiceProvider services, IHostEnvironment environment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(environment);

        // A scope of our own: this runs from the root provider at startup, where no request or job
        // scope exists to inherit.
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DbContext>();

        List<string> names;
        try
        {
            // Ordered so the message is stable across runs — an operator diffing two boot failures
            // should not have to account for Postgres's physical row order.
            names = await db.Database
                .SqlQuery<string>($"""SELECT name AS "Value" FROM feature_flags ORDER BY name""")
                .ToListAsync(ct);
        }
        catch (Exception ex) when (IsUnreachable(ex))
        {
            Logger(scope).LogError(
                ex,
                "Could not read feature_flags at startup to validate the capability registry; continuing " +
                "so the host binds. Drift, if any, is unchecked for this boot. The readiness probe holds " +
                "this replica out of rotation until the capability seam is reachable.");
            return;
        }

        var unknown = names.Where(name => !CapabilityCatalog.TryGet(name, out _)).ToList();
        if (unknown.Count == 0)
        {
            return;
        }

        var detail =
            $"feature_flags holds {unknown.Count} row(s) naming no registered capability: " +
            $"{string.Join(", ", unknown)}. The registry (Capabilities.All) is the source of truth for " +
            "what exists; a row it does not name is ignored at resolution time, so the flag would " +
            "silently do nothing. Add the capability to the registry, or delete the row.";

        if (!environment.IsProduction())
        {
            throw new InvalidOperationException(detail);
        }

        // Production: report and carry on. The rows are inert, and refusing to boot here would strand
        // any rollback to a revision whose registry predates them. Logged at Error so it reaches the
        // same alerting path as the nightly sweep's violations rather than being lost in Information.
        Logger(scope).LogError(
            "Capability registry drift detected at startup; continuing because the rows are inert. {Detail}",
            detail);
    }

    /// <summary>
    /// True when the failure is "the server could not be reached", false when it is "the server
    /// answered and said no".
    /// </summary>
    /// <remarks>
    /// <b>The chain has to be walked, not the top frame matched.</b> EF Core wraps a transient
    /// provider failure in an <see cref="InvalidOperationException"/> reading "An exception has been
    /// raised that is likely due to a transient failure", with the real
    /// <see cref="NpgsqlException"/> underneath. A filter written against the exception type actually
    /// thrown therefore never fires against a live unreachable database, only against a hand-thrown
    /// test double — which is precisely the trap
    /// <c>An_unreachable_database_does_not_stop_the_host_from_binding</c> caught.
    /// <para>
    /// <see cref="PostgresException"/> is checked BEFORE its <see cref="NpgsqlException"/> base and
    /// returns false: a server-side error such as a missing table or a revoked grant means the
    /// connection worked perfectly and the deployment is broken. That must keep surfacing rather than
    /// being ridden out as an outage.
    /// </para>
    /// </remarks>
    private static bool IsUnreachable(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException:
                    return false;
                case NpgsqlException or SocketException or TimeoutException:
                    return true;
            }
        }

        return false;
    }

    private static ILogger<CapabilityRegistryValidatorMarker> Logger(IServiceScope scope) =>
        scope.ServiceProvider.GetRequiredService<ILogger<CapabilityRegistryValidatorMarker>>();
}

/// <summary>
/// Log-category anchor for <see cref="CapabilityRegistryValidator"/>, which is static and so cannot be
/// a generic argument to <see cref="ILogger{TCategoryName}"/>. Named rather than reusing an unrelated
/// type so the category an operator filters on says what produced the entry.
/// </summary>
public sealed class CapabilityRegistryValidatorMarker
{
}
