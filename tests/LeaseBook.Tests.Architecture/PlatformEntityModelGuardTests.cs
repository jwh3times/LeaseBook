using LeaseBook.Modules.Capabilities.Domain;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Persistence;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The one-token trap in the whole capability seam (ADR-028), guarded.
/// <para>
/// <c>AppDbContext</c> applies the org global query filter <b>by convention</b> to every entity that
/// implements <see cref="IOrgScoped"/> — there is no per-entity opt-in. So adding <c>: IOrgScoped</c>
/// to <see cref="Entitlement"/> is a one-token change that silently empties every cross-org platform
/// read that RLS deliberately permits. It fails <i>asymmetrically</i>, which is why it needs a guard
/// rather than trust: the write side throws loudly (the tenancy pass demands a tenant context), so a
/// developer making the change sees writes break and "fixes" them, while the read side just returns
/// zero rows and no existing test goes red.
/// </para>
/// <para>
/// The model is built through <see cref="AppDbContextDesignTimeFactory"/> — the same model
/// <c>dotnet ef</c> generates migrations from — so this asserts on the real thing, not a replica.
/// No database is touched: EF builds the model without opening a connection.
/// </para>
/// </summary>
public sealed class PlatformEntityModelGuardTests
{
    /// <summary>
    /// The three platform tables that carry an <c>org_id</c>-shaped column and are therefore one
    /// keystroke away from the trap. <c>FeatureFlag</c> has no org column at all, so it cannot
    /// plausibly acquire the interface and is left out on purpose.
    /// </summary>
    private static readonly Type[] PlatformEntities =
    [
        typeof(Entitlement),
        typeof(CapabilityCohort),
        typeof(PlatformAuditEvent),
    ];

    private const string Why =
        "Platform tables are data ABOUT orgs, not tenant data belonging to one. IOrgScoped is what " +
        "makes AppDbContext attach the org global query filter (and org stamping, the CrossOrg write " +
        "guard, and the audit pass) — attaching it here silently reduces every cross-org platform " +
        "read to zero rows while RLS would have allowed it, and nothing else in the suite goes red. " +
        "Isolation for these tables is RLS plus the single platform escape (ADR-028).";

    [Fact]
    public void Platform_entities_do_not_implement_IOrgScoped()
    {
        var offenders = PlatformEntities
            .Where(t => typeof(IOrgScoped).IsAssignableFrom(t))
            .Select(t => t.Name)
            .ToArray();

        offenders.ShouldBeEmpty($"{string.Join(", ", offenders)} must not implement IOrgScoped. {Why}");
    }

    [Fact]
    public void Platform_entities_carry_no_global_query_filter()
    {
        using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        var offenders = new List<string>();
        foreach (var clrType in PlatformEntities)
        {
            var entityType = db.Model.FindEntityType(clrType)
                ?? throw new InvalidOperationException(
                    $"{clrType.Name} is not in the EF model — its IEntityTypeConfiguration is missing " +
                    "or its assembly is absent from PersistenceAssemblies.ModelAssemblies.");

            var filters = FiltersOf(entityType);
            if (filters.Count > 0)
            {
                offenders.Add($"{clrType.Name} has {filters.Count} query filter(s)");
            }
        }

        offenders.ShouldBeEmpty($"{string.Join("; ", offenders)}. {Why}");
    }

    /// <summary>
    /// The positive control. Without it the test above would pass vacuously if the EF metadata API it
    /// reads ever stopped reporting filters — the guard would go green for exactly the wrong reason.
    /// <c>AuditEvent</c> is real tenant data and MUST be filtered.
    /// </summary>
    [Fact]
    public void An_org_scoped_entity_does_carry_a_query_filter()
    {
        using var db = new AppDbContextDesignTimeFactory().CreateDbContext([]);

        var auditEvents = db.Model.FindEntityType(typeof(AuditEvent)).ShouldNotBeNull();

        FiltersOf(auditEvents).ShouldNotBeEmpty(
            "AuditEvent is IOrgScoped, so AppDbContext's convention must have attached the org filter. " +
            "If this is empty the sibling guard above is asserting nothing.");
    }

    private static IReadOnlyList<IQueryFilter> FiltersOf(IEntityType entityType) =>
        entityType.GetDeclaredQueryFilters().ToList();
}
