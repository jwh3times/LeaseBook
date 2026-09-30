namespace LeaseBook.SharedKernel.Tenancy;

/// <summary>
/// Which slice of its organization a unit of work may reach at the database (#314, ADR-048). The
/// organization boundary is <c>app.org_id</c>; this is the boundary <i>inside</i> it, carried in
/// <c>app.persona</c> by <see cref="OrgScopedExecutor"/> and enforced by a restrictive row-level
/// security gate on every org-scoped table.
/// <para>
/// <see cref="None"/> is the default on purpose. It is what an unrecognised or mixed role set
/// resolves to, and the gate admits nothing for it — so forgetting to state a persona fails closed
/// rather than reading as the whole organization.
/// </para>
/// <para>
/// A bare enum keeps <c>SharedKernel</c> free of any role vocabulary: the host owns turning an
/// authenticated principal's roles into one of these (<c>PersonaResolver</c>).
/// </para>
/// </summary>
public enum Persona
{
    /// <summary>Any mix of a portal role with another role, or no recognised role. Admits nothing.</summary>
    None = 0,

    /// <summary>A named system process — a job, a worker, a seeder, a CLI verb. Organization-wide.</summary>
    System,

    /// <summary>A user holding only PMAdmin and/or PMStaff. Organization-wide.</summary>
    Staff,

    /// <summary>A user holding only Tenant. Only the rows granted through its active resident link.</summary>
    Tenant,

    /// <summary>A user holding only Owner. Only the rows granted through its active owner link.</summary>
    Owner,
}

/// <summary>The persisted vocabulary of <see cref="Persona"/>.</summary>
public static class PersonaValues
{
    /// <summary>
    /// The value <see cref="OrgScopedExecutor"/> writes to <c>app.persona</c>. The row-level security
    /// gate compares against exactly these strings, so an out-of-range enum value is refused rather
    /// than rendered as something the gate might match.
    /// </summary>
    public static string ToDbValue(this Persona persona) => persona switch
    {
        Persona.None => "none",
        Persona.System => "system",
        Persona.Staff => "staff",
        Persona.Tenant => "tenant",
        Persona.Owner => "owner",
        _ => throw new ArgumentOutOfRangeException(nameof(persona), persona, "Not a defined persona."),
    };

    /// <summary>True for the two personas whose grants are resolved from a portal link.</summary>
    public static bool IsPortal(this Persona persona) => persona is Persona.Tenant or Persona.Owner;
}
