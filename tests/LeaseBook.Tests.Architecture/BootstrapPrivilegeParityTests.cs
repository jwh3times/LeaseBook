using System.Text.RegularExpressions;
using Shouldly;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The privilege model in <c>infra/db/bootstrap.sql</c> (local/CI) and
/// <c>infra/db/azure-bootstrap.sql</c> (the administration image, ADR-027) must stay the same model.
///
/// <para>
/// They are two files because the surrounding mechanics genuinely differ — Azure has no superuser,
/// the database already exists, passwords arrive through client-side <c>\password</c>, and the script
/// must be replayable. None of that is a reason for the <i>grants</i> to differ, and when they did,
/// the Azure side was the one missing <c>REVOKE ALL ON SCHEMA public FROM PUBLIC</c> and the sequence
/// default privilege. Every integration test runs <c>bootstrap.sql</c>, so the dev side is exercised
/// constantly; the Azure side is exercised only by the administration-image test, and only for the
/// properties that test happens to assert.
/// </para>
///
/// <para>
/// So this compares statements rather than files, one-directionally: everything the dev bootstrap
/// grants must appear in the Azure script. The Azure script may add more — the <c>WITH SET TRUE</c>
/// membership, the drift check, idempotency guards — and legitimately omits the statements listed in
/// <see cref="NotApplicableToAzure"/>, each for a stated reason. A literal diff would be useless
/// here; the point is that a privilege added to one side cannot be silently absent from the other.
/// </para>
/// </summary>
public sealed class BootstrapPrivilegeParityTests
{
    private const string DevScript = "infra/db/bootstrap.sql";
    private const string AzureScript = "infra/db/azure-bootstrap.sql";

    /// <summary>
    /// Statements that carry a privilege or ownership decision. Anything matching this is part of the
    /// model both scripts must agree on.
    /// </summary>
    private static readonly Regex PrivilegeStatement = new(
        @"^(GRANT|REVOKE|ALTER\s+DEFAULT\s+PRIVILEGES|ALTER\s+SCHEMA|CREATE\s+SCHEMA)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Dev-only privilege statements with no Azure counterpart, each with the reason Azure's starting
    /// state differs. <b>Empty on purpose:</b> the two scripts currently agree on every privilege
    /// statement, and an entry here is a documented exception rather than a way to quiet the test.
    /// Adding one means arguing why Azure should run a different privilege model than every
    /// integration test exercises.
    /// <para>
    /// Note that the structural differences do not need entries: <c>CREATE DATABASE</c> is not a
    /// privilege statement and never enters the comparison, and the psql meta-commands the Azure
    /// script relies on are not SQL statements at all.
    /// </para>
    /// </summary>
    private static readonly Dictionary<string, string> NotApplicableToAzure =
        new(StringComparer.Ordinal);

    [Fact]
    public void Every_privilege_the_dev_bootstrap_grants_is_also_granted_by_the_azure_script()
    {
        var dev = PrivilegeStatements(DevScript);
        var azure = PrivilegeStatements(AzureScript);

        dev.ShouldNotBeEmpty($"{DevScript} parsed to no privilege statements — the parser is broken, " +
                             "not the scripts.");
        azure.ShouldNotBeEmpty($"{AzureScript} parsed to no privilege statements — the parser is " +
                               "broken, not the scripts.");

        var missing = dev
            .Where(statement => !NotApplicableToAzure.ContainsKey(statement))
            .Where(statement => !azure.Contains(statement))
            .ToList();

        missing.ShouldBeEmpty(missing.Count == 0
            ? ""
            : $"{AzureScript} is missing {missing.Count} privilege statement(s) that {DevScript} " +
              "applies. Azure would then run with a different privilege model than every test " +
              "exercises, which is how the public-schema REVOKE and the sequence grant went missing " +
              "before. Add them, or add an entry to NotApplicableToAzure saying why Azure differs." +
              Environment.NewLine + string.Join(Environment.NewLine, missing));
    }

    /// <summary>
    /// The exclusion list is itself a source of drift: an entry naming a statement the dev script no
    /// longer contains is a stale justification nobody will re-read, and it would silently excuse a
    /// future statement that happened to normalize to the same text.
    /// </summary>
    [Fact]
    public void The_not_applicable_list_names_only_statements_that_still_exist()
    {
        var dev = PrivilegeStatements(DevScript);

        foreach (var (statement, reason) in NotApplicableToAzure)
        {
            dev.ShouldContain(statement,
                $"NotApplicableToAzure excuses '{statement}' ({reason}), but {DevScript} no longer " +
                "contains it. Remove the entry.");
        }
    }

    /// <summary>
    /// Splits on statement terminators rather than lines, because the default-privilege statements
    /// wrap, and normalizes the spelling differences that carry no meaning: repeated whitespace, and
    /// the <c>IF NOT EXISTS</c> the replayable Azure script needs and the run-once dev script does
    /// not. psql meta-commands (<c>\connect</c>, <c>\password</c>, <c>\gexec</c>) are not SQL
    /// statements and never match the privilege pattern.
    /// </summary>
    private static HashSet<string> PrivilegeStatements(string relativePath)
    {
        var sql = RepositorySource.Current.File(relativePath).Text;

        // Strip line comments before splitting: a ';' inside one would otherwise end a statement.
        var withoutComments = string.Join('\n',
            sql.Split('\n').Select(line =>
            {
                var comment = line.IndexOf("--", StringComparison.Ordinal);
                return comment >= 0 ? line[..comment] : line;
            }));

        return withoutComments
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize)
            .Where(statement => PrivilegeStatement.IsMatch(statement))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Normalize(string statement)
    {
        var collapsed = Regex.Replace(statement, @"\s+", " ").Trim();
        return Regex.Replace(collapsed, @"\bIF NOT EXISTS\s+", "", RegexOptions.IgnoreCase);
    }
}
