using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// ADR-025: <c>web/src/api</c> owns request execution — transport policy, the success rule, file
/// downloads, and the error vocabulary. These guards keep the three policies that had escaped it
/// from escaping again.
/// <para>
/// Each rule failed silently before it was consolidated, which is why a convention was not enough.
/// The success rule stood at 21 read sites, twenty of which threw a hand-written literal and
/// discarded the ProblemDetails body — so a failed read could never render the correlation id
/// <c>ApiErrorNotice</c> exists to show, and nothing was red. The anchor dance was copied at five
/// sites that had grown four different error contracts for one operation shape. Telemetry re-stated
/// the XSRF header and <c>credentials</c> over a raw fetch, a second copy of security policy that
/// would keep working until the contract moved and then fail invisibly.
/// </para>
/// <para>
/// Source-scanned rather than compiled: TypeScript has no IL for the C# guards to read, and the
/// property is about which file a construct appears in. <see cref="Every_scan_still_finds_its_subject"/>
/// is the vacuity control — a refactor that renames these constructs turns the scans green, so the
/// api module's own occurrences are asserted present.
/// </para>
/// </summary>
public sealed class SpaRequestExecutionTests
{
    private const string ApiModule = "web/src/api";

    /// <summary>The hand-written success rule: <c>if (error || !data) throw new Error(...)</c>.</summary>
    private static readonly Regex SuccessRule = new(@"error\s*\|\|\s*!\s*data", RegexOptions.Compiled);

    /// <summary>The blob-to-anchor download tail.</summary>
    private static readonly Regex ObjectUrl = new(@"\bcreateObjectURL\s*\(", RegexOptions.Compiled);

    /// <summary>Reading the XSRF cookie out of <c>document.cookie</c>.</summary>
    private static readonly Regex CookieRead = new(@"document\.cookie", RegexOptions.Compiled);

    /// <summary>
    /// A request issued outside the generated client. Matches a member call (<c>globalThis.fetch(</c>)
    /// as well as a bare one — the api module's single sanctioned call site is the former, and an
    /// escape would most naturally be written the same way.
    /// </summary>
    private static readonly Regex RawFetch = new(@"(?<![_\w])fetch\s*\(", RegexOptions.Compiled);

    public static TheoryData<string, string, string> EscapedPolicies() => new()
    {
        {
            nameof(SuccessRule),
            "the success rule",
            "call unwrap(call, 'fallback message') from @/api — a hand-written throw discards the " +
            "server's code and correlationId, and nothing fails when it does"
        },
        {
            nameof(ObjectUrl),
            "the blob download tail",
            "call download(() => …, filename) from @/api — five copies of this grew four different " +
            "error contracts for one operation shape"
        },
        {
            nameof(CookieRead),
            "XSRF cookie reads",
            "the client interceptor in web/src/api/client.ts owns this; a second copy is a second " +
            "statement of security policy that fails silently when the contract moves"
        },
        {
            nameof(RawFetch),
            "raw fetch calls",
            "use the generated SDK functions from @/api so credentials and the XSRF header follow " +
            "one policy"
        },
    };

    [Theory]
    [MemberData(nameof(EscapedPolicies))]
    public void Request_execution_policy_is_stated_only_in_the_api_module(
        string patternName,
        string subject,
        string remedy)
    {
        var offenders = MatchesOutsideTheApiModule(PatternFor(patternName));

        offenders.ShouldBeEmpty(
            $"""
             {subject} belongs to {ApiModule} (ADR-025). {remedy}.

             Outside the api module:
               {string.Join("\n  ", offenders)}
             """);
    }

    [Fact]
    public void Every_scan_still_finds_its_subject()
    {
        // These four constructs exist exactly once each, inside the api module. If a scan stops
        // matching them the guard above goes green for the wrong reason.
        MatchesInsideTheApiModule(SuccessRule).ShouldNotBeEmpty(
            "web/src/api/request.ts must still hold the one success rule");
        MatchesInsideTheApiModule(ObjectUrl).ShouldNotBeEmpty(
            "web/src/api/request.ts must still hold the one blob download tail");
        MatchesInsideTheApiModule(CookieRead).ShouldNotBeEmpty(
            "web/src/api/client.ts must still hold the one XSRF cookie read");
        MatchesInsideTheApiModule(RawFetch).ShouldNotBeEmpty(
            "web/src/api/runtime.ts must still hold the one fetch call");
    }

    [Fact]
    public void The_web_source_scan_reaches_the_feature_modules()
    {
        // A scan that resolved to an empty file set would satisfy every rule above. Anchor it on
        // files that must exist for the SPA to have any request paths at all.
        var scanned = RepositorySource.Current.WebSourceFiles()
            .Select(file => file.RelativePath.Replace('\\', '/'))
            .ToList();

        scanned.ShouldContain("web/src/features/banking/banking.ts");
        scanned.ShouldContain("web/src/features/reports/reports.ts");
        scanned.ShouldContain("web/src/lib/telemetry.ts");
        scanned.ShouldNotContain(
            path => path.Contains("/generated/", StringComparison.Ordinal),
            "the generated client is not authored here and states transport policy by design");
    }

    private static Regex PatternFor(string name) => name switch
    {
        nameof(SuccessRule) => SuccessRule,
        nameof(ObjectUrl) => ObjectUrl,
        nameof(CookieRead) => CookieRead,
        nameof(RawFetch) => RawFetch,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown pattern."),
    };

    private static IReadOnlyList<string> MatchesOutsideTheApiModule(Regex pattern) =>
        [.. Scan(pattern, inside: false)];

    private static IReadOnlyList<string> MatchesInsideTheApiModule(Regex pattern) =>
        [.. Scan(pattern, inside: true)];

    private static IEnumerable<string> Scan(Regex pattern, bool inside) =>
        RepositorySource.Current.WebSourceFiles()
            .Where(file => IsInApiModule(file.RelativePath) == inside)
            .SelectMany(file => file.Find(pattern))
            .Select(match => match.ToString());

    private static bool IsInApiModule(string relativePath) =>
        relativePath.Replace('\\', '/').StartsWith($"{ApiModule}/", StringComparison.Ordinal);
}
