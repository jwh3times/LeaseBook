using System.Text.RegularExpressions;
using LeaseBook.Modules.Capabilities.Registry;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Capabilities;

public sealed class CapabilityRegistryTests
{
    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <summary>
    /// An ACTUAL kebab pattern, not "lowercase with no spaces". The loose version admitted
    /// <c>a=b</c>, <c>a.b</c>, a leading or doubled hyphen, and the empty string — and the name is not
    /// cosmetic: it is the database key, the CLI argument, and half of the <c>name=on</c> encoding the
    /// cross-run period guard records in <c>bulk_runs.summary_json</c> and decodes on the way back
    /// (ADR-028). A name containing '=' would make that encoding ambiguous. Foreclosing it here is
    /// cheaper than defending against it at every reader.
    /// </summary>
    [Fact]
    public void Names_are_unique_and_kebab_case()
    {
        var names = CapabilityCatalog.All.Select(c => c.Name).ToArray();

        names.ShouldBeUnique();
        names.ShouldAllBe(n => KebabCase.IsMatch(n));
    }

    /// <summary>
    /// The pattern's own guard rails. Without these the regex above could be quietly weakened to
    /// something that passes for every name that happens to exist today.
    /// </summary>
    [Theory]
    [InlineData("consolidated-statements")]
    [InlineData("money-path-fixture")]
    [InlineData("a")]
    [InlineData("v2-rent-proration")]
    public void The_kebab_pattern_accepts_a_well_formed_name(string name)
    {
        KebabCase.IsMatch(name).ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("Consolidated-Statements")]
    [InlineData("consolidated statements")]
    [InlineData("consolidated_statements")]
    [InlineData("consolidated.statements")]
    [InlineData("a=b")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("double--hyphen")]
    public void The_kebab_pattern_rejects_a_malformed_name(string name)
    {
        KebabCase.IsMatch(name).ShouldBeFalse();
    }

    [Fact]
    public void TryGet_resolves_a_known_name()
    {
        CapabilityCatalog.TryGet("consolidated-statements", out var capability).ShouldBeTrue();
        capability.RequiresGrant.ShouldBeTrue();
    }

    [Fact]
    public void TryGet_rejects_an_unknown_name()
    {
        CapabilityCatalog.TryGet("no-such-capability", out _).ShouldBeFalse();
    }
}
