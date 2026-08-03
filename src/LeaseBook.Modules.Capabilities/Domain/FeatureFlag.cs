namespace LeaseBook.Modules.Capabilities.Domain;

/// <summary>
/// A deployment-wide kill switch / rollout toggle, keyed by capability name. Genuinely global: a
/// flag is a property of the deployment, not of a tenant, so there is no OrgId here at all.
/// <para>
/// Deliberately mutable state, unlike <see cref="Entitlement"/> — <c>feature_flags</c> keeps its
/// UPDATE/DELETE grants so the platform CLI can flip a switch. RLS makes the table readable in any
/// session (the resolver reads a kill switch inside the ambient request transaction, where platform
/// scope is unavailable and unsafe to fake) but writable only from the platform plane (ADR-028).
/// </para>
/// </summary>
public sealed class FeatureFlag
{
    /// <summary>The capability name from the source-code registry. Also the primary key.</summary>
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    /// <summary>Free-text operator note; null when the flag was created without one.</summary>
    public string? Description { get; set; }

    public DateTime UpdatedAt { get; set; }

    public string UpdatedBy { get; set; } = string.Empty;
}
