namespace LeaseBook.Modules.Capabilities.Registry;

/// <summary>
/// How old a capability is allowed to get, and where the registry that answers it lives (ADR-028).
/// <para>
/// <b>This is the module's own knowledge, and that is why it sits here rather than in the host.</b>
/// The policy window is a fact about capabilities; <see cref="IsStale"/> is a fact about capabilities;
/// and <see cref="RegistryRelativePath"/> is where this module keeps its catalog. All three used to
/// live in <c>LeaseBook.Web</c>, which meant the module's own file layout was pinned in host code —
/// so moving the registry would break a file in another project.
/// </para>
/// <para>
/// <b>What deliberately did NOT come with it: the probe.</b> Resolving an actual introduction date
/// means walking up the filesystem for the repository root and shelling out to <c>git</c>. That is
/// development-environment plumbing, not domain logic, and no module in this solution starts a
/// subprocess. It stays in the host as <c>CapabilityAgeProbe</c>, which consumes the three values
/// below. The module states the policy; the host does the digging.
/// </para>
/// </summary>
public static class CapabilityAge
{
    /// <summary>
    /// How long a money-path capability may live. Money-path capabilities are short-lived BY POLICY —
    /// created for a rollout, deleted after — and the whole design's tolerance for gating a posting
    /// path on a flag rests on that. One that lives on is standing risk on the books.
    /// </summary>
    public static readonly TimeSpan PolicyWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// The file whose history dates each capability, relative to the repository root, in git's own
    /// forward-slash form (git accepts it as a pathspec on Windows too).
    /// <para>
    /// Pinned as a constant and asserted to resolve by <c>CapabilityAgeTests</c>, because the pathspec
    /// is how this whole mechanism goes quiet: move or rename the registry without updating this
    /// string and every probe returns "no history", every capability becomes unknown, and the gate
    /// skips forever while looking perfectly healthy.
    /// </para>
    /// <para>
    /// It names a path inside this module, which is the reason the constant belongs to the module. A
    /// file that describes where this module keeps its catalog should move when the catalog moves,
    /// and it can only do that reliably if it lives beside it.
    /// </para>
    /// </summary>
    public const string RegistryRelativePath =
        "src/LeaseBook.Modules.Capabilities/Registry/Capabilities.cs";

    /// <summary>
    /// The one definition of "stale", shared by the CLI report and the CI gate.
    /// <para>
    /// <b>Both consumers share this one implementation on purpose.</b> <c>capabilities list --stale</c>
    /// reports the ages and <c>CapabilityAgeTests</c> fails CI on them; if the two computed staleness
    /// separately they could disagree, and the operator-facing report saying "within window" while CI
    /// says otherwise (or worse, the reverse) would discredit both.
    /// </para>
    /// <para>
    /// <b><see cref="Capability.IsFixture"/> is exempt, and that exemption is why the field exists.</b>
    /// <c>money-path-fixture</c> is permanent by design — it exists so the money-path machinery has
    /// something real to exercise — so without this it would arm the gate 90 days after it was added
    /// and fail CI on an unrelated PR, for a fixture doing exactly its job.
    /// </para>
    /// </summary>
    public static bool IsStale(Capability capability, DateTimeOffset introducedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capability);

        return capability.IsMoneyPath
            && !capability.IsFixture
            && now - introducedAt > PolicyWindow;
    }
}

/// <summary>
/// What the age probe saw. Either an introduction date per capability, or a reason it saw nothing —
/// never a silent empty map, because "no stale capabilities" and "I could not look" are the two
/// answers this mechanism must never confuse.
/// <para>
/// The shape lives with the policy rather than with the probe that fills it: a second probe — a
/// different VCS, a build-stamped manifest — would answer the same question and must not be free to
/// answer it in a shape that drops the unavailable case.
/// </para>
/// </summary>
public sealed record CapabilityAgeReport(
    IReadOnlyDictionary<string, DateTimeOffset> IntroducedAt,
    string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;

    public static CapabilityAgeReport Unavailable(string reason) =>
        new(new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal), reason);
}
