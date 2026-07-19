namespace LeaseBook.Web.Auth;

/// <summary>
/// The fixed role set (WP-06). <see cref="Owner"/> and <see cref="Tenant"/> are dormant in Phase 1 —
/// seeded so portal personas (Phase 2–3) slot in without a migration, but no endpoints grant them yet.
/// </summary>
public static class Roles
{
    public const string PMAdmin = "PMAdmin";
    public const string PMStaff = "PMStaff";
    public const string Owner = "Owner";
    public const string Tenant = "Tenant";

    public static readonly string[] All = [PMAdmin, PMStaff, Owner, Tenant];
}

/// <summary>Named authorization policies. Admin is a superset of staff.</summary>
public static class AuthPolicies
{
    public const string RequirePMAdmin = "RequirePMAdmin";
    public const string RequirePMStaff = "RequirePMStaff";
    public const string AuthenticatedMfaExempt = "AuthenticatedMfaExempt";
}
