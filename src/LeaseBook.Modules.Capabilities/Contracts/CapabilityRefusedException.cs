namespace LeaseBook.Modules.Capabilities.Contracts;

/// <summary>
/// A capability write refused on domain grounds — the organization does not exist, there is no
/// explicit flag override to clear, the named user is not in the organization, the cohort rule being
/// removed is not there.
/// <para>
/// <b>Throwing is the rollback.</b> These refusals are decided <i>inside</i> the write transaction,
/// after a read the argument parser could not perform, so raising is what leaves neither a state row
/// nor an audit row behind. A refusal that returned a value instead would have to unwind the
/// transaction by hand at every call site.
/// </para>
/// <para>
/// Distinct from a provider error on purpose. This says <b>the domain refused</b>; a
/// <c>PostgresException</c> says the database rejected the statement. The host maps the latter to
/// operator prose — it owns the Npgsql dependency — and simply prints the message of the former,
/// because a domain refusal already knows what to say.
/// </para>
/// </summary>
public sealed class CapabilityRefusedException(string message) : Exception(message);
