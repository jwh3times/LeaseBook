using LeaseBook.SharedKernel;
using LeaseBook.Web.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace LeaseBook.Web.Audit;

/// <summary>
/// The <c>entity_type</c> vocabulary of <c>audit_events</c> — what the review surface (#321) offers as
/// its entity filter.
/// <para>
/// Derived from the EF model rather than from data: a <c>SELECT DISTINCT entity_type</c> answers "what
/// has this org written so far", which makes the filter list shrink on a quiet org and grow as rows
/// accumulate, and costs a scan of the largest table in the database to say so. The model already knows
/// the full vocabulary, because the auditing pass in <see cref="Persistence.AppDbContext"/> writes one
/// row per tracked change of an <see cref="IOrgScoped"/> entity and nothing else.
/// </para>
/// <para>
/// <see cref="Synthetic"/> is the one part that cannot be derived — the hand-written audit rows whose
/// <c>entity_type</c> names a domain event rather than a table. <c>AuditVocabularyDriftTests</c> scans
/// source for those literals, so adding one without listing it here fails the build.
/// </para>
/// </summary>
public static class AuditEntityTypes
{
    /// <summary>
    /// Audit rows written by hand, whose <c>entity_type</c> is a domain-event name rather than a table.
    /// Kept in one place so the entity filter can offer them and the drift guard can check them.
    /// </summary>
    public static readonly IReadOnlyList<string> Synthetic =
    [
        "account-security",
        "audit-export-generated",
        "compliance-pack-generated",
        "import-superseded",
        "migration-signed-off",
        "org-provisioned",
    ];

    /// <summary>
    /// Every <c>entity_type</c> an audit row can carry: the table name of each audited entity, plus
    /// <see cref="Synthetic"/>. Ordinal-sorted so the filter list is stable between requests.
    /// </summary>
    public static IReadOnlyList<string> All(IModel model) =>
    [
        .. model.GetEntityTypes()
            .Where(entity => typeof(IOrgScoped).IsAssignableFrom(entity.ClrType))
            // audit_events is never audited (no recursion) so it is never an entity_type.
            .Where(entity => entity.ClrType != typeof(AuditEvent))
            .Select(entity => entity.GetTableName())
            .Where(table => table is not null)
            .Select(table => table!)
            .Concat(Synthetic)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(table => table, StringComparer.Ordinal),
    ];
}
