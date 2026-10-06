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

    // ---- groups: several register lines the bank shows as one statement line (ADR-053) ----

    private static RegisterCandidate[] Payout(string reference, DateOnly date, params decimal[] amounts) =>
        [.. amounts.Select(a => new RegisterCandidate(Id(), date, a, "Payout line", reference))];

    [Fact]
    public void A_statement_line_matches_a_whole_group_at_its_signed_sum()
    {
        var lineId = Id();
        // The worked payout: three receipts, a shortfall out and a surplus in.
        var group = Payout("po_1", Feb1, 1000.00m, 500.00m, 250.00m, -7.50m, 0.40m);

        var r = AutoMatcher.Match([new MatchInput(lineId, Feb1.AddDays(1), 1742.90m)], group).ShouldHaveSingleItem();

        (r.Kind, r.GroupRef, r.JournalLineId).ShouldBe((MatchKind.Matched, "po_1", null));
        AutoMatcher.Groups(group)["po_1"].Select(c => c.JournalLineId).ShouldBe(group.Select(c => c.JournalLineId), ignoreOrder: true);
    }

    [Fact]
    public void A_line_that_belongs_to_a_group_is_never_matched_by_itself()
    {
        var group = Payout("po_1", Feb1, 1000.00m, 500.00m);
        var alone = new RegisterCandidate(Id(), Feb1.AddDays(20), 500.00m, "A lone deposit");

        // 1,000.00 is one of the group's lines and nothing else: unmatched, not a part of the payout.
        // 500.00 is also one of them, but a lone line far outside the window has it: suggested, to that line.
        var results = AutoMatcher.Match(
            [new MatchInput(Id(), Feb1, 1000.00m), new MatchInput(Id(), Feb1, 500.00m)], [.. group, alone]);

        (results[0].Kind, results[0].JournalLineId, results[0].GroupRef).ShouldBe((MatchKind.Unmatched, null, null));
        (results[1].Kind, results[1].JournalLineId, results[1].GroupRef).ShouldBe((MatchKind.Suggested, alone.JournalLineId, null));
    }

    [Fact]
    public void A_group_is_suggested_outside_the_window_and_claimed_by_one_statement_line_only()
    {
        var first = Id();
        var second = Id();
        var group = Payout("po_1", Feb1.AddDays(10), 600.00m, -200.00m);

        var results = AutoMatcher.Match(
            [new MatchInput(first, Feb1, 400.00m), new MatchInput(second, Feb1.AddDays(1), 400.00m)], group);

        results.Count(r => r.GroupRef == "po_1").ShouldBe(1);
        results.Single(r => r.GroupRef == "po_1").Kind.ShouldBe(MatchKind.Suggested);
        results.Single(r => r.GroupRef is null).Kind.ShouldBe(MatchKind.Unmatched);
    }

    [Fact]
    public void Two_groups_of_the_same_amount_go_to_the_nearer_statement_dates_and_never_mix_their_lines()
    {
        var early = Id();
        var late = Id();
        var candidates = Payout("po_1", Feb1, 300.00m, 100.00m).Concat(Payout("po_2", Feb1.AddDays(7), 150.00m, 250.00m)).ToArray();

        var results = AutoMatcher.Match(
            [new MatchInput(late, Feb1.AddDays(7), 400.00m), new MatchInput(early, Feb1, 400.00m)], candidates);

        results.Single(r => r.StatementLineId == early).GroupRef.ShouldBe("po_1");
        results.Single(r => r.StatementLineId == late).GroupRef.ShouldBe("po_2");
        // 450.00 is 300.00 of one payout and 150.00 of the other: no such statement line can match.
        AutoMatcher.Match([new MatchInput(Id(), Feb1, 450.00m)], candidates)[0].Kind.ShouldBe(MatchKind.Unmatched);
    }

    [Fact]
    public void A_group_that_nets_to_nothing_answers_no_statement_line()
    {
        var group = Payout("po_1", Feb1, 600.00m, -600.00m);

        AutoMatcher.Match([new MatchInput(Id(), Feb1, 0.00m)], group)[0].Kind.ShouldBe(MatchKind.Unmatched);
    }
}
