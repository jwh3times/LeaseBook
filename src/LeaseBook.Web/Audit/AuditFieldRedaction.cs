namespace LeaseBook.Web.Audit;

/// <summary>
/// Which audited fields the review surface (#321) refuses to render. The list surface shows only
/// metadata; the detail drawer shows the <c>before</c>/<c>after</c> column snapshots, and this decides
/// what a snapshot is allowed to say.
/// <para>
/// <b>Credentials are not the exposure here.</b> The auditing pass writes one row per tracked change of
/// an <see cref="SharedKernel.IOrgScoped"/> entity, and <c>AppUser</c> is deliberately not one — Identity
/// tables are identity-class, so no password hash, security stamp or recovery code has ever been written
/// to <c>audit_events</c>. The hand-written <c>account-security</c> rows carry attribution and no payload
/// at all, for the same reason.
/// </para>
/// <para>
/// <b>The exposure is content the product does not author.</b> <c>ImportRow.RawJson</c> holds a row of the
/// customer's previous system's export verbatim — whatever columns that system chose to include, which is
/// outside LeaseBook's schema and outside its judgement. <c>MappedJson</c> and <c>ErrorsJson</c> are
/// derived from it and quote it back. Rendering those in a drawer would republish an unbounded payload
/// through a surface whose whole purpose is to be read by a person, so they are redacted to a marker and
/// the reviewer is sent to the import batch, which shows the same data under its own gating.
/// </para>
/// <para>
/// <see cref="SensitiveNameFragments"/> is defence in depth for the column that does not exist yet:
/// it redacts on name, so a secret added to an audited entity is masked from the day it lands rather
/// than from the day someone notices. <c>AuditPayloadExposureTests</c> is the other half — it fails the
/// build when an audited entity gains a <c>*Json</c> property that nobody has classified as
/// LeaseBook-authored or externally-sourced, gains a secret-named one, or becomes an Identity type. It
/// keys the free-form arm on the <c>Json</c> suffix, which is the shape every such column has taken so
/// far; a payload column named something else is <b>not</b> caught, and the list above is where it
/// would have to be added by hand.
/// </para>
/// </summary>
public static class AuditFieldRedaction
{
    /// <summary>What a redacted value renders as. A marker, not an empty string: the reviewer must be
    /// able to tell "this field changed and you may not read it" from "this field was cleared".</summary>
    public const string Marker = "[redacted]";

    /// <summary>
    /// Audited properties whose content originates outside LeaseBook's schema. Matched by property name
    /// because all three exist only on <c>ImportRow</c>; a same-named property elsewhere would be the
    /// same kind of payload and redacting it too is the safe direction.
    /// </summary>
    public static readonly IReadOnlyList<string> UnboundedContentFields =
    [
        "RawJson",
        "MappedJson",
        "ErrorsJson",
    ];

    /// <summary>
    /// Name fragments that mark a value as a secret. Matched case-insensitively anywhere in the property
    /// name, so <c>PasswordHash</c>, <c>ApiKey</c> and <c>RefreshToken</c> all redact without anyone
    /// having to enumerate them first.
    /// </summary>
    public static readonly IReadOnlyList<string> SensitiveNameFragments =
    [
        "password",
        "secret",
        "token",
        "credential",
        "apikey",
        "privatekey",
        "securitystamp",
        "passwordhash",
        "ssn",
        "taxid",
        "routingnumber",
        "accountnumber",
    ];

    /// <summary>True when <paramref name="field"/>'s value must never leave the server.</summary>
    public static bool IsRedacted(string field) =>
        UnboundedContentFields.Contains(field, StringComparer.OrdinalIgnoreCase)
        || SensitiveNameFragments.Any(fragment =>
            field.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
