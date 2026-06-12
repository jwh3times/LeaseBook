namespace LeaseBook.SharedKernel;

/// <summary>
/// Marks an entity as belonging to exactly one org. The EF model convention (WP-05) applies a
/// global query filter and a stamping interceptor to every <see cref="IOrgScoped"/> entity, and
/// its migration must call <c>EnableOrgRls</c>. RLS is the boundary; this is the ergonomic layer.
/// </summary>
public interface IOrgScoped
{
    Guid OrgId { get; set; }
}
