using LeaseBook.Modules.Capabilities.Registry;
using LeaseBook.Web.Capabilities;
using Shouldly;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// Money-path capabilities are short-lived BY POLICY: created for a rollout, deleted after. Every one
/// that lives on is standing risk on the books, and ADR-028's money rule — a capability may gate
/// whether a posting path is REACHABLE — is only tolerable because these are temporary. Without a gate,
/// "temporary" becomes permanent silently. This is the un-mute signal.
/// <para>
/// Age comes from git history (see <see cref="CapabilityAge"/>), which works only because the registry
/// is SOURCE CODE. A gate over <c>feature_flags</c> rows could never do this: it would enumerate the
/// empty Testcontainers database and pass vacuously forever.
/// </para>
/// <para>
/// <b>An environment that cannot answer skips locally and FAILS in CI</b> — see
/// <see cref="SkipUnlessCi"/>. Shallow clones, exported archives and images have no usable history,
/// and a gate that fails an engineer's ordinary test run is disabled by the first person it
/// inconveniences; but in GitHub Actions the same condition means the workflow stopped arming the
/// gate, and a skip there is a green build that checked nothing.
/// </para>
/// </summary>
public sealed class CapabilityAgeTests
{
    /// <summary>
    /// The gate. It has nothing to fire on today — the only money-path capability is the fixture, which
    /// is exempt — and that is correct rather than a gap: it arms itself the moment a real one is added,
    /// with no action required from whoever adds it.
    /// </summary>
    [Fact]
    public async Task No_money_path_capability_exceeds_the_policy_window()
    {
        var report = await CapabilityAge.ResolveAsync(TestContext.Current.CancellationToken);
        if (!report.IsAvailable)
        {
            SkipUnlessCi($"capability age gate NOT ARMED: {report.UnavailableReason}");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var stale = new List<string>();
        var unknown = new List<string>();

        foreach (var capability in CapabilityCatalog.MoneyPath.Where(c => !c.IsFixture))
        {
            if (!report.IntroducedAt.TryGetValue(capability.Name, out var introduced))
            {
                // A brand-new entry, uncommitted or on a branch this checkout cannot see. Not a
                // failure. Collected only to be named in the assertion message below, which Shouldly
                // renders on failure alone — so this annotates a stale-capability failure ("and these
                // others were not even checked"); it is not a standalone signal.
                unknown.Add(capability.Name);
                continue;
            }

            if (CapabilityAge.IsStale(capability, introduced, now))
            {
                stale.Add($"{capability.Name} ({(now - introduced).Days} days old)");
            }
        }

        stale.ShouldBeEmpty(
            "money-path capabilities are short-lived by policy — each one is standing risk on the " +
            "books, and ADR-028's money rule is only tolerable because they are temporary. Delete the " +
            "capability and its gate, or record the extension in ADR-028. Past the " +
            $"{(int)CapabilityAge.PolicyWindow.TotalDays}-day window: {string.Join(", ", stale)}" +
            (unknown.Count == 0 ? "" : $" (no history yet for: {string.Join(", ", unknown)})"));
    }

    /// <summary>
    /// The vacuity guard, and the reason this file is not one test.
    /// <para>
    /// The probe is scoped to a pathspec. Move or rename <c>Capabilities.cs</c> without updating
    /// <see cref="CapabilityAge.RegistryRelativePath"/> and every probe returns "no history": every
    /// capability becomes unknown, the gate above skips every entry, and CI stays green forever with
    /// nothing anywhere pointing at the cause. A gate that cannot see its subject is worse than no
    /// gate, because it is believed.
    /// </para>
    /// <para>
    /// The "at least one" assertion is deliberately not "all". A capability added in the working tree
    /// and not yet committed legitimately has no history, and failing the local test run of the very PR
    /// that introduces a capability would be the same kind of nuisance-failure that gets gates deleted.
    /// In practice the pathspec is either right (all resolve) or wrong (none do), so one is enough to
    /// discriminate.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_age_probe_can_actually_see_this_repository()
    {
        var repoRoot = CapabilityAge.FindRepoRoot();
        if (repoRoot is null)
        {
            SkipUnlessCi("capability age probe NOT ARMED: no source tree above the test assembly.");
            return;
        }

        File.Exists(Path.Combine(
                repoRoot,
                CapabilityAge.RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar)))
            .ShouldBeTrue(
                $"the capability registry must be at {CapabilityAge.RegistryRelativePath}: the age " +
                "probe reads history by that pathspec, so a moved registry makes every capability " +
                "ageless and the gate above vacuous. Update CapabilityAge.RegistryRelativePath.");

        var report = await CapabilityAge.ResolveAsync(TestContext.Current.CancellationToken);
        if (!report.IsAvailable)
        {
            SkipUnlessCi($"capability age probe NOT ARMED: {report.UnavailableReason}");
            return;
        }

        report.IntroducedAt.ShouldNotBeEmpty(
            "git history is readable and the registry is where it should be, yet no capability " +
            "resolved an introduction date. That combination means the probe is looking in the wrong " +
            "place, and the age gate is silently inert.");

        var now = DateTimeOffset.UtcNow;
        report.IntroducedAt
            .Where(pair => pair.Value > now.AddDays(1))
            .Select(pair => $"{pair.Key} @ {pair.Value:u}")
            .ShouldBeEmpty(
                "an introduction date in the future produces a negative age, which can never exceed " +
                "the policy window — the gate would be permanently satisfied.");
    }

    /// <summary>
    /// The verdict itself, pinned without git so it holds in every environment — including the ones
    /// where the two tests above skip.
    /// <para>
    /// <b>The fixture exemption is the case that matters.</b> <c>money-path-fixture</c> is
    /// <see cref="Capability.IsMoneyPath"/> AND <see cref="Capability.IsFixture"/>, permanently, because
    /// the cross-run period guard needs a real money-path entry to compare against. Drop the exemption
    /// and this gate arms on it 90 days later and fails CI on an unrelated PR, for a fixture doing its
    /// job — which is precisely how a gate earns a permanent mute.
    /// </para>
    /// </summary>
    [Fact]
    public void The_window_fires_on_a_real_money_path_capability_and_exempts_a_fixture()
    {
        var now = DateTimeOffset.UtcNow;
        var old = now - CapabilityAge.PolicyWindow - TimeSpan.FromDays(1);
        var fresh = now - CapabilityAge.PolicyWindow + TimeSpan.FromDays(1);

        var moneyPath = new Capability("scratch-money", RequiresGrant: false, DefaultEnabled: false, IsMoneyPath: true);
        var fixture = moneyPath with { Name = "scratch-fixture", IsFixture = true };
        var ordinary = moneyPath with { Name = "scratch-plain", IsMoneyPath = false };

        CapabilityAge.IsStale(moneyPath, old, now).ShouldBeTrue("this is the failure the gate exists for");
        CapabilityAge.IsStale(moneyPath, fresh, now).ShouldBeFalse("still inside the window");
        CapabilityAge.IsStale(fixture, old, now).ShouldBeFalse("Capability.IsFixture is exempt, permanently");
        CapabilityAge.IsStale(ordinary, old, now).ShouldBeFalse("only money-path capabilities are on a clock");
    }

    /// <summary>
    /// Skips locally; FAILS in CI.
    /// <para>
    /// <b>A skip cannot be made loud enough to rely on.</b> At <c>dotnet test</c> default verbosity —
    /// what CI runs — the runner prints the <c>[SKIP]</c> line naming the test but not the reason, and
    /// neither <c>Console.Error</c> (xunit v3 captures it and surfaces it only for failures) nor the
    /// raw stderr handle (vstest swallows the test host's) gets through; both were tried and printed
    /// nothing. A disarmed gate is therefore a green build with <c>Skipped: 2</c> that nobody reads.
    /// </para>
    /// <para>
    /// <b>So CI is not allowed to skip.</b> GitHub Actions guarantees a source tree, git on PATH, and
    /// whatever history the workflow asked for. "Unavailable" there is never an environment
    /// limitation — it means the workflow stopped arming the gate, which is a defect in the workflow
    /// and belongs in a red build.
    /// </para>
    /// <para>
    /// <b>Why this rather than a test asserting <c>fetch-depth: 0</c> in <c>ci.yml</c>.</b> That would
    /// guard a literal, not the invariant: another job already sets <c>fetch-depth: 0</c>, so the
    /// natural "the file contains the string" check passes even after the backend job's line is
    /// deleted — it would have been born broken. Binding it to the right job needs a YAML parse plus a
    /// pinned job name, and still only asserts a proxy that a job rename, a matrix split, a
    /// reusable-workflow refactor, or a self-hosted runner that clones shallow would leave green. An
    /// environment variable read at run time is immune to all of it, and to the shallow clone itself.
    /// </para>
    /// <para>
    /// The local skip is kept deliberately: an image build, an exported archive or a shallow local
    /// clone genuinely cannot answer, and a gate that fails an engineer's ordinary test run is the
    /// kind that gets deleted by the first person it inconveniences.
    /// </para>
    /// </summary>
    private static void SkipUnlessCi(string reason)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true", StringComparison.Ordinal))
        {
            Assert.Fail(
                reason + " In CI this is not an environment limitation: the checkout stopped " +
                "providing history. Restore `fetch-depth: 0` on the backend job (ci.yml).");
        }

        Assert.Skip(reason);
    }

    /// <summary>
    /// The registry's own fixture must stay exempt-able: if <c>money-path-fixture</c> ever lost
    /// <see cref="Capability.IsFixture"/> while keeping <see cref="Capability.IsMoneyPath"/>, the gate
    /// would start counting down on it silently, and the resulting CI failure would arrive months later
    /// on an unrelated change.
    /// </summary>
    [Fact]
    public void The_permanent_money_path_fixture_is_marked_as_one()
    {
        CapabilityCatalog.MoneyPathFixture.IsMoneyPath.ShouldBeTrue();
        CapabilityCatalog.MoneyPathFixture.IsFixture.ShouldBeTrue(
            "it is permanent by design (the cross-run guard needs a real money-path entry), so it must " +
            "carry the exemption the age gate keys on");
    }
}
