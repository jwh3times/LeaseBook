namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// An earlier committed run for the SAME (org, run type, period) recorded a different money-path
/// capability state than the one resolved at this confirm's entry (ADR-028). Raised by
/// <see cref="RunEngine.ConfirmAsync"/> before the strategy runs, so a rejected confirm posts
/// nothing.
/// <para>
/// <b>The hazard this closes, which the freeze and the preview guard do not.</b> Both of those make
/// ONE run internally consistent. The repo's designed recovery path is a re-run — <c>source_ref</c>
/// uniqueness makes already-posted items land as <c>Skipped</c> (ADR-019 §2) — so a period is
/// routinely built by more than one run. Run 1 confirms a selection while a money-path capability is
/// off; the flag flips; run 2 confirms the remainder while it is on. Each run is internally
/// consistent and the period is not. Recording the state in <c>summary_json</c> explains that
/// afterwards; only refusing the second confirm prevents it.
/// </para>
/// <para>
/// <b>Overridable, unlike <see cref="CapabilitiesChangedException"/>.</b> A stale preview token is
/// always the operator's mistake to re-take, and re-previewing costs nothing. This one can be the
/// intended action: the capability change may be exactly why the period is being re-run. So the
/// confirm accepts an explicit acknowledgement, and a run that used it says so in its
/// <c>summary_json</c> — the override is recorded, never silent.
/// </para>
/// </summary>
public sealed class CapabilitiesChangedSincePriorRunException : OperationsDomainException
{
    /// <summary>
    /// The stable wire code. Deliberately NOT <c>capabilities_changed</c>: a client that re-previews
    /// on that code would loop forever here, because a fresh preview cannot change what an already
    /// committed run recorded.
    /// </summary>
    public const string ErrorCode = "capabilities_changed_since_prior_run";

    private const string DefaultMessage =
        "An earlier run for this period was posted while a different set of features was in effect, " +
        "so continuing would compute one period two ways. Re-run it deliberately if that is what you " +
        "intend, or restore the earlier feature state first.";

    public CapabilitiesChangedSincePriorRunException()
        : base(ErrorCode, DefaultMessage)
    {
    }

    public CapabilitiesChangedSincePriorRunException(string message)
        : base(ErrorCode, message)
    {
    }
}
