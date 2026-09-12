using System.Text.RegularExpressions;
using LeaseBook.Web.Audit;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The audit-review surface (#321) offers its entity and action filters from the catalogs in
/// <see cref="AuditEntityTypes"/> and <see cref="AuditActions"/>, not from <c>SELECT DISTINCT</c> over
/// <c>audit_events</c>. That is the right trade — a filter list built from data describes the rows that
/// happen to exist rather than the events that can occur — but it moves the failure: a hand-written audit
/// row with a new <c>entity_type</c> or <c>action</c> becomes invisible to the filter, silently, and the
/// reviewer is told the event does not exist rather than that the filter cannot name it.
/// <para>
/// Table-backed entity types cannot drift, because <see cref="AuditEntityTypes.All"/> reads the EF model.
/// The hand-written literals are what this scans for. It reads source, so an empty database hides nothing
/// from it.
/// </para>
/// <para>
/// The four seeders assign <c>EntityType</c> a <c>const</c> rather than a literal, so the scan resolves
/// same-file <c>const string</c> declarations. An assignment it can resolve to neither a literal nor a
/// known const is reported as an offender rather than skipped: a value the guard cannot read is exactly
/// the value that would slip past it.
/// </para>
/// </summary>
public sealed class AuditVocabularyDriftTests
{
    private static readonly string[] SourceRoots = ["src"];

    /// <summary>
    /// Every file that constructs an audit row by hand today. Pinned so the scan cannot pass by finding
    /// nothing: if the detection breaks, the coverage assertion goes red instead of the whole guard
    /// quietly succeeding against zero matches.
    /// <para>
    /// All of them, not a sample, and <see cref="The_scan_still_reaches_every_known_audit_writer"/>
    /// asserts both directions. An earlier version pinned five of these ten: the four seeders share one
    /// <c>entity_type</c> and only one was pinned, so losing the other three would have changed nothing
    /// any assertion could see.
    /// </para>
    /// </summary>
    private static readonly string[] KnownAuditWriters =
    [
        "src/LeaseBook.Web/Auth/AccountSecurityAudit.cs",
        "src/LeaseBook.Web/Endpoints/AuditLogEndpoints.cs",
        "src/LeaseBook.Web/Onboarding/BalanceImportService.cs",
        "src/LeaseBook.Web/Onboarding/Verification/VerificationService.cs",
        "src/LeaseBook.Web/Persistence/AppDbContext.cs",
        "src/LeaseBook.Web/Reporting/ReportingEndpoints.cs",
        "src/LeaseBook.Web/Seeding/CutoverSeeder.cs",
        "src/LeaseBook.Web/Seeding/DemoSeeder.cs",
        "src/LeaseBook.Web/Seeding/LoadSeeder.cs",
        "src/LeaseBook.Web/Seeding/ScenarioSeeder.cs",
    ];

    /// <summary>
    /// Assignments that are deliberately not literals, with the reason each is safe. Declared rather
    /// than skipped by pattern, so adding a fourth is a decision someone makes here.
    /// <list type="bullet">
    /// <item>The auditing pass derives <c>entity_type</c> from the EF model, which is exactly the half
    /// <see cref="AuditEntityTypes.All"/> reads back — it cannot drift from itself.</item>
    /// <item>Its <c>action</c> is one of three values mapped from <c>EntityState</c>, which
    /// <see cref="AuditActions.Tracked"/> enumerates.</item>
    /// <item>The account-security lifecycle takes its action as a parameter; the values are collected
    /// from its call sites by <see cref="RecordedActionLiteral"/> instead.</item>
    /// </list>
    /// </summary>
    private static readonly (string File, string Expression)[] Exempt =
    [
        ("src/LeaseBook.Web/Persistence/AppDbContext.cs", "entry.Metadata.GetTableName"),
        ("src/LeaseBook.Web/Persistence/AppDbContext.cs", "action"),
        ("src/LeaseBook.Web/Auth/AccountSecurityAudit.cs", "action"),
    ];

    /// <summary>Matches the construction itself, not the type name in a doc comment or a nested type.</summary>
    private static readonly Regex AuditEventConstruction =
        new(@"new AuditEvent\s*[({]", RegexOptions.Compiled);

    private static readonly Regex EntityTypeAssignment =
        new(@"EntityType\s*=\s*(?<rhs>""[^""]*""|[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    private static readonly Regex ActionAssignment =
        new(@"\bAction\s*=\s*(?<rhs>""[^""]*""|[A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.Compiled);

    /// <summary>The account-security lifecycle passes its action as an argument, not an initializer.</summary>
    private static readonly Regex RecordedActionLiteral =
        new(@"RecordAsync\([^,]+,\s*""(?<value>[^""]+)""", RegexOptions.Compiled);

    /// <summary><c>private const string Foo = "bar";</c> — the indirection the seeders use.</summary>
    private static readonly Regex ConstStringDeclaration =
        new(@"const\s+string\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*""(?<value>[^""]*)""", RegexOptions.Compiled);

    [Fact]
    public void Every_hand_written_entity_type_is_in_the_catalog()
    {
        var (found, unresolved) = Values(EntityTypeAssignment);

        unresolved.ShouldBeEmpty(
            $"EntityType assignments the scan could not resolve to a value: {string.Join("; ", unresolved)}");
        found.ShouldNotBeEmpty("the entity_type scan matched nothing — the extraction is broken, not the source");

        var unlisted = found.Keys
            .Where(value => !AuditEntityTypes.Synthetic.Contains(value, StringComparer.Ordinal))
            .ToList();
        unlisted.ShouldBeEmpty(
            "audit entity_type literals missing from AuditEntityTypes.Synthetic — the review surface " +
            $"cannot filter on them: {Describe(found, unlisted)}");
    }

    [Fact]
    public void Every_hand_written_action_is_in_the_catalog()
    {
        var (found, unresolved) = Values(ActionAssignment);

        unresolved.ShouldBeEmpty(
            $"Action assignments the scan could not resolve to a value: {string.Join("; ", unresolved)}");

        foreach (var (value, sites) in Literals(RecordedActionLiteral, RepositorySource.Current.CodeFilesUnder(SourceRoots)))
        {
            found[value] = found.TryGetValue(value, out var existing) ? [.. existing, .. sites] : sites;
        }

        found.ShouldNotBeEmpty("the action scan matched nothing — the extraction is broken, not the source");
        var unlisted = found.Keys.Where(value => !AuditActions.All.Contains(value, StringComparer.Ordinal)).ToList();
        unlisted.ShouldBeEmpty(
            "audit action literals missing from AuditActions — the review surface cannot filter on " +
            $"them: {Describe(found, unlisted)}");
    }

    /// <summary>
    /// The coverage arm, in both directions. Every pinned writer must still be reached, so a refactor
    /// that moves or renames the construction is noticed here rather than by the filter list quietly
    /// shrinking — and every reached file must be pinned, so a new hand-written audit row cannot arrive
    /// with nothing asserting that the scan sees it.
    /// </summary>
    [Fact]
    public void The_scan_still_reaches_every_known_audit_writer()
    {
        var reached = AuditWriters()
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missed = KnownAuditWriters.Where(path => !reached.Contains(path)).ToList();
        missed.ShouldBeEmpty(
            "these files used to write an audit row by hand and the scan no longer sees them — either " +
            $"they moved (update the pin) or the detection broke: {string.Join(", ", missed)}");

        var unpinned = reached.Where(path => !KnownAuditWriters.Contains(path, StringComparer.Ordinal)).ToList();
        unpinned.ShouldBeEmpty(
            "new files construct an audit row by hand. Add them to KnownAuditWriters so the coverage " +
            $"arm keeps meaning what it says: {string.Join(", ", unpinned)}");
    }

    /// <summary>
    /// Files that construct an <c>AuditEvent</c>. Scoping the initializer scan this way keeps an
    /// unrelated <c>Action = "…"</c> elsewhere in the host out of the audit vocabulary; the
    /// account-security call sites, which pass their action as an argument, are picked up separately by
    /// <see cref="RecordedActionLiteral"/> over the whole tree.
    /// </summary>
    private static IReadOnlyList<RepositoryFile> AuditWriters() =>
    [
        .. RepositorySource.Current.CodeFilesUnder(SourceRoots)
            .Where(file => AuditEventConstruction.IsMatch(file.SignificantText)),
    ];

    /// <summary>
    /// Values assigned to the named property across the audit writers, resolving a same-file
    /// <c>const string</c> where the right-hand side is an identifier. The second list is every
    /// assignment that resolved to neither.
    /// </summary>
    private static (Dictionary<string, List<string>> Found, List<string> Unresolved) Values(Regex assignment)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var unresolved = new List<string>();

        foreach (var file in AuditWriters())
        {
            var constants = Constants(file);
            foreach (var line in file.Lines.Where(line => line.Code.Length > 0))
            {
                // A const *declaration* also matches `…EntityType = "org-provisioned"`. Skip the
                // declaration: its value is collected through the assignment that references it, which
                // is what keeps the const from being counted as a hand-written literal it is not.
                if (ConstStringDeclaration.IsMatch(line.Code))
                {
                    continue;
                }

                foreach (Match match in assignment.Matches(line.Code))
                {
                    var rhs = match.Groups["rhs"].Value;
                    var site = $"{file.RelativePath}:{line.Number}";
                    var value = rhs.StartsWith('"')
                        ? rhs.Trim('"')
                        : constants.GetValueOrDefault(rhs.Split('.')[^1]);

                    if (value is null)
                    {
                        if (!IsExempt(file.RelativePath, rhs))
                        {
                            unresolved.Add($"{site}: {match.Value.Trim()}");
                        }

                        continue;
                    }

                    if (!found.TryGetValue(value, out var sites))
                    {
                        sites = [];
                        found[value] = sites;
                    }

                    sites.Add(site);
                }
            }
        }

        return (found, unresolved);
    }

    /// <summary>
    /// Full relative paths, not a file-name suffix: an <c>EndsWith</c> would exempt any same-named file
    /// dropped anywhere in the tree, the trap <c>MigrationBackfillRlsTests</c> names.
    /// </summary>
    private static bool IsExempt(string relativePath, string expression) =>
        Exempt.Contains((relativePath.Replace('\\', '/'), expression));

    private static Dictionary<string, string> Constants(RepositoryFile file)
    {
        var constants = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ConstStringDeclaration.Matches(file.SignificantText))
        {
            constants[match.Groups["name"].Value] = match.Groups["value"].Value;
        }

        return constants;
    }

    private static Dictionary<string, List<string>> Literals(Regex pattern, IEnumerable<RepositoryFile> files)
    {
        var found = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (var line in file.Lines.Where(line => line.Code.Length > 0))
            {
                foreach (Match match in pattern.Matches(line.Code))
                {
                    var value = match.Groups["value"].Value;
                    if (!found.TryGetValue(value, out var sites))
                    {
                        sites = [];
                        found[value] = sites;
                    }

                    sites.Add($"{file.RelativePath}:{line.Number}");
                }
            }
        }

        return found;
    }

    private static string Describe(Dictionary<string, List<string>> found, IEnumerable<string> values) =>
        string.Join("; ", values.Select(value => $"'{value}' at {string.Join(", ", found[value])}"));
}
