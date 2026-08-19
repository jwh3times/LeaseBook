using System.Reflection;
using LeaseBook.Modules.Operations.Runs;
using Shouldly;

namespace LeaseBook.Tests.Operations;

/// <summary>
/// F-d: an unstamped <see cref="RunPreview"/> must be unconstructible, not merely discouraged.
/// <para>
/// The token was previously an <c>init</c> property defaulting to the empty string, which the engine
/// stamped afterwards through a <c>with</c> expression. Every one of the five construction sites left
/// it unset, and nothing but the engine's memory kept a preview from reaching an operator without one.
/// The empty default failed closed, but "safe when someone forgets" is a weaker guarantee than
/// "impossible to forget".
/// </para>
/// <para>
/// The compiler is what enforces this now, so these assertions guard the <i>shape</i> rather than the
/// behaviour — specifically against someone reintroducing a default and quietly restoring the old
/// hazard. Reflection over the type, not a scan of source text: the lesson from F-e is to pin
/// behaviour with the type system wherever one exists.
/// </para>
/// </summary>
public sealed class RunPreviewShapeTests
{
    [Fact]
    public void RunPreview_cannot_be_constructed_without_a_capabilities_version()
    {
        var constructors = typeof(RunPreview).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance);

        constructors.Length.ShouldBe(
            1,
            "RunPreview should have exactly one public constructor — its primary one. A second would " +
            "be a way to build a preview without supplying the capability-version token.");

        // Positional records keep the declared parameter name verbatim, so this is PascalCase.
        // Compared case-insensitively so a later shift to a conventional camelCase parameter does
        // not read as the property having disappeared.
        var version = constructors[0].GetParameters()
            .SingleOrDefault(p =>
                string.Equals(p.Name, "capabilitiesVersion", StringComparison.OrdinalIgnoreCase));

        version.ShouldNotBeNull(
            "capabilitiesVersion must be a constructor parameter. If it has moved back to an " +
            "init-only property, F-d has been reintroduced: every construction site can then omit it " +
            "and rely on the engine remembering to stamp it afterwards.");

        version!.HasDefaultValue.ShouldBeFalse(
            "capabilitiesVersion must have no default. A default is exactly what let five " +
            "construction sites leave the token empty.");
    }

    /// <summary>
    /// The other half of the split: what a strategy returns carries no token at all, so a strategy
    /// cannot supply, guess, or blank one. If this member ever appears here, the two responsibilities
    /// have been re-merged.
    /// </summary>
    [Fact]
    public void StrategyPreview_carries_no_capability_token()
    {
        typeof(StrategyPreview)
            .GetProperty("CapabilitiesVersion", BindingFlags.Public | BindingFlags.Instance)
            .ShouldBeNull(
                "the capability version is the engine's to resolve; a strategy has no way to know it " +
                "and no business inventing one.");
    }
}
