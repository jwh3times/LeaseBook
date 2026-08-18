namespace LeaseBook.Web.Audit;

/// <summary>
/// How an audit row names who acted. One implementation for both readers, because the per-entry
/// trail and the compliance extract must not describe the same row two different ways.
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
    public static string For(string? displayName, string? email, string? process) =>
        displayName
        ?? email
        ?? (string.IsNullOrEmpty(process) ? "System" : $"System ({process})");
}
