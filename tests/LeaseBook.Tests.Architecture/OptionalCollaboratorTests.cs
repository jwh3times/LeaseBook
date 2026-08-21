using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// ADR-039's rule generalized and enforced: an invariant collaborator may be absent only if its
/// absence <b>throws</b>. Optionality was never the problem — silent degradation is. A collaborator
/// that can be null is a check that can quietly not run, and a check that does not run looks exactly
/// like a check that passed.
/// <para>
/// This is not hypothetical, and it has cost twice. <c>PostingService</c> took
/// <c>IActorContext</c> optionally through M1–M8, so the accounting harness posted every entry
/// attributed to nobody (ADR-039's originating incident). It then kept
/// <c>IReconciliationLock? = null</c>, whose absence skipped the reconciliation period-lock check
/// wholesale; and <c>FinalizeReconciliationHandler</c> took <c>IActorContext? = null</c> and wrote
/// <c>actor?.UserId</c> into the money record, so the suite finalized reconciliations — the act that
/// locks a period — with a null finalizer and no test could tell.
/// </para>
/// <para>
/// The guard is at the <b>declaration</b>, not the usage, because the two hazards had different
/// shapes: one degraded through <c>?.</c> and the other through <c>if (x is not null)</c>. A scan for
/// either shape alone would have shipped the other. You cannot degrade on a collaborator you are
/// required to have, so requiring it is the property worth pinning.
/// </para>
/// <para>
/// Scope is <c>src/</c> only. The hazard we found was reached from a test call site, but the
/// <i>declaration</i> that permitted it lives in production code — and test doubles have legitimate
/// reasons to be partially constructed. Unlike <see cref="PlatformScopeCallSiteTests"/>, widening to
/// <c>tests/</c> here would flag helpers without protecting anything.
/// </para>
/// </summary>
public sealed class OptionalCollaboratorTests
{
    /// <summary>
    /// A nullable interface-typed parameter or field: <c>IFoo? bar</c>. Generic types such as
    /// <c>IReadOnlyList&lt;T&gt;?</c> do not match — the type argument breaks the pattern before the
    /// <c>?</c> — which is intended: a nullable collection is data, not a collaborator.
    /// </summary>
    private static readonly Regex NullableCollaborator = new(
        @"\bI[A-Z][A-Za-z0-9]*\?\s+[_a-z][A-Za-z0-9]*", RegexOptions.Compiled);

    /// <summary>
    /// The one file allowed to declare optional collaborators, and the reason it is allowed rather
    /// than an oversight: <c>AppDbContext</c> is constructed outside DI by the migrator, by EF at
    /// design time, and by test fixtures, none of which can supply a request's org or actor. It earns
    /// the exemption by failing closed anyway — see the companion test below, which is what keeps this
    /// entry from being a free bypass.
    /// </summary>
    private static readonly string AllowedDeclarationSite =
        Path.Combine("src", "LeaseBook.Web", "Persistence", "AppDbContext.cs");

    [Fact]
    public void No_invariant_collaborator_is_optional_outside_the_allow_list()
    {
        var offenders = new List<string>();

        foreach (var file in RepositorySource.Current.CodeFilesUnder("src"))
        {
            if (string.Equals(file.RelativePath, AllowedDeclarationSite, StringComparison.Ordinal))
            {
                continue;
            }

            offenders.AddRange(file.Find(NullableCollaborator).Select(match => match.ToString()));
        }

        offenders.ShouldBeEmpty(
            "make the collaborator required, or fail closed on its absence with `?? throw` and add the " +
            "declaring file to this allow list with a reason (ADR-039). A collaborator that can be null " +
            "is an invariant check that can quietly not run"
            + (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>
    /// The allow-list entry must keep earning it. Every null-conditional access on one of that file's
    /// optional collaborators has to be answered by <c>?? throw</c> — otherwise the exemption would
    /// launder exactly the degradation this guard exists to ban, in the one file the scan above skips.
    /// <para>
    /// Also a vacuity check: if a refactor removes the optional declarations entirely, the collaborator
    /// set comes back empty and the assertion below fails rather than passing over nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void An_allowed_optional_collaborator_still_fails_closed()
    {
        var source = RepositorySource.Current.File(AllowedDeclarationSite).SignificantText;

        var collaborators = NullableCollaborator.Matches(source)
            .Select(match => match.Value.Split('?', StringSplitOptions.TrimEntries)[^1])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        collaborators.ShouldNotBeEmpty(
            "the allow-list entry no longer declares optional collaborators — drop it from this guard " +
            "rather than leaving an exemption nothing needs");

        foreach (var name in collaborators)
        {
            // Two lookaheads, both load-bearing. The first pins the member name to its full extent:
            // without it the matcher backtracks to `_actor?.Acto`, looks past a leftover `r` for the
            // `?? throw`, does not find one, and reports the correctly-guarded site as an offender.
            // The second is the actual rule, and `\s` spans newlines so a `?? throw` wrapped onto the
            // following line still reads as guarded.
            var unguarded = new Regex(
                $@"\b{Regex.Escape(name)}\?\.[A-Za-z0-9_]+(?![A-Za-z0-9_])(?!\s*\?\?\s*throw)");

            unguarded.IsMatch(source).ShouldBeFalse(
                $"`{name}` is accessed with `?.` and no `?? throw` — an absent collaborator must fail " +
                "closed, not degrade into a null the caller cannot distinguish from a real answer");
        }
    }
}
