using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The generated SPA client must not bake an absolute origin. <c>web/src/api/runtime.ts</c> sets
/// <c>baseUrl: window.location.origin</c> on every client — the SPA is served by the host in
/// production and proxied to it in development, so requests are always same-origin and a generated
/// default is dead config.
/// <para>
/// It is dead config that still cost a build, though (#369). Handing the generator a URL input made it
/// infer a server from that origin and emit <c>baseUrl: 'http://localhost:5080/'</c> — both as the
/// client's default and as a narrowed type — so the documented <c>npm run api:generate</c> produced a
/// client the CI drift gate rejected, with an error message advising the reader to re-run the command
/// that caused it. The generator now reads the build-emitted document, and the client plugin sets
/// <c>baseUrl: false</c>; this is the assertion that notices either of those being undone.
/// </para>
/// <para>
/// It would <b>not</b> catch the emitted document gaining a <c>servers</c> entry — <c>resolveBaseUrl</c>
/// returns early on <c>baseUrl: false</c>, before the servers are consulted, so such an entry produces
/// no output at all. That is an argument for keeping <c>baseUrl: false</c>, not a property of this test.
/// </para>
/// <para>
/// Scanning generated output is deliberate. Every other web guard skips it — <c>WebSourceFiles()</c>
/// excludes <c>generated</c> — because generated code is not hand-authored and reviewing it line by
/// line is not the point. This is the one property of it worth pinning, precisely because nobody reads
/// the diff.
/// </para>
/// </summary>
public sealed class GeneratedClientOriginTests
{
    private const string GeneratedClientRoot = "web/src/api/generated";

    /// <summary>
    /// An absolute origin bound to a <c>baseUrl</c> near it — the shape the bug took both times: a
    /// runtime default, <c>createConfig({ baseUrl: 'http://localhost:5080/' })</c>, and a narrowed
    /// type, <c>baseUrl: 'http://localhost:5080/' | (string &amp; {})</c>.
    /// <para>
    /// Keyed on the property rather than on URLs in general. The vendored client and core runtime the
    /// generator copies in are full of documentation links — npmjs, MDN, swagger.io — and an allowlist
    /// of those would grow with every generator upgrade while guarding nothing: the property worth
    /// pinning is that no origin is <b>configured</b>, not that none is mentioned.
    /// </para>
    /// <para>
    /// Case-insensitive because the key is the client plugin's, not ours — <c>getClientBaseUrlKey</c>
    /// returns <c>baseURL</c> for the axios and nuxt clients, so a plugin swap would otherwise leave
    /// this passing while guarding nothing. Matched over the whole file within a bounded window rather
    /// than line by line, so it does not depend on the generator's printer choosing not to wrap.
    /// </para>
    /// </summary>
    private static readonly Regex ConfiguredOrigin = new(
        @"baseUrl[^;{}]{0,120}?(?<origin>https?://[A-Za-z0-9.\-]+(:\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    [Fact]
    public void The_generated_client_bakes_no_absolute_origin()
    {
        var files = RepositorySource.Current.WebGeneratedFiles(GeneratedClientRoot);
        files.ShouldNotBeEmpty($"no generated client found under {GeneratedClientRoot} — the scan is broken");

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = file.Text;
            foreach (Match match in ConfiguredOrigin.Matches(text))
            {
                var line = text.AsSpan(0, match.Index).Count('\n') + 1;
                offenders.Add($"{file.RelativePath}:{line}: {match.Groups["origin"].Value}");
            }
        }

        offenders.ShouldBeEmpty(
            "the generated API client hardcodes an origin. web/src/api/runtime.ts owns baseUrl, so this " +
            "is both dead config and a diff the CI drift gate will reject — regenerate with " +
            "`npm run api:generate`, which reads the build-emitted document rather than a running host " +
            $"(#369): {string.Join("; ", offenders)}");
    }
}
