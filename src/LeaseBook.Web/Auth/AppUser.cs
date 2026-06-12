using Microsoft.AspNetCore.Identity;

namespace LeaseBook.Web.Auth;

/// <summary>
/// Application user. Belongs to exactly one org (the v1 rule — multi-org membership is deferred).
/// <para>
/// Deliberately <b>not</b> <see cref="SharedKernel.IOrgScoped"/>: Identity tables are identity-class
/// and carry no RLS, because authentication must succeed <i>before</i> an org context exists
/// (pitfall E6). The <c>org_id</c> column is here so sign-in can mint the <c>org_id</c> claim that
/// the tenancy middleware then uses; isolation of user rows is enforced by app logic, not RLS.
/// </para>
/// </summary>
public sealed class AppUser : IdentityUser<Guid>
{
    public Guid OrgId { get; set; }

    public string? DisplayName { get; set; }
}
