namespace LeaseBook.Modules.Operations.Contracts;

/// <summary>
/// Operations' own view of a frozen capability set (ADR-028). Deliberately NOT a SharedKernel type
/// and NOT the Capabilities module's <c>CapabilitySet</c>: every module depends on SharedKernel and
/// Accounting is a posting path, so a capability type declared there would be reachable from posting
/// code while every reference-graph test in <c>MoneyPathBoundaryTests</c> stayed green — a gate
/// passing on a rule it no longer enforces. The host adapter maps the real set into this view.
/// <para>
/// <b>What this may and may not decide.</b> A capability may gate whether a posting path is
/// REACHABLE — endpoint, command, or run-strategy selection. It may never change the lines or amounts
/// an existing business event produces; money-affecting parameters live in <c>OrgSettings</c>, which
/// is org-scoped, RLS'd, audited, seeded and golden-pinned. Concretely: no value read off this type
/// may become an argument to an Accounting command, business event, or posting-template input.
/// </para>
/// </summary>
/// <param name="Values">
/// The COMPLETE resolved map, one entry per capability in the registry — not the enabled subset. The
/// producing type asserts that completeness and explains why: an absent entry read as "off" is
/// indistinguishable from a deployment-wide kill switch, and effectively undiagnosable in production.
/// Projecting down to enabled-names-only here would throw that guarantee away one hop later, on the
/// money path, which is the worst place to lose it.
/// </param>
/// <param name="Version">
/// The opaque version token of the resolved set. Recorded in <c>bulk_runs.summary_json</c> so a
/// committed run states which capability state it ran under, and used by the preview/confirm
/// concurrency check.
/// </param>
/// <param name="MoneyPathNames">
/// The names, out of <paramref name="Values"/>, that the registry marks as money-path. Names rather
/// than a second resolved map on purpose: <see cref="MoneyPathState"/> reads their values back out of
/// the one frozen <paramref name="Values"/> map, so the money-path view cannot drift from the set the
/// run actually posts under. A parallel map could.
/// <para>
/// <b>Why the cross-run guard compares this and not <paramref name="Version"/>.</b> The version is a
/// digest of the WHOLE resolved set plus the shape of the source-code registry, so it moves when a
/// deploy adds an unrelated capability or an org is granted a paid one. Comparing it across runs
/// would reject the re-run that ADR-019 §2 makes the designed recovery path — on a change that
/// provably cannot reach a posting path — and train operators to acknowledge every conflict by
/// reflex. Only a money-path capability can make one period compute two ways, so only money-path
/// state is compared.
/// </para>
/// </param>
public sealed record RunCapabilities(
    IReadOnlyDictionary<string, bool> Values,
    string Version,
    IReadOnlySet<string> MoneyPathNames)
{
    /// <summary>
    /// Answers for a resolved capability, and THROWS for anything else rather than answering "off".
    /// <para>
    /// A typo'd, renamed or retired name is a bug in the calling code, not a state. Answering false
    /// would mean a money-path gate silently closes: charges quietly do not post, every downstream
    /// figure is internally consistent, and nothing anywhere records that a gate was consulted with a
    /// name that no longer exists. Throwing turns a silent fiduciary failure into a loud one, which
    /// on this path is strictly the better direction.
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="capabilityName"/> is not in the resolved set.
    /// </exception>
    public bool IsEnabled(string capabilityName) =>
        Values.TryGetValue(capabilityName, out var enabled)
            ? enabled
            : throw new ArgumentOutOfRangeException(
                nameof(capabilityName), capabilityName,
                "Not a resolved capability. A silent false here would be indistinguishable from a " +
                "kill switch. Capability names come from the registry; a literal that no longer " +
                "resolves is a bug, not an 'off'.");

    /// <summary>Names that resolved to "on", ordered, for recording in a run summary.</summary>
    public IReadOnlyList<string> EnabledNames() =>
        Values.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToArray();

    /// <summary>
    /// The money-path capabilities and their resolved values, as ordered <c>name=on</c> /
    /// <c>name=off</c> entries. This is what a committed run records in <c>summary_json</c> and what
    /// the cross-run period guard compares against a prior run for the same period.
    /// <para>
    /// <b>Readable entries, not a hash.</b> The comparison is between two runs that may be weeks and
    /// a deploy apart, and its rejection asks a human to make a money decision. "off → on for
    /// <c>x</c>" is a decidable question; "digest A ≠ digest B" is not, and nothing can recover the
    /// state from a digest after the fact.
    /// </para>
    /// <para>
    /// <b>BOTH values, not the enabled subset.</b> A newly-added money-path capability that resolves
    /// off must still make the state differ from a prior run that predates it — the set of gates
    /// consulted moved, even though nothing turned on. Recording only the "on" names would make the
    /// two states compare equal and the guard silently miss it.
    /// </para>
    /// <para>
    /// Compared element-wise, never as a joined string, so the encoding needs no delimiter escaping:
    /// names are unique, so an ordered list of pairs is already injective.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> MoneyPathState() =>
        MoneyPathNames
            .Order(StringComparer.Ordinal)
            .Select(name => $"{name}={(IsEnabled(name) ? "on" : "off")}")
            .ToArray();

    /// <summary>
    /// True when the set of money-path capability NAMES has changed between the run that recorded
    /// <paramref name="recordedState"/> and this one — in either direction. A capability was added to
    /// the registry, or removed from it, or both.
    /// <para>
    /// <b>Why this case needs its own message.</b> The names come from the REGISTRY, which is source
    /// code, not from <c>feature_flags</c>. So when the set of names moves, the earlier state cannot
    /// be reproduced by any operator action: there is no switch that deletes a capability the release
    /// added, and none that resurrects one it removed. The ordinary remedy — put the feature state
    /// back the way it was — is unavailable, and offering it would send the operator hunting for a
    /// control that does not exist. Only a deliberate acknowledgement clears it.
    /// </para>
    /// <para>
    /// <b>Symmetric on purpose.</b> ADDITION is the commoner deploy and is exactly as period-breaking
    /// as removal: a prior run's <c>["a=off"]</c> and a new <c>["a=off", "b=off"]</c> differ, and no
    /// flag write can take <c>b</c> off the list. An asymmetric predicate that answered only for
    /// removal would hand every addition the impossible remedy — the same failure this exists to
    /// prevent, in the mirror direction.
    /// </para>
    /// <para>
    /// The comparison itself does NOT filter these names out. A run that posted while a gate was live
    /// and a run that posted before that gate existed (or after it was deleted) really are two
    /// behaviours, so the difference is real and belongs on the screen. What changes is only which
    /// remedy the message offers.
    /// </para>
    /// <para>
    /// Answered from <see cref="MoneyPathNames"/> rather than from a registry lookup, because
    /// Operations must not reference the Capabilities module — the port already carries exactly the
    /// names the current registry marks money-path, which is the same question.
    /// </para>
    /// </summary>
    public bool RegistryMoved(IReadOnlyList<string> recordedState)
    {
        ArgumentNullException.ThrowIfNull(recordedState);

        // Set equality, not a one-way membership scan: an added name is as much a registry move as a
        // removed one, and only SetEquals sees both. It runs under MoneyPathNames' own comparer,
        // which the host builds ordinal.
        var recordedNames = recordedState.Select(RecordedName).ToHashSet(StringComparer.Ordinal);

        return !MoneyPathNames.SetEquals(recordedNames);
    }

    /// <summary>
    /// Decodes the name out of a <c>name=on</c> / <c>name=off</c> entry, on the LAST separator — the
    /// exact inverse of the encoder in <see cref="MoneyPathState"/>, since the value can never contain
    /// '=' while a name could in principle.
    /// <para>
    /// Unreachable today: registry names are kebab-case, pinned by
    /// <c>CapabilityRegistryTests.Names_are_unique_and_kebab_case</c>, so first-split and last-split
    /// agree for every name that exists. Written as the true inverse anyway because it costs nothing
    /// and stays correct if that ever changes. Even under a name containing '=' the blast radius would
    /// be one of two error MESSAGES: the guard compares whole entries with SequenceEqual, so its
    /// decision, the recorded state and every posted amount are untouched either way.
    /// </para>
    /// </summary>
    private static string RecordedName(string entry) =>
        entry.LastIndexOf('=') is var i && i >= 0 ? entry[..i] : entry;
}

/// <summary>
/// Consumer-owned port (ADR-007): Operations must not reference the Capabilities module directly.
/// The host implements this as a thin adapter delegating to <c>ICapabilityGate</c>.
/// <para>
/// <b>The adapter runs on the ambient RLS transaction and must not open a new connection or scope.</b>
/// <c>ICapabilityGate.ResolveDurableAsync</c> already reads on the ambient one; reaching for
/// <c>IPlatformScope</c> there would call <c>BeginTransactionAsync</c> with a transaction already
/// open and throw.
/// </para>
/// <para>
/// Missing org context faults the returned task rather than throwing synchronously, mirroring the
/// gate. Do not "simplify" the adapter into an expression-bodied pass-through that reintroduces a
/// synchronous throw: the difference is invisible under a bare await and load-bearing the moment a
/// caller composes several of these with <c>Task.WhenAll</c>.
/// </para>
/// </summary>
public interface ICapabilitySnapshot
{
    Task<RunCapabilities> ResolveDurableAsync(CancellationToken ct);
}
