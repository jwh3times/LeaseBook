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

    public static readonly IReadOnlyList<Capability> All =
    [
        ConsolidatedStatements,
    ];

    private static readonly FrozenDictionary<string, Capability> ByName =
        All.ToFrozenDictionary(c => c.Name, StringComparer.Ordinal);

    public static bool TryGet(string name, out Capability capability) =>
        ByName.TryGetValue(name, out capability!);

    public static IEnumerable<Capability> MoneyPath => All.Where(c => c.IsMoneyPath);
}
