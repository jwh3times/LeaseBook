using System.Text.RegularExpressions;

namespace LeaseBook.Tests.Architecture;

/// <summary>
/// The one SQL vocabulary for reading migration source: which tables <c>FORCE ROW LEVEL SECURITY</c>,
/// and whether a migration's DML against one of them lifts FORCE around itself.
/// <para>
/// Split from its test class on purpose. A guard that can only be exercised by planting a real
/// violation in migration history cannot be verified — migrations are permanent, so the bad example
/// would have to ship. Keeping the analysis pure lets <see cref="MigrationBackfillRlsTests"/> feed it
/// the exact pre-fix v0.7.0 shape as a string and prove it bites.
/// </para>
/// </summary>
internal static class MigrationRlsGuard
{
    /// <summary>
    /// Every <c>Rls</c> helper emits <c>FORCE ROW LEVEL SECURITY</c>, so the table argument of any of
    /// them joins the forced set — not just the org-isolation one. <c>feature_flags</c> and
    /// <c>platform_audit_events</c> gate on <c>app.platform</c>, which a migration transaction sets no
    /// more than it sets <c>app.org_id</c>, so their DML is filtered to zero rows the same way.
    /// </summary>
    private static readonly Regex EnablesForcedRls = new(
        @"\.Enable\w*Rls\w*\(\s*""(?<table>\w+)""",
        RegexOptions.Compiled);

    /// <summary>Raw-string SQL blocks, plus the single-line <c>Sql("…")</c> form.</summary>
    private static readonly Regex RawStringBlock = new(
        "\"\"\"(?<body>.*?)\"\"\"",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex SingleLineSql = new(
        @"\.Sql\(\s*""(?<body>(?:[^""\\\n]|\\.)*)""\s*\)",
        RegexOptions.Compiled);

    /// <summary>
    /// The statement shapes RLS filters <b>silently</b>. A plain <c>INSERT … VALUES</c> into a forced
    /// table is deliberately absent: its WITH CHECK fails closed with <c>42501</c>, so it aborts the
    /// migration at the mistake instead of succeeding against nothing. The hazard this guard exists
    /// for is the write that reports success having touched zero rows.
    /// </summary>
    private static readonly Regex UpdateStatement =
        new(@"^UPDATE\s+(?<table>\w+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DeleteStatement =
        new(@"^DELETE\s+FROM\s+(?<table>\w+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex InsertStatement =
        new(@"^INSERT\s+INTO\s+(?<table>\w+)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LiftsForce = new(
        @"^ALTER\s+TABLE\s+(?<table>\w+)\s+NO\s+FORCE\s+ROW\s+LEVEL\s+SECURITY\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RestoresForce = new(
        @"^ALTER\s+TABLE\s+(?<table>\w+)\s+FORCE\s+ROW\s+LEVEL\s+SECURITY\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Any DML the block extractor below would not see. If a backfill is ever written with a verbatim
    /// or concatenated literal, this is what fails rather than the scan quietly covering nothing.
    /// <para>
    /// EF's own <c>InsertData</c>/<c>UpdateData</c>/<c>DeleteData</c> builders are included, and they
    /// are not merely unreadable — they are unfixable. Each emits its statement on its own, so there
    /// is no block in which the lift could bracket it, and the surrounding <c>ALTER TABLE</c> would
    /// have to be two extra <c>Sql</c> calls whose ordering nothing enforces. Raw SQL is the only
    /// shape where the bracketing and the write are one reviewable unit.
    /// </para>
    /// </summary>
    private static readonly Regex AnyDml = new(
        @"\bUPDATE\s+\w+\s+SET\b|\bDELETE\s+FROM\s+\w+|\bINSERT\s+INTO\s+\w+|\.(?:Insert|Update|Delete)Data\s*\(",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Tables any migration in <paramref name="files"/> put under FORCE ROW LEVEL SECURITY.</summary>
    public static IReadOnlySet<string> ForcedTables(IEnumerable<RepositoryFile> files)
    {
        var tables = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            foreach (Match match in EnablesForcedRls.Matches(file.SignificantText))
            {
                tables.Add(match.Groups["table"].Value);
            }
        }

        return tables;
    }

    /// <summary>
    /// SQL blocks in source order. Comment lines are already gone via
    /// <see cref="RepositoryFile.SignificantText"/>, so prose about an <c>UPDATE</c> is not mistaken
    /// for one.
    /// </summary>
    public static IReadOnlyList<string> SqlBlocks(RepositoryFile file)
    {
        var text = file.SignificantText;
        return
        [
            .. RawStringBlock.Matches(text).Select(match => match.Groups["body"].Value),
            .. SingleLineSql.Matches(text).Select(match => match.Groups["body"].Value),
        ];
    }

    /// <summary>DML the extractor missed, meaning the scan cannot speak for this file.</summary>
    public static bool HasDmlOutsideExtractedBlocks(RepositoryFile file)
    {
        var remainder = file.SignificantText;
        foreach (var block in SqlBlocks(file))
        {
            var at = remainder.IndexOf(block, StringComparison.Ordinal);
            if (at >= 0)
            {
                remainder = remainder.Remove(at, block.Length);
            }
        }

        return AnyDml.IsMatch(remainder);
    }

    /// <summary>
    /// Statements in <paramref name="block"/> that read or rewrite a forced table without FORCE lifted
    /// across them. Both halves are required: without the lift the write silently matches nothing;
    /// without the restore the migration leaves the table permanently unprotected against its own
    /// owner, which is the worse of the two failures.
    /// </summary>
    public static IReadOnlyList<string> FindUnbracketedDml(string block, IReadOnlySet<string> forcedTables)
    {
        var statements = Statements(block);
        var lifted = new Dictionary<string, int>(StringComparer.Ordinal);
        var restored = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var index = 0; index < statements.Count; index++)
        {
            var lift = LiftsForce.Match(statements[index]);
            if (lift.Success)
            {
                lifted.TryAdd(lift.Groups["table"].Value, index);
                continue;
            }

            var restore = RestoresForce.Match(statements[index]);
            if (restore.Success)
            {
                restored[restore.Groups["table"].Value] = index;
            }
        }

        var violations = new List<string>();
        for (var index = 0; index < statements.Count; index++)
        {
            var statement = statements[index];
            foreach (var table in DependsOnForcedTables(statement, forcedTables))
            {
                if (!lifted.TryGetValue(table, out var liftedAt) || liftedAt > index)
                {
                    violations.Add(
                        $"{Summarize(statement)} → no `ALTER TABLE {table} NO FORCE ROW LEVEL SECURITY` before it");
                }
                else if (!restored.TryGetValue(table, out var restoredAt) || restoredAt < index)
                {
                    violations.Add(
                        $"{Summarize(statement)} → FORCE never restored on {table} in the same block");
                }
            }
        }

        return violations;
    }

    /// <summary>
    /// Which forced tables a statement's success depends on. For <c>UPDATE</c>/<c>DELETE</c> that is
    /// every forced table it names — the target, and anything in a <c>FROM</c>/<c>USING</c> clause,
    /// since a filtered join source zeroes the result just as effectively as a filtered target. For
    /// <c>INSERT … SELECT</c> it is the read side only; the target's own WITH CHECK raises rather than
    /// filters.
    /// </summary>
    private static IEnumerable<string> DependsOnForcedTables(string statement, IReadOnlySet<string> forcedTables)
    {
        string scanned;
        if (UpdateStatement.IsMatch(statement) || DeleteStatement.IsMatch(statement))
        {
            scanned = statement;
        }
        else if (InsertStatement.IsMatch(statement))
        {
            var select = statement.IndexOf("SELECT", StringComparison.OrdinalIgnoreCase);
            if (select < 0)
            {
                yield break;
            }

            scanned = statement[select..];
        }
        else
        {
            yield break;
        }

        foreach (var table in forcedTables.OrderBy(table => table, StringComparer.Ordinal))
        {
            if (Regex.IsMatch(scanned, $@"\b{Regex.Escape(table)}\b", RegexOptions.IgnoreCase))
            {
                yield return table;
            }
        }
    }

    private static IReadOnlyList<string> Statements(string block) =>
        [
            .. block
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Split(';')
                .Select(statement => string.Join(
                    ' ',
                    statement
                        .Split('\n')
                        .Select(line => line.TrimStart().StartsWith("--", StringComparison.Ordinal) ? "" : line.Trim())
                        .Where(line => line.Length > 0)))
                .Where(statement => statement.Length > 0),
        ];

    private static string Summarize(string statement) =>
        statement.Length <= 80 ? statement : statement[..77] + "...";
}
