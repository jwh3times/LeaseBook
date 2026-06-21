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

/// <summary>One statement line's resolution: its kind and the register line it matched, if any.</summary>
public sealed record MatchResult(Guid StatementLineId, MatchKind Kind, Guid? JournalLineId);

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

        var claimed = new HashSet<Guid>();
        var results = new MatchResult?[statementLines.Count];

        // Pass 1: exact amount within the date window → matched (claim the closest candidate).
        for (var i = 0; i < statementLines.Count; i++)
        {
            var line = statementLines[i];
            var best = BestUnclaimed(line, candidates, claimed, c => DayGap(c, line) <= windowDays);
            if (best is not null)
            {
                claimed.Add(best.JournalLineId);
                results[i] = new MatchResult(line.StatementLineId, MatchKind.Matched, best.JournalLineId);
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
            var best = BestUnclaimed(line, candidates, claimed, _ => true);
            results[i] = best is not null
                ? AssignSuggested(line, best, claimed)
                : new MatchResult(line.StatementLineId, MatchKind.Unmatched, null);
        }

        return results.Select(r => r!).ToList();
    }

    private static MatchResult AssignSuggested(MatchInput line, RegisterCandidate best, HashSet<Guid> claimed)
    {
        claimed.Add(best.JournalLineId);
        return new MatchResult(line.StatementLineId, MatchKind.Suggested, best.JournalLineId);
    }

    // The unclaimed exact-amount candidate closest in date (ties broken by id, for determinism).
    private static RegisterCandidate? BestUnclaimed(
        MatchInput line, IReadOnlyList<RegisterCandidate> candidates, HashSet<Guid> claimed,
        Func<RegisterCandidate, bool> within) =>
        candidates
            .Where(c => !claimed.Contains(c.JournalLineId) && c.Amount == line.Amount && within(c))
            .OrderBy(c => DayGap(c, line))
            .ThenBy(c => c.JournalLineId)
            .FirstOrDefault();

    private static int DayGap(RegisterCandidate candidate, MatchInput line) =>
        Math.Abs(candidate.Date.DayNumber - line.Date.DayNumber);
}
