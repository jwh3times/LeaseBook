namespace LeaseBook.Modules.Operations.Runs;

/// <summary>
/// An earlier committed run for the SAME (org, run type, period) recorded a different money-path
/// capability state than the one resolved at this run confirmation's entry (ADR-028). Raised by
/// <see cref="RunEngine.ConfirmAsync"/> before the strategy runs, so a rejected run confirmation posts
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

    /// <summary>
    /// The ordinary case: a money-path capability the registry still defines resolved differently for
    /// the two runs. Both remedies are open — put the feature state back, or acknowledge deliberately.
    /// </summary>
    private const string StateMovedMessage =
        "An earlier run for this period was posted while a different set of features was in effect, " +
        "so continuing would compute one period two ways. Restore the earlier feature state, or " +
        "re-run it deliberately if computing the period this way is what you intend.";

    /// <summary>
    /// The registry-moved case, which needs its own words in BOTH directions. The money-path names
    /// come from source code, not from <c>feature_flags</c>: no operator action deletes a capability
    /// this release added, and none resurrects one it removed. Offering "restore the earlier feature
    /// state" would send them hunting for a control that does not exist, so this message offers only
    /// the route that works.
    /// </summary>
    private const string RegistryMovedMessage =
        "An earlier run for this period was posted when a different set of features existed, so " +
        "continuing would compute one period two ways. That earlier state cannot be restored — which " +
        "features exist changed with this release — so this run has to be confirmed deliberately if " +
        "computing the period this way is what you intend.";

    private CapabilitiesChangedSincePriorRunException(string message)
        : base(ErrorCode, message)
    {
    }

    /// <summary>A live money-path capability resolved differently than it did for the prior run.</summary>
    public static CapabilitiesChangedSincePriorRunException StateMoved() => new(StateMovedMessage);

    /// <summary>
    /// The set of money-path capabilities the registry defines changed between the two runs — one was
    /// added, removed, or both. Same wire code, because it is the same conflict and clients branch on
    /// the code; different message, because only one of the two remedies is actually available.
    /// </summary>
    public static CapabilitiesChangedSincePriorRunException RegistryMoved() => new(RegistryMovedMessage);
}
