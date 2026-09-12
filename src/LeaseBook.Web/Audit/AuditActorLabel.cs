using LeaseBook.SharedKernel.Tenancy;

namespace LeaseBook.Web.Audit;

/// <summary>
/// How an audit row names who acted. One implementation for all three readers, because the per-entry
/// trail, the compliance extract and the admin review surface must not describe the same row three
/// different ways.
/// </summary>
internal static class AuditActorLabel
{
    /// <summary>
    /// A person by name (or email), otherwise the system — naming the process where the row records
    /// one (ADR-039). Before that, every automated write rendered as a bare "System", which is the
    /// reading an examiner cannot act on: the demo seeder, the nightly invariant sweep and a CLI verb
    /// were one indistinguishable actor.
    /// <para>
    /// Rows written before ADR-039 have no process and still render as plain "System". That is
    /// deliberate — it is what those rows actually say, and inventing a process for them would put a
    /// false attribution in the document an examiner reads.
    /// </para>
    /// </summary>
    /// <param name="actorKind">
    /// The persisted <c>actor_kind</c>, where the caller has it. Without it, a user whose identity row
    /// no longer resolves — deleted, or belonging to another org — falls through to "System", which is
    /// a <b>false</b> attribution rather than a missing one: the row says <c>user</c>, and the review
    /// surface's System filter (which keys on <c>actor_kind</c>) will not return the row it just
    /// labelled System. Passing the kind keeps "we cannot name them" separate from "nobody was there".
    /// </param>
    public static string For(string? displayName, string? email, string? process, string? actorKind = null) =>
        displayName
        ?? email
        ?? (actorKind == Actor.UserKind
            ? "Unknown user"
            : string.IsNullOrEmpty(process) ? "System" : $"System ({process})");
}
