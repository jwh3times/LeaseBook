using LeaseBook.Modules.Banking.Contracts;
using LeaseBook.Modules.Banking.Import;
using Shouldly;

namespace LeaseBook.Tests.Banking;

/// <summary>
/// The auto-match heuristic (P67): each imported statement line vs. the uncleared register candidates —
/// exact amount + date within ±N days → <c>matched</c>; exact amount but outside the window →
/// <c>suggested</c>; no amount match → <c>unmatched</c>. Pure, deterministic, and a candidate is never
/// claimed by two statement lines.
/// </summary>
public sealed class AutoMatcherTests
{
    private static readonly DateOnly Feb1 = new(2026, 2, 1);
    private static Guid Id() => Guid.NewGuid();

    [Fact]
    public void Exact_amount_within_the_window_is_matched()
    {
        var lineId = Id();
        var candId = Id();
        var lines = new[] { new MatchInput(lineId, Feb1, 1500.00m) };
        var candidates = new[] { new RegisterCandidate(candId, Feb1.AddDays(2), 1500.00m, "ACH RENT") };

        var results = AutoMatcher.Match(lines, candidates);

        var r = results.ShouldHaveSingleItem();
        r.StatementLineId.ShouldBe(lineId);
        r.Kind.ShouldBe(MatchKind.Matched);
        r.JournalLineId.ShouldBe(candId);
    }

    [Fact]
    public void Exact_amount_outside_the_window_is_suggested()
    {
        var lineId = Id();
        var candId = Id();
        var lines = new[] { new MatchInput(lineId, Feb1, 1500.00m) };
        var candidates = new[] { new RegisterCandidate(candId, Feb1.AddDays(9), 1500.00m, "ACH RENT") };

        var results = AutoMatcher.Match(lines, candidates);

        results[0].Kind.ShouldBe(MatchKind.Suggested);
        results[0].JournalLineId.ShouldBe(candId);
    }

    [Fact]
    public void No_amount_match_is_unmatched()
    {
        var lines = new[] { new MatchInput(Id(), Feb1, 1500.00m) };
        var candidates = new[] { new RegisterCandidate(Id(), Feb1, 42.00m, "Something else") };

        var results = AutoMatcher.Match(lines, candidates);

        results[0].Kind.ShouldBe(MatchKind.Unmatched);
        results[0].JournalLineId.ShouldBeNull();
    }

    [Fact]
    public void A_candidate_is_never_claimed_by_two_statement_lines()
    {
        var l1 = Id();
        var l2 = Id();
        var candId = Id();
        var lines = new[]
        {
            new MatchInput(l1, Feb1, 100.00m),
            new MatchInput(l2, Feb1.AddDays(1), 100.00m),
        };
        var candidates = new[] { new RegisterCandidate(candId, Feb1, 100.00m, "Single candidate") };

        var results = AutoMatcher.Match(lines, candidates);

        results.Count(r => r.Kind == MatchKind.Matched).ShouldBe(1);
        results.Single(r => r.StatementLineId == l1).JournalLineId.ShouldBe(candId);
        results.Single(r => r.StatementLineId == l2).Kind.ShouldBe(MatchKind.Unmatched);
    }
}
