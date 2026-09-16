using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The fiduciary pass/fail marks on an owner statement PDF are drawn from the same vector paths the
/// SPA's icon set uses, so the printed panel and the on-screen one carry the identical mark
/// (issue #396). <c>StatementPdf</c> cannot import <c>Icon.tsx</c>, so it restates the two path
/// literals — and a restatement nobody checks is a copy that drifts.
/// <para>
/// Nothing else would catch that drift. The statement tests count subpaths rather than compare path
/// data, so editing the SPA icon would leave the PDF silently showing the old mark with every test
/// green. This repo's standing answer to a duplicated contract is a generator plus a drift gate; at
/// two constants a generator earns nothing, so the gate stands on its own.
/// </para>
/// </summary>
public sealed class StatementMarkIconDriftTests
{
    private const string IconSource = "web/src/design/Icon.tsx";
    private const string RendererSource = "src/LeaseBook.Web/Reporting/StatementPdf.cs";

    /// <summary>
    /// The SPA icon name paired with the C# constant that must restate it. Both marks, not one: the
    /// pass mark alone would leave the fail mark free to drift, and the fail mark is the one an owner
    /// only ever sees when something is already wrong.
    /// </summary>
    private static readonly (string IconName, string ConstantName)[] MirroredPaths =
    [
        ("check", "CheckPath"),
        ("alert", "AlertPath"),
    ];

    [Theory]
    [InlineData("check", "CheckPath")]
    [InlineData("alert", "AlertPath")]
    public void Statement_mark_restates_the_spa_icon_path_exactly(string iconName, string constantName)
    {
        ReadIconPath(iconName).ShouldBe(ReadRendererPath(constantName),
            $"{RendererSource} restates the SPA's '{iconName}' icon as {constantName}; they have drifted, "
            + "so a statement PDF now prints a different mark than the screen shows");
    }

    /// <summary>
    /// Pins both readers against finding nothing. A regex that silently stops matching would make the
    /// comparison above trivially true — two empty strings are equal — so the guard has to prove it can
    /// still read a real path out of each file before its equality assertion means anything.
    /// </summary>
    [Fact]
    public void The_readers_still_find_a_path_in_each_file()
    {
        foreach (var (iconName, constantName) in MirroredPaths)
        {
            // StartsWith(...).ShouldBeTrue(message) rather than ShouldStartWith(expected, message):
            // Shouldly's third parameter there is a Case, not a custom message, so the message
            // overload does not bind.
            ReadIconPath(iconName).StartsWith('M').ShouldBeTrue(
                $"could not read the '{iconName}' path out of {IconSource}");
            ReadRendererPath(constantName).StartsWith('M').ShouldBeTrue(
                $"could not read {constantName} out of {RendererSource}");
        }
    }

    /// <summary>Reads <c>name: 'M…'</c> out of the SPA's <c>ICONS</c> map.</summary>
    private static string ReadIconPath(string iconName)
    {
        var text = RepositorySource.Current.File(IconSource).Text;
        var match = Regex.Match(text, $@"\b{Regex.Escape(iconName)}:\s*'([^']+)'");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// Reads <c>private const string Name = "M…";</c> out of the renderer. The declaration may wrap
    /// onto the following line, which is why this does not anchor the literal to the same line.
    /// </summary>
    private static string ReadRendererPath(string constantName)
    {
        var text = RepositorySource.Current.File(RendererSource).Text;
        var match = Regex.Match(
            text,
            $@"const\s+string\s+{Regex.Escape(constantName)}\s*=\s*""([^""]+)""");

        return match.Success ? match.Groups[1].Value : string.Empty;
    }
}
