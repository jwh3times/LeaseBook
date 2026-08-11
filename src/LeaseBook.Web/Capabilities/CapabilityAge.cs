using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using LeaseBook.Modules.Capabilities.Registry;
using CapabilityCatalog = LeaseBook.Modules.Capabilities.Registry.Capabilities;

namespace LeaseBook.Web.Capabilities;

/// <summary>
/// How old each capability is, and whether a money-path one has outlived the policy window (ADR-028).
/// <para>
/// <b>The registry carries no timestamp, deliberately — the age comes from git history.</b>
/// <see cref="Capability"/> is a record of five booleans and a name; adding an <c>IntroducedOn</c>
/// field would make the clock a value the author of a capability types in, which is exactly the value
/// someone under deadline pressure adjusts. History is not writable that way, and — the decisive
/// property — it requires no action at all from whoever adds a capability. A hand-entered date that is
/// simply forgotten leaves the gate disarmed forever with nothing to notice; git dates the entry
/// whether or not anybody thought about the gate.
/// </para>
/// <para>
/// This works only because the catalog is SOURCE CODE. A gate over <c>feature_flags</c> rows could not
/// do it: the test database is empty, so it would enumerate nothing and pass vacuously forever.
/// </para>
/// <para>
/// <b>Both consumers share this one implementation on purpose.</b> <c>capabilities list --stale</c>
/// reports the ages and <c>CapabilityAgeTests</c> fails CI on them; if the two computed staleness
/// separately they could disagree, and the operator-facing report saying "within window" while CI says
/// otherwise (or worse, the reverse) would discredit both. <see cref="IsStale"/> is the single
/// definition.
/// </para>
/// <para>
/// <b>It degrades to UNKNOWN, never to "fresh".</b> See <see cref="CapabilityAgeReport"/> — every
/// caller has to handle an unavailable report explicitly, because the failure mode that matters here
/// is a probe that quietly answers "nothing is stale" in an environment where it cannot see anything.
/// </para>
/// </summary>
public static class CapabilityAge
{
    /// <summary>
    /// How long a money-path capability may live. Money-path capabilities are short-lived BY POLICY —
    /// created for a rollout, deleted after — and the whole design's tolerance for gating a posting
    /// path on a flag rests on that. One that lives on is standing risk on the books.
    /// </summary>
    public static readonly TimeSpan PolicyWindow = TimeSpan.FromDays(90);

    /// <summary>
    /// The file the probe reads history for, relative to the repository root, in git's own forward-slash
    /// form (git accepts it as a pathspec on Windows too).
    /// <para>
    /// Pinned as a constant and asserted to exist by <c>CapabilityAgeTests</c>, because the pathspec is
    /// how this whole mechanism goes quiet: move or rename the registry without updating this string and
    /// every probe returns "no history", every capability becomes unknown, and the gate skips forever
    /// while looking perfectly healthy.
    /// </para>
    /// </summary>
    public const string RegistryRelativePath =
        "src/LeaseBook.Modules.Capabilities/Registry/Capabilities.cs";

    private static readonly TimeSpan GitTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Capability names reach <c>git</c> as process arguments. They come from source code and are
    /// already pinned to kebab-case by the registry suite, so this is belt-and-braces — but an argument
    /// list built from a name that starts with <c>-</c> would be read by git as an OPTION, and the point
    /// of validating is that the probe refuses rather than runs something else.
    /// </summary>
    private static readonly Regex SafeCapabilityName = new(
        "^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// The one definition of "stale", shared by the CLI report and the CI gate.
    /// <para>
    /// <b><see cref="Capability.IsFixture"/> is exempt, and that exemption is why the field exists.</b>
    /// <c>money-path-fixture</c> is permanent by design — it exists so the money-path machinery has
    /// something real to exercise — so without this it would arm the gate 90 days after it was added
    /// and fail CI on an unrelated PR, for a fixture doing exactly its job.
    /// </para>
    /// </summary>
    public static bool IsStale(Capability capability, DateTimeOffset introducedAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(capability);

        return capability.IsMoneyPath
            && !capability.IsFixture
            && now - introducedAt > PolicyWindow;
    }

    /// <summary>
    /// Resolves the introduction date of every registry capability from git history, or explains why it
    /// could not.
    /// </summary>
    public static async Task<CapabilityAgeReport> ResolveAsync(CancellationToken ct)
    {
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return CapabilityAgeReport.Unavailable(
                "the source tree is not present next to this process (no LeaseBook.slnx above " +
                $"{AppContext.BaseDirectory}). Capability age is read from git history, so a deployed " +
                "container — including the capabilities ACA job — cannot report it.");
        }

        if (!File.Exists(Path.Combine(repoRoot, RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar))))
        {
            return CapabilityAgeReport.Unavailable(
                $"the capability registry is not at {RegistryRelativePath}. Age is probed by pathspec, " +
                "so a moved or renamed registry silently resolves nothing — update " +
                $"{nameof(CapabilityAge)}.{nameof(RegistryRelativePath)} to the new path.");
        }

        // Asked BEFORE any history is read, because a shallow clone does not fail — it LIES. Every
        // commit behind the graft point is invisible, so the pickaxe finds the first commit it can see
        // (the graft itself) and every capability reports an age of roughly zero: a clean bill of health
        // that means nothing. Verified against a `git clone --depth 1`, where a capability introduced
        // years earlier reported the HEAD commit's date. actions/checkout defaults to fetch-depth: 1, so
        // this is the ordinary CI case, not an exotic one — the backend job sets fetch-depth: 0.
        var shallow = await GitAsync(repoRoot, ["rev-parse", "--is-shallow-repository"], ct);
        if (!shallow.Ok)
        {
            return CapabilityAgeReport.Unavailable(
                $"git history is not readable here ({shallow.Failure}). This is expected in an exported " +
                "archive or an image build; it is not a correctness failure, and it is reported rather " +
                "than treated as 'nothing is stale'.");
        }

        if (shallow.Output.Trim().Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return CapabilityAgeReport.Unavailable(
                "this is a SHALLOW git clone. Every commit before the graft point is invisible, so each " +
                "capability would date to the graft commit and report an age near zero — a false clean " +
                "bill of health rather than an error. Fetch full history (actions/checkout with " +
                "fetch-depth: 0, or `git fetch --unshallow`) to arm the age gate.");
        }

        var introduced = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        foreach (var capability in CapabilityCatalog.All)
        {
            if (!SafeCapabilityName.IsMatch(capability.Name))
            {
                return CapabilityAgeReport.Unavailable(
                    $"capability name '{capability.Name}' is not the kebab-case shape the probe passes " +
                    "to git as an argument, so no age was read for anything.");
            }

            // --follow, and no --diff-filter. Both matter:
            //   * `--diff-filter=A` restricts to commits where the FILE was added, which is only ever
            //     the commit that created the registry — so every capability added after that resolves
            //     to nothing and skips. Verified: it returned empty for `money-path-fixture` here.
            //   * without `--follow`, a `git mv` of the registry truncates history at the rename, and
            //     every capability's age silently resets to the day the file moved.
            // Newest first, so the LAST line is the oldest commit that changed how many times this name
            // appears in the registry — i.e. when it was introduced. A name deleted and later re-added
            // therefore dates to the FIRST introduction, which is the conservative direction: re-adding
            // a money-path capability under the same name re-arms the same risk.
            //
            // Two ways the clock resets, both unsafe and neither detectable from here:
            //   * RENAMING a capability's Name string starts a new pickaxe history at zero. Defensible
            //     (a renamed money-path capability is arguably a new one) but it is a one-line bypass.
            //   * A rename of THIS FILE combined with a rewrite of more than half its content in the
            //     same commit defeats git's rename detection, so --follow stops there and every age
            //     dates to that commit. The vacuity guard cannot catch it: the pathspec still resolves
            //     and the dates are merely younger.
            var log = await GitAsync(
                repoRoot,
                ["log", "--follow", "--format=%aI", "-S", capability.Name, "--", RegistryRelativePath],
                ct);

            if (!log.Ok)
            {
                return CapabilityAgeReport.Unavailable(
                    $"reading history for '{capability.Name}' failed ({log.Failure}), so no age was read " +
                    "for anything. A per-capability skip would have been a silent one.");
            }

            var oldest = log.Output
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault();

            // No commit touches this name yet: a brand-new entry in the working tree, or one added on a
            // branch whose commits are not here. Absent, not zero — the caller reports it as unknown.
            if (oldest is not null &&
                DateTimeOffset.TryParse(
                    oldest, CultureInfo.InvariantCulture, DateTimeStyles.None, out var when))
            {
                introduced[capability.Name] = when;
            }
        }

        return new CapabilityAgeReport(introduced, UnavailableReason: null);
    }

    /// <summary>
    /// Walks up from the running assembly for <c>LeaseBook.slnx</c>. Null when there is no source tree,
    /// which is the normal state of a deployed container.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "LeaseBook.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName;
    }

    /// <summary>
    /// Runs one git command with an explicit argument LIST — never a command string — so nothing is
    /// handed to a shell for re-parsing. Works in a git worktree (where <c>.git</c> is a file, not a
    /// directory) because git resolves that itself from the working directory.
    /// </summary>
    private static async Task<GitResult> GitAsync(
        string repoRoot, string[] arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(info);
            if (process is null)
            {
                return GitResult.Failed("git could not be started");
            }

            // Both streams are drained concurrently with the wait: reading one to the end before
            // waiting deadlocks if the other fills its pipe buffer.
            var stdout = process.StandardOutput.ReadToEndAsync(ct);
            var stderr = process.StandardError.ReadToEndAsync(ct);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(GitTimeout);

            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return GitResult.Failed($"git did not finish within {GitTimeout.TotalSeconds:0}s");
            }

            var output = await stdout;
            var error = await stderr;

            return process.ExitCode == 0
                ? new GitResult(true, output, null)
                : GitResult.Failed(
                    $"git exited {process.ExitCode}: {error.Trim().Split('\n').FirstOrDefault()}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Most often "git is not installed". Reported, never thrown: this is a reporting probe, and
            // an environment without git is not a correctness failure.
            return GitResult.Failed($"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private readonly record struct GitResult(bool Ok, string Output, string? Failure)
    {
        public static GitResult Failed(string failure) => new(false, string.Empty, failure);
    }
}

/// <summary>
/// What the age probe saw. Either an introduction date per capability, or a reason it saw nothing —
/// never a silent empty map, because "no stale capabilities" and "I could not look" are the two
/// answers this mechanism must never confuse.
/// </summary>
public sealed record CapabilityAgeReport(
    IReadOnlyDictionary<string, DateTimeOffset> IntroducedAt,
    string? UnavailableReason)
{
    public bool IsAvailable => UnavailableReason is null;

    public static CapabilityAgeReport Unavailable(string reason) =>
        new(new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal), reason);
}
