namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// The confirm carried a capability-version token that no longer matches the set resolved at confirm
/// entry (ADR-028). Raised by <see cref="RunEngine.ConfirmAsync"/> before the strategy runs, so a
/// rejected run confirmation posts nothing.
/// <para>
/// <b>Typed, not a bare <c>InvalidOperationException</c>.</b> ADR-025's terminal handler suppresses
/// an untyped message entirely and returns an uncoded 500, which would turn a recoverable
/// "re-preview and try again" into an opaque failure — the same reclassification that ADR-025 made
/// for <c>no_trust_account</c>. The host maps <see cref="OperationsDomainException.Code"/> to 409
/// through <c>ProblemResults</c>.
/// </para>
/// <para>
/// <b>The message carries no diagnostic detail on purpose.</b> Neither version token appears in it.
/// They are opaque digests that mean nothing to an operator, and ADR-025's error-content rule keeps
/// internal identifiers off the wire; the run confirmation's telemetry span already tags the resolved version
/// for the engineer side, and the log line the handler writes carries the correlation id.
/// </para>
/// </summary>
public sealed class CapabilitiesChangedException : OperationsDomainException
{
    /// <summary>The stable wire code. The SPA branches on exactly this value to re-fetch its preview.</summary>
    public const string ErrorCode = "capabilities_changed";

    private const string DefaultMessage =
        "The features available to this account changed while you were reviewing this run, so the " +
        "amounts shown may no longer be what would post. Reload the preview and confirm again.";

    public CapabilitiesChangedException()
        : base(ErrorCode, DefaultMessage)
    {
    }

    public CapabilitiesChangedException(string message)
        : base(ErrorCode, message)
    {
    }
}
