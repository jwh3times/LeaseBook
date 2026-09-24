using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The response-cookie policy is positional: it replaces the cookie feature only for middleware
/// registered after it. Keep the policy at the front of the application pipeline and keep direct
/// writers narrow enough that every exception is reviewed rather than inherited from a file-level
/// allowlist.
/// </summary>
public sealed class CookiePolicyCallSiteTests
{
    private const string ProgramPath = "src/LeaseBook.Web/Program.cs";
    private static readonly string XsrfWriterPath = Path.Combine(
        "src", "LeaseBook.Web", "Auth", "AuthEndpoints.cs");

    private static readonly Regex UsesCookiePolicy = new(
        @"\bconfiguredApp\.UseLeaseBookCookiePolicy\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex ExceptionHandlerThenCookiePolicy = new(
        @"configuredApp\.UseExceptionHandler\s*\(\s*\)\s*;\s*" +
        @"configuredApp\.UseLeaseBookCookiePolicy\s*\(\s*configuredApp\.Environment\s*\)\s*;",
        RegexOptions.Compiled);

    private static readonly Regex AccessesResponseCookies = new(
        @"\.Response\.Cookies\b",
        RegexOptions.Compiled);

    private static readonly Regex WritesSetCookieHeader = new(
        @"\bIResponseCookies\b|HeaderNames\.SetCookie|SetCookieHeaderValue|" +
        @"GetTypedHeaders\s*\(\s*\)\.SetCookie|" +
        @"\.Headers\s*\[\s*\""Set-Cookie\""\s*\]|" +
        @"\.Headers\.Append\s*\(\s*\""Set-Cookie\""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ExplicitXsrfWriter = new(
        @"http\.Response\.Cookies\.Append\s*\(\s*\""XSRF-TOKEN\""[\s\S]*?" +
        @"Secure\s*=\s*CookieSecurity\.IsSecureRequired\s*\(\s*environment\s*,\s*http\.Request\s*\)" +
        @"[\s\S]*?\}\s*\)\s*;",
        RegexOptions.Compiled);

    [Fact]
    public void Cookie_policy_is_the_first_application_middleware()
    {
        var program = RepositorySource.Current.File(ProgramPath).SignificantText;
        var registrations = UsesCookiePolicy.Matches(program);

        registrations.Count.ShouldBe(1, "the pipeline must have exactly one response-cookie policy");
        ExceptionHandlerThenCookiePolicy.IsMatch(program).ShouldBeTrue(
            "the cookie policy must immediately follow the exception handler so no application " +
            "middleware can be inserted ahead of it without this guard failing");

        var policyAt = registrations[0].Index;
        policyAt.ShouldBeLessThan(
            RequiredPosition(program, "configuredApp.UseAuthentication()"),
            "authentication mints the application and two-factor correlation cookies");
        policyAt.ShouldBeLessThan(
            RequiredPosition(program, "configuredApp.MapModuleEndpoints(endpointAssemblies)"),
            "endpoints can mint antiforgery and hand-written response cookies");
    }

    [Fact]
    public void Only_the_explicitly_secure_xsrf_cookie_is_written_directly()
    {
        var writes = RepositorySource.Current
            .CodeFilesUnder("src")
            .SelectMany(file => file.Find(AccessesResponseCookies))
            .ToArray();

        writes.Length.ShouldBe(1,
            "new response-cookie writers require an explicit review and must stay behind the " +
            "pipeline policy" + Diagnostics(writes));
        writes[0].RelativePath.ShouldBe(XsrfWriterPath);
        writes[0].Text.ShouldContain("Response.Cookies.Append(\"XSRF-TOKEN\"");

        var source = RepositorySource.Current.File(XsrfWriterPath).Text;
        ExplicitXsrfWriter.IsMatch(source).ShouldBeTrue(
            "the JS-readable XSRF token must state its environment-derived Secure decision at the " +
            "call site as well as passing through the pipeline policy");
    }

    [Fact]
    public void No_application_code_writes_set_cookie_headers_directly()
    {
        var offenders = RepositorySource.Current
            .CodeFilesUnder("src")
            .SelectMany(file => file.Find(WritesSetCookieHeader))
            .ToArray();

        offenders.ShouldBeEmpty(
            "write cookies through HttpResponse.Cookies so CookiePolicy can apply the environment's " +
            "Secure requirement" + Diagnostics(offenders));
    }

    private static int RequiredPosition(string source, string marker)
    {
        var index = source.IndexOf(marker, StringComparison.Ordinal);
        index.ShouldBeGreaterThanOrEqualTo(0, $"Program.cs must still contain '{marker}'");
        return index;
    }

    private static string Diagnostics(IEnumerable<RepositoryMatch> matches)
    {
        var lines = matches.Select(match => match.ToString()).ToArray();
        return lines.Length == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, lines);
    }
}
