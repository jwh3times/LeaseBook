using Microsoft.EntityFrameworkCore;
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
/// sees a successful write, no error anywhere, and a feature that never turns on. This converts that
/// into a boot failure, which is the only signal an unattended deployment can act on.
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
/// Development and CI are where drift is cheap and actionable, so they throw. The operator-typo case is
/// caught earlier still: the toggle CLI rejects a name that is not in the registry at write time, which
/// is the point at which the typo can be corrected in one step.
/// </para>
/// <para>
/// <b>Every unknown name, not the first</b> — in both branches. A rename usually lands as a pair (the
/// new row inserted, the old one left behind) and a bad deploy can strand several at once. Reporting
/// only the first would turn one fix into N boot-fix-boot cycles, each costing a full deployment.
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

        // Ordered so the message is stable across runs — an operator diffing two boot failures should
        // not have to account for Postgres's physical row order.
        var names = await db.Database
            .SqlQuery<string>($"""SELECT name AS "Value" FROM feature_flags ORDER BY name""")
            .ToListAsync(ct);

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
        scope.ServiceProvider
            .GetRequiredService<ILogger<CapabilityRegistryValidatorMarker>>()
            .LogError(
                "Capability registry drift detected at startup; continuing because the rows are inert. {Detail}",
                detail);
    }
}

/// <summary>
/// Log-category anchor for <see cref="CapabilityRegistryValidator"/>, which is static and so cannot be
/// a generic argument to <see cref="ILogger{TCategoryName}"/>. Named rather than reusing an unrelated
/// type so the category an operator filters on says what produced the entry.
/// </summary>
public sealed class CapabilityRegistryValidatorMarker
{
}
