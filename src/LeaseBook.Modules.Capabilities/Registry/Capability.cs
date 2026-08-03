namespace LeaseBook.Modules.Capabilities.Registry;

/// <summary>
/// One entry in the capability catalog. The catalog is SOURCE CODE, never database rows — the
/// database stores state (on/off, granted/revoked), never definition. Three mechanisms depend on
/// that: strict CLI parsing needs a list to fail against, <see cref="IsMoneyPath"/> is a property of
/// the code that reads the capability rather than of an operator's memory, and any CI gate must
/// enumerate this assembly (a gate enumerating feature_flags would enumerate the empty
/// Testcontainers database and pass vacuously forever).
/// </summary>
/// <param name="Name">Stable kebab-case identifier. Also the database key.</param>
/// <param name="RequiresGrant">
/// True for paid capabilities: absent a live entitlement the capability is unavailable regardless of
/// flag state. Introducing a RequiresGrant capability over already-shipped behavior REQUIRES a data
/// migration granting it to every existing org — see CapabilityResolver and ADR-028.
/// </param>
/// <param name="DefaultEnabled">Flag value when no feature_flags row exists.</param>
/// <param name="IsMoneyPath">
/// True when the capability gates reachability of a posting path. Money-path capabilities are
/// resolved inside the ambient transaction rather than served from cache, and are expected to be
/// short-lived by policy.
/// </param>
/// <param name="IsFixture">
/// True for test-only capabilities that exist to exercise the mechanism itself. Exempt from the
/// money-path age gate (Task 13), which would otherwise fail CI 90 days after a permanent test
/// fixture was added. Never true for a capability real code gates on.
/// </param>
public sealed record Capability(
    string Name,
    bool RequiresGrant,
    bool DefaultEnabled,
    bool IsMoneyPath,
    bool IsFixture = false);
