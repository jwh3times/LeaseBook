using System.Collections.Frozen;

namespace LeaseBook.Modules.Capabilities.Registry;

/// <summary>
/// The capability catalog — the single source of truth for what capabilities exist. Add an entry
/// here before it can be referenced in code, stored in the database, or named on the CLI.
/// </summary>
public static class Capabilities
{
    /// <summary>Paid: consolidated multi-property owner statements. Seed entry for the mechanism.</summary>
    public static readonly Capability ConsolidatedStatements = new(
        Name: "consolidated-statements",
        RequiresGrant: true,
        DefaultEnabled: false,
        IsMoneyPath: false);

    /// <summary>
    /// The permanent money-path FIXTURE. It gates nothing: no production code path reads it, and none
    /// ever should — it exists so the money-path machinery (the cross-run period guard's comparison,
    /// and Task 13's age gate) has something real to exercise.
    /// <para>
    /// <b>Why a real registry entry rather than a test-local <see cref="Capability"/>.</b> The
    /// cross-run guard compares the money-path SUBSET of a resolved set, and that subset is derived
    /// from <see cref="MoneyPath"/> by the host adapter. A capability constructed inside a test never
    /// enters the resolver, never appears in a resolved set, and therefore cannot move the recorded
    /// state the guard compares — the suite would pass vacuously against an empty subset on both
    /// sides.
    /// </para>
    /// <para>
    /// <b><see cref="Capability.IsFixture"/>, so the age gate exempts it.</b> Money-path capabilities
    /// are short-lived by policy and Task 13 fails CI on one older than 90 days; a permanent fixture
    /// without the exemption is a time bomb that goes off in three months on an unrelated PR.
    /// </para>
    /// <para>
    /// <b><see cref="Capability.RequiresGrant"/>: false</b> so a test moves it with a single
    /// <c>feature_flags</c> write. <b><see cref="Capability.DefaultEnabled"/>: false</b> so its
    /// resting state is off everywhere, including production, where nothing reads it anyway.
    /// </para>
    /// </summary>
    public static readonly Capability MoneyPathFixture = new(
        Name: "money-path-fixture",
        RequiresGrant: false,
        DefaultEnabled: false,
        IsMoneyPath: true,
        IsFixture: true);

    public static readonly IReadOnlyList<Capability> All =
    [
        ConsolidatedStatements,
        MoneyPathFixture,
    ];

    private static readonly FrozenDictionary<string, Capability> ByName =
        All.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out Capability capability) =>
        ByName.TryGetValue(name, out capability!);

    public static IEnumerable<Capability> MoneyPath => All.Where(c => c.IsMoneyPath);
}
