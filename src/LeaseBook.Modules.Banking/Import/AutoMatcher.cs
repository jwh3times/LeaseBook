using LeaseBook.Modules.Banking.Contracts;

namespace LeaseBook.Modules.Banking.Import;

/// <summary>How a statement line resolved against the register (P67); also the persisted <c>statement_matches.kind</c>.</summary>
public enum MatchKind
{
    Matched,
    Suggested,
    Unmatched,
    Created,
}

/// <summary>The minimal statement-line shape the matcher needs (id + date + signed amount).</summary>
public sealed record MatchInput(Guid StatementLineId, DateOnly Date, decimal Amount);

/// <summary>
/// One statement line's resolution: its kind and what it matched, if anything. A match is either one
/// register line (<see cref="JournalLineId"/>) or a whole group of them (<see cref="GroupRef"/>), never both.
/// </summary>
public sealed record MatchResult(Guid StatementLineId, MatchKind Kind, Guid? JournalLineId, string? GroupRef = null);

/// <summary>The wire/storage strings for <see cref="MatchKind"/> — the <c>statement_matches.kind</c> contract.</summary>
public static class MatchKinds
{
    public const string Matched = "matched";
    public const string Suggested = "suggested";
    public const string Unmatched = "unmatched";
    public const string Created = "created";

    public static readonly string[] All = [Matched, Suggested, Unmatched, Created];

    public static string ToDb(this MatchKind kind) => kind switch
    {
        MatchKind.Matched => Matched,
        MatchKind.Suggested => Suggested,
        MatchKind.Unmatched => Unmatched,
        MatchKind.Created => Created,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown match kind."),
    };

    /// <summary>
    /// True once a confirmed decision should clear its register line: an auto/suggested match (P67), or a
    /// <c>created</c> transaction that now stands in for the statement line.
    /// </summary>
    public static bool ClearsRegisterLine(string kind) => kind is Matched or Suggested or Created;
}

/// <summary>
/// The auto-match heuristic (P67): pure, deterministic classification of imported statement lines against
/// the uncleared register candidates. Exact amount + date within ±<see cref="DefaultWindowDays"/> →
/// <see cref="MatchKind.Matched"/>; exact amount outside the window → <see cref="MatchKind.Suggested"/>;
/// no amount match → <see cref="MatchKind.Unmatched"/>. Two passes (in-window first), greedily claiming the
/// closest candidate by date and never assigning one candidate to two statement lines.
/// <para>
/// A statement line matches one <i>unit</i>: a lone register line, or a whole group of lines that share a
/// <see cref="RegisterCandidate.GroupRef"/>, taken at the group's signed sum (ADR-053). A grouped line is
/// never a unit by itself, so a statement line cannot match, and so cannot clear, part of a group.
/// </para>
/// </summary>
public static class AutoMatcher
{
    public const int DefaultWindowDays = 4;

    public static IReadOnlyList<MatchResult> Match(
        IReadOnlyList<MatchInput> statementLines,
        IReadOnlyList<RegisterCandidate> candidates,
        int windowDays = DefaultWindowDays)
    {
        ArgumentNullException.ThrowIfNull(statementLines);
        ArgumentNullException.ThrowIfNull(candidates);

        var units = Units(candidates);
        var claimed = new HashSet<Guid>();
        var results = new MatchResult?[statementLines.Count];

        // Pass 1: exact amount within the date window → matched (claim the closest unit).
        for (var i = 0; i < statementLines.Count; i++)
        {
            var line = statementLines[i];
            var best = BestUnclaimed(line, units, claimed, u => DayGap(u, line) <= windowDays);
            if (best is not null)
            {
                results[i] = Assign(line, best, MatchKind.Matched, claimed);
            }
        }

        // Pass 2: the rest — exact amount, any date → suggested; otherwise unmatched.
        for (var i = 0; i < statementLines.Count; i++)
        {
            if (results[i] is not null)
            {
                continue;
            }

            var line = statementLines[i];
            var best = BestUnclaimed(line, units, claimed, _ => true);
            results[i] = best is not null
                ? Assign(line, best, MatchKind.Suggested, claimed)
                : new MatchResult(line.StatementLineId, MatchKind.Unmatched, null);
        }

        return results.Select(r => r!).ToList();
    }

    /// <summary>
    /// The lines of each group among <paramref name="candidates"/>, keyed by group reference. The one
    /// definition of a group's membership, shared by the matcher, the preview and the confirmation.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<RegisterCandidate>> Groups(
        IReadOnlyList<RegisterCandidate> candidates) =>
        candidates
            .Where(c => c.GroupRef is not null)
            .GroupBy(c => c.GroupRef!, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<RegisterCandidate>)[.. g.OrderBy(c => c.Date).ThenBy(c => c.JournalLineId)],
                StringComparer.Ordinal);

    /// <summary>A group's date for matching: the latest of its lines (a payout posts them all on one day).</summary>
    public static DateOnly GroupDate(IReadOnlyList<RegisterCandidate> group) => group.Max(c => c.Date);

    // What one statement line can match. Key is the journal line id, or a group's lowest one — unique
    // either way, and stable, so ties still break deterministically.
    private sealed record Unit(Guid Key, DateOnly Date, decimal Amount, Guid? JournalLineId, string? GroupRef);

    private static List<Unit> Units(IReadOnlyList<RegisterCandidate> candidates)
    {
        var units = candidates
            .Where(c => c.GroupRef is null)
            .Select(c => new Unit(c.JournalLineId, c.Date, c.Amount, c.JournalLineId, null))
            .ToList();
        // A group that nets to nothing never reaches the bank, so no statement line can answer for it.
        units.AddRange(Groups(candidates)
            .Select(g => new Unit(
                g.Value.Min(c => c.JournalLineId), GroupDate(g.Value), g.Value.Sum(c => c.Amount), null, g.Key))
            .Where(u => u.Amount != 0m));
        return units;
    }

    private static MatchResult Assign(MatchInput line, Unit best, MatchKind kind, HashSet<Guid> claimed)
    {
        claimed.Add(best.Key);
        return new MatchResult(line.StatementLineId, kind, best.JournalLineId, best.GroupRef);
    }

    // The unclaimed exact-amount unit closest in date (ties broken by id, for determinism).
    private static Unit? BestUnclaimed(
        MatchInput line, List<Unit> units, HashSet<Guid> claimed, Func<Unit, bool> within) =>
        units
            .Where(u => !claimed.Contains(u.Key) && u.Amount == line.Amount && within(u))
            .OrderBy(u => DayGap(u, line))
            .ThenBy(u => u.Key)
            .FirstOrDefault();

    private static int DayGap(Unit unit, MatchInput line) =>
        Math.Abs(unit.Date.DayNumber - line.Date.DayNumber);
}
