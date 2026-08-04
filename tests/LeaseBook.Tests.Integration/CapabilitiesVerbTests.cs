using LeaseBook.Web.Capabilities;
using LeaseBook.Web.Seeding;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Integration;

/// <summary>
/// The <c>capabilities</c> verb's parser (ADR-028, Task 12). Strict in the same sense as
/// <see cref="SeedVerb"/>: an unknown value is an error, never a silent fall-through.
/// <para>
/// Two of these tests are the whole reason parsing validates against the registry rather than
/// deferring to the database. <see cref="Unknown_capability_name_fails_loudly"/> is the operator-typo
/// case — a row created for a name the registry does not know is inert at resolution time, so the
/// flag silently does nothing and the next boot logs drift; rejecting it here is the one moment the
/// typo can be fixed in a single step. <see cref="The_money_path_fixture_is_refused_on_every_verb"/>
/// is the blast-radius case: see the message the parser emits.
/// </para>
/// </summary>
public sealed class CapabilitiesVerbTests
{
    private const string Known = "consolidated-statements";

    // ── The registry is the gate ────────────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_capability_name_fails_loudly()
    {
        var ok = CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "enable", "no-such-thing"], out _, out var error);

        ok.ShouldBeFalse();
        error.ShouldContain("no-such-thing");
        error.ShouldContain("unknown capability");
    }

    /// <summary>
    /// Every mutating verb, not just the flag ones: <c>money-path-fixture</c> is
    /// <c>RequiresGrant: false</c>, so a grant is inert — but a COHORT row ORs over an absent flag and
    /// would turn it on for the targeted org just as effectively as the flag would.
    /// </summary>
    [Theory]
    [InlineData("capabilities", "flag", "enable")]
    [InlineData("capabilities", "flag", "disable")]
    [InlineData("capabilities", "flag", "clear")]
    public void The_money_path_fixture_is_refused_on_every_verb(string verb, string noun, string action)
    {
        var fixtureName = CapabilityCatalog.MoneyPathFixture.Name;

        CapabilitiesVerb.TryResolve([verb, noun, action, fixtureName], out _, out var error).ShouldBeFalse();
        error.ShouldContain(fixtureName);
        error.ShouldContain("fixture");
    }

    [Fact]
    public void The_money_path_fixture_is_refused_on_grant_revoke_and_cohort_too()
    {
        var name = CapabilityCatalog.MoneyPathFixture.Name;
        var org = "11111111-1111-1111-1111-111111111111";

        CapabilitiesVerb.TryResolve(["capabilities", "grant", name, "--org", org], out _, out var grant)
            .ShouldBeFalse();
        grant.ShouldContain("fixture");

        CapabilitiesVerb.TryResolve(["capabilities", "revoke", name, "--org", org], out _, out var revoke)
            .ShouldBeFalse();
        revoke.ShouldContain("fixture");

        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "add", name, "--org", org], out _, out var cohort).ShouldBeFalse();
        cohort.ShouldContain("fixture");

        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "remove", name, "--org", org], out _, out var removal).ShouldBeFalse();
        removal.ShouldContain("fixture");
    }

    /// <summary>
    /// The refusal message must explain the refusal the guard actually performs. The guard keys on
    /// <c>IsFixture</c>; the money-path clause is conditional on <c>IsMoneyPath</c>, because they are
    /// independent axes and a future non-money-path fixture refused with "it IS a money-path
    /// capability" would be handed the one reason it gets, falsely.
    /// </summary>
    [Fact]
    public void The_fixture_refusal_explains_the_money_path_blast_radius_only_when_it_applies()
    {
        CapabilityCatalog.MoneyPathFixture.IsMoneyPath.ShouldBeTrue("guards the premise of this test");

        CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "enable", CapabilityCatalog.MoneyPathFixture.Name],
            out _, out var error).ShouldBeFalse();

        error.ShouldContain("money-path");
        error.ShouldContain("409");
        // The symmetry consequence: refused in both directions, so there is no CLI undo at all.
        error.ShouldContain("either direction");
    }

    // ── Shapes that must resolve ────────────────────────────────────────────────────────────────

    [Fact]
    public void Known_capability_resolves_to_a_flag_enable_action()
    {
        var ok = CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "enable", Known], out var action, out _);

        ok.ShouldBeTrue();
        action.Kind.ShouldBe(CapabilitiesActionKind.FlagEnable);
        action.Capability.ShouldBe(Known);
    }

    [Fact]
    public void Flag_disable_resolves()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "disable", Known], out var action, out _).ShouldBeTrue();

        action.Kind.ShouldBe(CapabilitiesActionKind.FlagDisable);
    }

    [Fact]
    public void Flag_clear_resolves()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "clear", Known], out var action, out _).ShouldBeTrue();

        action.Kind.ShouldBe(CapabilitiesActionKind.FlagClear);
        action.Capability.ShouldBe(Known);
    }

    [Fact]
    public void List_resolves_with_and_without_stale()
    {
        CapabilitiesVerb.TryResolve(["capabilities", "list"], out var plain, out _).ShouldBeTrue();
        plain.Kind.ShouldBe(CapabilitiesActionKind.List);
        plain.Stale.ShouldBeFalse();

        CapabilitiesVerb.TryResolve(["capabilities", "list", "--stale"], out var stale, out _).ShouldBeTrue();
        stale.Stale.ShouldBeTrue();
    }

    [Fact]
    public void Grant_resolves_a_guid_org()
    {
        var org = Guid.Parse("11111111-1111-1111-1111-111111111111");

        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org", org.ToString()], out var action, out _).ShouldBeTrue();

        action.Kind.ShouldBe(CapabilitiesActionKind.Grant);
        action.OrgId.ShouldBe(org);
    }

    /// <summary>
    /// The same fixture aliases <c>check-invariants</c> accepts. One <c>--org</c> vocabulary across
    /// the CLI: an operator who learned it on one verb should not be surprised by another.
    /// </summary>
    [Theory]
    [InlineData("demo")]
    [InlineData("DEMO")]
    public void Grant_resolves_the_demo_alias(string value)
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org", value], out var action, out _).ShouldBeTrue();

        action.OrgId.ShouldBe(DemoSeeder.DemoOrgId);
    }

    [Fact]
    public void Revoke_resolves()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "revoke", Known, "--org", "demo"], out var action, out _).ShouldBeTrue();

        action.Kind.ShouldBe(CapabilitiesActionKind.Revoke);
        action.Capability.ShouldBe(Known);
    }

    [Fact]
    public void Cohort_add_accepts_an_optional_user()
    {
        var ok = CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "add", Known,
             "--org", "11111111-1111-1111-1111-111111111111",
             "--user", "22222222-2222-2222-2222-222222222222"],
            out var action, out _);

        ok.ShouldBeTrue();
        action.Kind.ShouldBe(CapabilitiesActionKind.CohortAdd);
        action.UserId.ShouldNotBeNull();
    }

    [Fact]
    public void Cohort_add_without_a_user_is_an_org_wide_rule()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "add", Known, "--org", "demo"], out var action, out _).ShouldBeTrue();

        action.UserId.ShouldBeNull();
    }

    /// <summary>
    /// <c>remove</c> takes exactly the arguments <c>add</c> takes, because it is the exact inverse —
    /// without it the CLI could create cohort state it could not undo.
    /// </summary>
    [Fact]
    public void Cohort_remove_mirrors_cohort_add()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "remove", Known, "--org", "demo"], out var orgWide, out _)
            .ShouldBeTrue();
        orgWide.Kind.ShouldBe(CapabilitiesActionKind.CohortRemove);
        orgWide.UserId.ShouldBeNull();

        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "remove", Known, "--org", "demo",
             "--user", "22222222-2222-2222-2222-222222222222"],
            out var scoped, out _).ShouldBeTrue();
        scoped.UserId.ShouldNotBeNull();
    }

    [Fact]
    public void List_accepts_an_optional_org()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "list", "--org", "demo", "--stale"], out var action, out _).ShouldBeTrue();

        action.Kind.ShouldBe(CapabilitiesActionKind.List);
        action.OrgId.ShouldBe(DemoSeeder.DemoOrgId);
        action.Stale.ShouldBeTrue();
    }

    // ── Strictness ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Grant_requires_an_org()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org");
    }

    [Fact]
    public void Cohort_add_requires_an_org()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "add", Known], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org");
    }

    [Fact]
    public void Unknown_subcommand_fails_with_usage()
    {
        CapabilitiesVerb.TryResolve(["capabilities", "frobnicate"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("capabilities:");
    }

    [Fact]
    public void A_bare_verb_fails_with_usage()
    {
        CapabilitiesVerb.TryResolve(["capabilities"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("capabilities:");
    }

    [Fact]
    public void Unknown_flag_action_fails_with_usage()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "toggle", Known], out _, out var error).ShouldBeFalse();
        error.ShouldContain("enable");
    }

    [Fact]
    public void Flag_without_a_name_fails()
    {
        CapabilitiesVerb.TryResolve(["capabilities", "flag", "enable"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("capabilities:");
    }

    [Fact]
    public void Unknown_cohort_action_fails()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "purge", Known, "--org", "demo"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("add");
        error.ShouldContain("remove");
    }

    [Fact]
    public void An_unparseable_org_fails_naming_the_value()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org", "not-an-org"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("not-an-org");
    }

    [Fact]
    public void An_unparseable_user_fails_naming_the_value()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "cohort", "add", Known, "--org", "demo", "--user", "nobody"],
            out _, out var error).ShouldBeFalse();
        error.ShouldContain("nobody");
    }

    /// <summary>
    /// <c>--user</c> only means something on a cohort rule. An entitlement is org-wide by definition,
    /// so accepting and ignoring the flag there would quietly grant more than the operator asked for.
    /// </summary>
    [Fact]
    public void User_is_rejected_on_grant()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org", "demo", "--user", "22222222-2222-2222-2222-222222222222"],
            out _, out var error).ShouldBeFalse();
        error.ShouldContain("--user");
    }

    [Fact]
    public void Stale_is_rejected_outside_list()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "flag", "enable", Known, "--stale"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--stale");
    }

    [Fact]
    public void A_repeated_org_flag_is_rejected_rather_than_last_one_wins()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org", "demo", "--org", "load"],
            out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org");
    }

    [Fact]
    public void A_dangling_org_flag_is_rejected()
    {
        CapabilitiesVerb.TryResolve(
            ["capabilities", "grant", Known, "--org"], out _, out var error).ShouldBeFalse();
        error.ShouldContain("--org");
    }

    [Fact]
    public void Trailing_junk_after_list_is_rejected()
    {
        CapabilitiesVerb.TryResolve(["capabilities", "list", "everything"], out _, out var error)
            .ShouldBeFalse();
        error.ShouldContain("everything");
    }
}
