using LeaseBook.Modules.Capabilities.Registry;
using LeaseBook.Web.Seeding;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Capabilities;

/// <summary>What one parsed <c>capabilities</c> invocation asks for.</summary>
public enum CapabilitiesActionKind
{
    List,
    FlagEnable,
    FlagDisable,
    Grant,
    Revoke,
    CohortAdd,
    CohortRemove,
}

/// <summary>
/// One parsed CLI invocation. The capability is validated against the registry at parse time, so an
/// action can only ever name something that exists — see <see cref="CapabilitiesVerb"/>.
/// </summary>
public sealed record CapabilitiesAction(
    CapabilitiesActionKind Kind,
    string? Capability = null,
    Guid? OrgId = null,
    Guid? UserId = null,
    bool Stale = false);

/// <summary>
/// Parses the <c>capabilities</c> CLI verb (ADR-028). Strict in the same sense as
/// <see cref="SeedVerb"/>: an unknown subcommand, an unknown capability, a missing required flag, or
/// an argument this grammar does not define is an error, never a silent fall-through.
/// <para>
/// <b>Validating the capability name HERE is the point of the whole design.</b> The registry
/// (<c>Capabilities.All</c>) defines what exists; the database only stores state. A row naming an
/// unregistered capability is inert — <c>CapabilityStateReader</c> iterates the registry and never
/// looks at it — so an operator who typos <c>consolidated-statments</c> would see a successful write,
/// no error, and a feature that never turns on, followed by drift logged at the next boot. That is
/// exactly the failure <c>CapabilityRegistryValidator</c> is deliberately lenient about in
/// Production, and its leniency is only affordable because the typo is rejected here, at the one
/// moment it can be fixed in a single step.
/// </para>
/// <para>
/// <b>Fixture capabilities are refused outright</b> — see <see cref="FixtureRefusal"/>.
/// </para>
/// </summary>
public static class CapabilitiesVerb
{
    public const string Usage =
        "capabilities: expected one of `list [--org <id|demo>] [--stale]`, " +
        "`flag enable|disable <name>`, `grant <capability> --org <id|demo>`, " +
        "`revoke <capability> --org <id|demo>`, or " +
        "`cohort add|remove <capability> --org <id|demo> [--user <id>]` " +
        "(e.g. `dotnet run --project src/LeaseBook.Web -- capabilities list`).";

    /// <summary>
    /// Resolves <paramref name="args"/> into one action, or explains why it could not.
    /// </summary>
    /// <returns>False with a populated <paramref name="error"/> on any malformed invocation.</returns>
    public static bool TryResolve(string[] args, out CapabilitiesAction action, out string error)
    {
        ArgumentNullException.ThrowIfNull(args);

        action = new CapabilitiesAction(CapabilitiesActionKind.List);
        error = string.Empty;

        if (args.Length < 2)
        {
            error = Usage;
            return false;
        }

        return args[1].ToLowerInvariant() switch
        {
            "list" => TryList(args, out action, out error),
            "flag" => TryFlag(args, out action, out error),
            "grant" => TryEntitlement(args, CapabilitiesActionKind.Grant, out action, out error),
            "revoke" => TryEntitlement(args, CapabilitiesActionKind.Revoke, out action, out error),
            "cohort" => TryCohort(args, out action, out error),
            _ => Fail($"capabilities: unknown subcommand '{args[1]}'. {Usage}", out action, out error),
        };
    }

    // ── Subcommands ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>--org</c> is optional here and narrows the listing to one tenant's entitlement and cohort
    /// state. Without it the listing is deployment-wide and reports counts. It is optional rather than
    /// required because the common question ("what exists, and what is flagged on?") is not
    /// org-specific — but the per-org form is what makes the entitlement-collision remedy answerable.
    /// </summary>
    private static bool TryList(string[] args, out CapabilitiesAction action, out string error)
    {
        action = new CapabilitiesAction(CapabilitiesActionKind.List);

        if (!TryReadOptions(args, start: 2, allowOrg: true, allowUser: false, allowStale: true,
                out var orgId, out _, out var stale, out error))
        {
            return false;
        }

        action = action with { OrgId = orgId, Stale = stale };
        return true;
    }

    private static bool TryFlag(string[] args, out CapabilitiesAction action, out string error)
    {
        if (args.Length < 3)
        {
            return Fail($"capabilities: `flag` expects `enable` or `disable`. {Usage}", out action, out error);
        }

        var kind = args[2].ToLowerInvariant() switch
        {
            "enable" => CapabilitiesActionKind.FlagEnable,
            "disable" => CapabilitiesActionKind.FlagDisable,
            _ => (CapabilitiesActionKind?)null,
        };

        if (kind is not { } flagKind)
        {
            return Fail(
                $"capabilities: unknown flag action '{args[2]}' — expected 'enable' or 'disable'.",
                out action, out error);
        }

        if (args.Length < 4)
        {
            return Fail(
                $"capabilities: `flag {args[2].ToLowerInvariant()}` expects a capability name. {Usage}",
                out action, out error);
        }

        if (!TryCapability(args[3], out var capability, out error))
        {
            action = Empty;
            return false;
        }

        if (!TryReadOptions(args, start: 4, allowOrg: false, allowUser: false, allowStale: false,
                out _, out _, out _, out error))
        {
            action = Empty;
            return false;
        }

        action = new CapabilitiesAction(flagKind, capability);
        return true;
    }

    private static bool TryEntitlement(
        string[] args, CapabilitiesActionKind kind, out CapabilitiesAction action, out string error)
    {
        var noun = kind == CapabilitiesActionKind.Grant ? "grant" : "revoke";

        if (args.Length < 3)
        {
            return Fail($"capabilities: `{noun}` expects a capability name. {Usage}", out action, out error);
        }

        if (!TryCapability(args[2], out var capability, out error))
        {
            action = Empty;
            return false;
        }

        if (!TryReadOptions(args, start: 3, allowOrg: true, allowUser: false, allowStale: false,
                out var orgId, out _, out _, out error))
        {
            action = Empty;
            return false;
        }

        if (orgId is not { } org)
        {
            return Fail(
                $"capabilities: `{noun}` requires --org <id|demo> — an entitlement is always about one " +
                "org, and there is no fleet-wide grant.",
                out action, out error);
        }

        action = new CapabilitiesAction(kind, capability, org);
        return true;
    }

    /// <summary>
    /// <c>add</c> and <c>remove</c> are exact inverses, deliberately: <c>remove</c> targets the one
    /// rule <c>add</c> with the same arguments would have created — the org-wide rule when no
    /// <c>--user</c> is given, that user's rule when one is. Anything looser (say, "remove every rule
    /// for this org") would make a bare <c>--org</c> silently destroy user-level rules the operator
    /// never mentioned.
    /// </summary>
    private static bool TryCohort(string[] args, out CapabilitiesAction action, out string error)
    {
        var kind = args.Length < 3
            ? null
            : args[2].ToLowerInvariant() switch
            {
                "add" => CapabilitiesActionKind.CohortAdd,
                "remove" => CapabilitiesActionKind.CohortRemove,
                _ => (CapabilitiesActionKind?)null,
            };

        if (kind is not { } cohortKind)
        {
            var got = args.Length < 3 ? "nothing" : $"'{args[2]}'";
            return Fail(
                $"capabilities: `cohort` expects `add` or `remove`, got {got}. {Usage}",
                out action, out error);
        }

        var noun = cohortKind == CapabilitiesActionKind.CohortAdd ? "add" : "remove";

        if (args.Length < 4)
        {
            return Fail(
                $"capabilities: `cohort {noun}` expects a capability name. {Usage}", out action, out error);
        }

        if (!TryCapability(args[3], out var capability, out error))
        {
            action = Empty;
            return false;
        }

        if (!TryReadOptions(args, start: 4, allowOrg: true, allowUser: true, allowStale: false,
                out var orgId, out var userId, out _, out error))
        {
            action = Empty;
            return false;
        }

        if (orgId is not { } org)
        {
            return Fail(
                $"capabilities: `cohort {noun}` requires --org <id|demo>. A cohort rule always carries " +
                "an org: asp_net_users is RLS-exempt, so a bare --user could not be validated against one.",
                out action, out error);
        }

        action = new CapabilitiesAction(cohortKind, capability, org, userId);
        return true;
    }

    // ── Shared parsing ──────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates a capability name against the registry and refuses fixtures.
    /// </summary>
    private static bool TryCapability(string value, out string capability, out string error)
    {
        capability = string.Empty;
        error = string.Empty;

        if (!CapabilityCatalog.TryGet(value, out var entry))
        {
            var known = string.Join(", ", CapabilityCatalog.All.Select(c => c.Name).Order(StringComparer.Ordinal));
            error =
                $"capabilities: unknown capability '{value}'. The registry (Capabilities.All) is the " +
                $"source of truth for what exists — known: {known}. A row naming an unregistered " +
                "capability is ignored at resolution time, so the write would appear to succeed and do " +
                "nothing. Add it to the registry first, or fix the spelling.";
            return false;
        }

        if (entry.IsFixture)
        {
            error = FixtureRefusal(entry);
            return false;
        }

        capability = entry.Name;
        return true;
    }

    /// <summary>
    /// Why a fixture capability is unreachable from the CLI, spelled out because the refusal looks
    /// arbitrary otherwise: the name IS in the registry, so a naive registry check passes it.
    /// <para>
    /// The guard keys on <see cref="Capability.IsFixture"/> alone, so the message must too. The
    /// money-path clause is conditional on <see cref="Capability.IsMoneyPath"/>: they are independent
    /// axes, and a future non-money-path fixture refused with "it IS a money-path capability" would be
    /// given a false reason — the only reason it ever gets.
    /// </para>
    /// <para>
    /// For <c>money-path-fixture</c>, which is both, the clause is the whole argument. It gates no
    /// production code path — nothing reads it, by design — but the run engine's cross-run guard
    /// compares the money-path SUBSET of each org's resolved set. Moving it would invalidate every
    /// in-flight period across every org at once, returning 409 on confirm, with a remedy (re-preview)
    /// the operator has to walk every affected tenant through.
    /// </para>
    /// <para>
    /// <b>The refusal is symmetric, and that has a consequence worth stating.</b> Disabling a fixture
    /// moves the money-path subset exactly as much as enabling it, so <c>flag disable</c> is refused
    /// too — which means there is <b>no CLI remedy in either direction</b>. Under ADR-027 private
    /// networking there is no psql either, so if a fixture flag ever went live in production the fix
    /// is a redeploy with the registry changed. That is the correct trade: an operator able to "undo"
    /// it would still be issuing the fleet-wide 409 the guard exists to prevent.
    /// </para>
    /// </summary>
    private static string FixtureRefusal(Capability entry)
    {
        var moneyPath = entry.IsMoneyPath
            ? "It gates no production code path, but it IS a money-path capability, so moving it — in " +
              "either direction — would change the money-path subset of every org's resolved set and " +
              "make every in-flight bulk run conflict on confirm (409) fleet-wide, with a remedy no " +
              "operator can reasonably execute. "
            : "It exists to exercise the capability mechanism itself and gates no production code path. ";

        return $"capabilities: '{entry.Name}' is a test fixture (Capability.IsFixture) and cannot be " +
               "changed from the CLI. " + moneyPath +
               "Fixtures move only in tests, which write the row directly; there is deliberately no " +
               "CLI remedy in either direction, so a fixture flag that somehow went live is fixed by a " +
               "redeploy with the registry changed.";
    }

    /// <summary>
    /// Reads the named options that follow the positional part of a subcommand. Every token must be
    /// one this subcommand defines: <c>--user</c> on a grant, or <c>--stale</c> on a flag toggle, is
    /// an error rather than an ignored extra, because accepting-and-ignoring would silently do
    /// something other than what was asked. A repeated flag is rejected for the same reason —
    /// last-one-wins on <c>--org</c> would target the wrong tenant without a word.
    /// </summary>
    private static bool TryReadOptions(
        string[] args,
        int start,
        bool allowOrg,
        bool allowUser,
        bool allowStale,
        out Guid? orgId,
        out Guid? userId,
        out bool stale,
        out string error)
    {
        orgId = null;
        userId = null;
        stale = false;
        error = string.Empty;

        for (var i = start; i < args.Length; i++)
        {
            var token = args[i];

            if (allowOrg && token.Equals("--org", StringComparison.Ordinal))
            {
                if (orgId is not null)
                {
                    error = "capabilities: --org was given more than once.";
                    return false;
                }

                if (i + 1 >= args.Length)
                {
                    error = "capabilities: --org expects a value (an org id, or 'demo', 'cutover', " +
                            "'load', or 'scenario').";
                    return false;
                }

                if (!TryResolveOrg(args[++i], out var org, out error))
                {
                    return false;
                }

                orgId = org;
                continue;
            }

            if (allowUser && token.Equals("--user", StringComparison.Ordinal))
            {
                if (userId is not null)
                {
                    error = "capabilities: --user was given more than once.";
                    return false;
                }

                if (i + 1 >= args.Length)
                {
                    error = "capabilities: --user expects a user id.";
                    return false;
                }

                var value = args[++i];
                if (!Guid.TryParse(value, out var user))
                {
                    error = $"capabilities: --user expects a GUID, got '{value}'.";
                    return false;
                }

                userId = user;
                continue;
            }

            if (allowStale && token.Equals("--stale", StringComparison.Ordinal))
            {
                if (stale)
                {
                    error = "capabilities: --stale was given more than once.";
                    return false;
                }

                stale = true;
                continue;
            }

            error = $"capabilities: unexpected argument '{token}'. {Usage}";
            return false;
        }

        return true;
    }

    /// <summary>
    /// The same <c>--org</c> vocabulary <c>check-invariants</c> accepts (see
    /// <c>InvariantSweep.ResolveOrgIds</c>): a GUID, or one of the fixture aliases. One vocabulary
    /// across the CLI — an operator who learned it on one verb should not be surprised by another.
    /// Unlike that verb this one reports rather than throws, so the caller can print usage.
    /// </summary>
    private static bool TryResolveOrg(string value, out Guid orgId, out string error)
    {
        error = string.Empty;

        switch (value.ToLowerInvariant())
        {
            case "demo":
                orgId = DemoSeeder.DemoOrgId;
                return true;
            case "cutover":
                orgId = CutoverSeeder.CutoverOrgId;
                return true;
            case "load":
                orgId = LoadSeeder.LoadOrgId;
                return true;
            case "scenario":
                orgId = ScenarioSeeder.ScenarioOrgId;
                return true;
        }

        if (Guid.TryParse(value, out orgId))
        {
            return true;
        }

        error =
            $"capabilities: --org expects an org id or 'demo', 'cutover', 'load', or 'scenario', got " +
            $"'{value}'.";
        return false;
    }

    private static CapabilitiesAction Empty => new(CapabilitiesActionKind.List);

    private static bool Fail(string message, out CapabilitiesAction action, out string error)
    {
        action = Empty;
        error = message;
        return false;
    }
}
