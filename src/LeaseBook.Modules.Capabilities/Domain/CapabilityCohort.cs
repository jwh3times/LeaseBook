namespace LeaseBook.Modules.Capabilities.Domain;

/// <summary>
/// A targeting rule: this org (optionally narrowed to one user in it) is in the rollout cohort for
/// a capability. Cohort membership is an OR over an otherwise-off flag — the beta case.
/// <para>
/// <see cref="OrgId"/> is ALWAYS present; <see cref="UserId"/> narrows it to a single user within
/// that org. Carrying the pair is deliberate: <c>asp_net_users</c> is itself RLS-exempt, so a bare
/// user id could not be validated against its org.
/// </para>
/// <para>
/// <b>Not IOrgScoped</b> — see <see cref="Entitlement"/> for why that interface would silently break
/// cross-org platform reads. Membership is mutable by design (the platform plane may update and
/// delete rows), so unlike entitlements this table is not append-only.
/// </para>
/// </summary>
public sealed class CapabilityCohort
{
    public Guid Id { get; set; }

    public string Capability { get; set; } = string.Empty;

    public Guid OrgId { get; set; }

    /// <summary>Null for an org-wide cohort row; set to narrow the rule to one user in that org.</summary>
    public Guid? UserId { get; set; }

    public DateTime AddedAt { get; set; }

    public string AddedBy { get; set; } = string.Empty;
}
