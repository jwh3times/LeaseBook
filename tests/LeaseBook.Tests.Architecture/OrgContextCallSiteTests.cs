using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The organization plane's single-setter rule (#314, ADR-048), enforced the way
/// <see cref="PlatformScopeCallSiteTests"/> enforces ADR-028's. <c>app.org_id</c> decides which
/// organization a transaction reads; <c>app.persona</c> and <c>app.user_id</c> decide which slice of
/// it. A second setter of any of the three is a way to widen a unit of work that no isolation test
/// would notice — and a session-level one (<c>SET</c>, <c>ALTER ROLE … SET</c>, a connection-string
/// <c>-c</c> option) would leak onto every pooled connection.
/// <para>
/// Scope is <c>src/</c>, <c>infra/</c> and <c>tests/</c>, for the reasons the platform guard gives.
/// Three sanctioned setters: the executor in production, the raw probe in tests, and the operator
/// restore spot-check, which runs as the SELECT-only ops role inside a <c>READ ONLY</c> transaction
/// and must state a persona or read nothing.
/// </para>
/// </summary>
public sealed class OrgContextCallSiteTests
{
    private const string Gucs = @"app\.(?:org_id|persona|user_id)";

    // The SET form requires its assignment (= or TO), which is what separates the statement — and
    // ALTER ROLE … SET — from prose such as "the executor, which set app.org_id". The bare
    // "app.x =" form catches a connection-string or PGOPTIONS "-c app.persona=system".
    private static readonly Regex SetsOrgContext = new(
        $@"set_config\s*\(\s*'{Gucs}'|\bSET\s+(?:LOCAL\s+|SESSION\s+)?{Gucs}\s*(?:=|\bTO\b)|\b{Gucs}\s*=",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Every <c>set_config</c> of one of the three, with its third (<c>is_local</c>) argument.</summary>
    private static readonly Regex SetConfigCall = new(
        $@"set_config\(\s*'(?<guc>{Gucs})'\s*,\s*(?<value>[^,]+?)\s*,\s*(?<local>\w+)\s*\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string Executor =
        Path.Combine("src", "LeaseBook.SharedKernel", "Tenancy", "OrgScopedExecutor.cs");

    private static readonly string Probe = Path.Combine("tests", "LeaseBook.Tests.Common", "RlsProbe.cs");

    private static readonly string RestoreCheck = Path.Combine("infra", "db", "verify.sql");

    [Fact]
    public void Only_the_sanctioned_setters_touch_the_org_context_gucs()
    {
        // Full relative paths, never file names: an EndsWith would exempt a same-named file anywhere.
        string[] allowed = [Executor, Probe, RestoreCheck];
        var offenders = new List<string>();

        foreach (var file in RepositorySource.Current.CodeFilesUnder("src", "infra", "tests"))
        {
            if (allowed.Contains(file.RelativePath, StringComparer.Ordinal))
            {
                continue;
            }

            offenders.AddRange(file.Find(SetsOrgContext).Select(match => match.ToString()));
        }

        offenders.ShouldBeEmpty(
            "set org context through OrgScopedExecutor in src/, or RlsProbe in tests/ (#314, ADR-048) — a " +
            "second setter of app.org_id, app.persona or app.user_id is an unaudited way to widen a unit of work" +
            (offenders.Count == 0 ? "" : Environment.NewLine + string.Join(Environment.NewLine, offenders)));
    }

    /// <summary>
    /// The sanctioned setters still set what they are sanctioned for, and only transaction-locally —
    /// so the guard above cannot pass vacuously over files that stopped doing the job, and none of
    /// them can leak a persona onto a pooled connection.
    /// </summary>
    [Theory]
    [InlineData("executor", "app.org_id", "app.persona", "app.user_id")]
    [InlineData("probe", "app.org_id", "app.persona", "app.user_id")]
    [InlineData("restore-check", "app.org_id", "app.persona")]
    public void A_sanctioned_setter_sets_its_gucs_transaction_locally(string setter, params string[] gucs)
    {
        var path = setter switch
        {
            "executor" => Executor,
            "probe" => Probe,
            _ => RestoreCheck,
        };
        var calls = SetConfigCall.Matches(RepositorySource.Current.File(path).Text);

        calls.Select(call => call.Groups["guc"].Value).Distinct().Order(StringComparer.Ordinal)
            .ShouldBe(gucs.Order(StringComparer.Ordinal), $"{path} sets exactly these org-context GUCs");
        foreach (Match call in calls)
        {
            call.Groups["local"].Value.ShouldBe("true",
                $"{path}: {call.Value} must be transaction-local — the third argument is is_local");
        }
    }

    /// <summary>The executor sets all three in ONE statement, so no statement ever runs between them.</summary>
    [Fact]
    public void The_executor_sets_org_and_persona_in_one_statement()
    {
        var source = RepositorySource.Current.File(Executor).Text;
        var statement = Regex.Match(source, @"SELECT set_config\('app\.org_id'.*?set_config\('app\.user_id'[^)]*\)", RegexOptions.Singleline);

        statement.Success.ShouldBeTrue("app.org_id through app.user_id must be one SELECT");
        // Concatenated so this file's own source is not itself a match for the scan above.
        statement.Value.ShouldContain("set_config(" + "'app.persona'");
        statement.Value.ShouldNotContain(";");
    }

    /// <summary>
    /// The bite test: every shape a second setter could take is recognised, and the prose that
    /// describes the executor is not. Each literal is split so this file is not itself a match.
    /// </summary>
    [Theory]
    [InlineData("SELECT set_config(" + "'app.persona', 'system', true)", true)]
    [InlineData("SELECT set_config(" + "'app.user_id', @u, false)", true)]
    [InlineData("SET LOCAL app." + "persona = 'staff'", true)]
    [InlineData("SET app." + "org_id TO '1'", true)]
    [InlineData("ALTER ROLE leasebook_app SET app." + "persona TO 'system'", true)]
    [InlineData("Host=db;Options=-c app." + "persona=system", true)]
    [InlineData("run inside the request middleware or OrgScopedExecutor, which set app." + "org_id (§C.4).", false)]
    [InlineData("current_setting(" + "'app.persona', true) = 'owner'", false)]
    public void The_scan_recognises_every_setter_shape(string line, bool isSetter)
    {
        SetsOrgContext.IsMatch(line).ShouldBe(isSetter);
    }

    /// <summary>The restore spot-check reads org-wide, so it must state the staff persona — nothing wider.</summary>
    [Fact]
    public void The_restore_check_reads_as_staff()
    {
        Regex.IsMatch(
                RepositorySource.Current.File(RestoreCheck).Text,
                @"set_config\(\s*'app\.persona'\s*,\s*'staff'\s*,\s*true\s*\)")
            .ShouldBeTrue();
    }
}
