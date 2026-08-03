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
/// <b>Fail the host, not the request.</b> Same call as the Hangfire storage decision in
/// <c>Program.cs</c>: the alternative is a replica that comes up reporting healthy while its
/// configuration means something other than what the operator wrote. Boot loudly and let the platform
/// hold the previous revision.
/// </para>
/// <para>
/// <b>Every unknown name, not the first.</b> A rename usually lands as a pair (the new row inserted,
/// the old one left behind) and a bad deploy can strand several at once. Throwing on the first would
/// turn one fix into N boot-fix-boot cycles, each costing a full deployment.
/// </para>
/// </summary>
public static class CapabilityRegistryValidator
{
    /// <summary>
    /// Reads every <c>feature_flags</c> name and throws once if any of them is unknown to the registry.
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
    /// <exception cref="InvalidOperationException">One or more rows name no registered capability.</exception>
    public static async Task ValidateAsync(IServiceProvider services, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(services);

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

        throw new InvalidOperationException(
            $"feature_flags holds {unknown.Count} row(s) naming no registered capability: " +
            $"{string.Join(", ", unknown)}. The registry (Capabilities.All) is the source of truth for " +
            "what exists; a row it does not name is ignored at resolution time, so the flag would " +
            "silently do nothing. Add the capability to the registry, or delete the row.");
    }
}
