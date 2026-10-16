using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// What becomes of a payment when an attempt fails, when a person retries or closes its review, and
/// when the processor never says how it went (ADR-046, #490).
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentRecoveryTests(PostgresFixture fixture)
{
    private static Task<PaymentOperation> SubmitAsync(PaymentsHarness h, CancellationToken ct) =>
        h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, Guid.NewGuid(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);

    private static async Task<Guid> ClaimAsync(PaymentsHarness h, Guid id, CancellationToken ct) =>
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, id, ct), ct))!.LeaseClaimId!.Value;

    private static Task FailAsync(PaymentsHarness h, Guid id, Guid token, string reason, CancellationToken ct) =>
        h.InOrg(() => h.Payments.FailAsync(h.Binding, id, token, reason, ct), ct);

    [Fact]
    public async Task A_technical_failure_is_retried_on_the_schedule_and_then_goes_to_a_person()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);

        for (var attempt = 1; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);
            var retrying = await h.ReadAsync(op.Id, ct);
            (retrying.Status, retrying.Reason, retrying.Attempts, retrying.LeaseUntil, retrying.LeaseClaimId)
                .ShouldBe(("Processing", "technical_failure", attempt, (DateTime?)null, (Guid?)null));
            var wait = TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[attempt - 1]);
            retrying.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime + wait, TimeSpan.FromMilliseconds(1));
            h.Clock.Advance(wait);
        }

        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.Attempts).ShouldBe(("NeedsReview", "technical_failure", PaymentEngine.RetrySeconds.Length + 1));
        held.DueAt.ShouldBe(DateTime.MaxValue, TimeSpan.FromSeconds(1));
        PaymentEngine.RetrySeconds.ShouldBe([1, 5, 30, 120, 600]);
    }

    [Fact]
    public async Task A_failure_that_is_not_technical_goes_to_a_person_at_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);

        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "processor_rejected", ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.Attempts).ShouldBe(("NeedsReview", "processor_rejected", 1));
        held.DueAt.ShouldBe(DateTime.MaxValue, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_failure_under_a_lease_that_is_no_longer_held_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        await ClaimAsync(h, op.Id, ct);

        await FailAsync(h, op.Id, Guid.NewGuid(), "technical_failure", ct);

        var unchanged = await h.ReadAsync(op.Id, ct);
        (unchanged.Status, unchanged.Reason, unchanged.Attempts).ShouldBe(("Requested", (string?)null, 0));
    }

    [Fact]
    public async Task A_payment_a_person_holds_keeps_its_reason_whatever_fails_while_a_late_fact_is_judged()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "Dispute"), ct);
        await h.ObserveAsync(h.Observation(op, "BankCredit"), ct);

        for (var attempt = 1; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[Math.Max(0, attempt - 2)]));
            await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);
            var still = await h.ReadAsync(op.Id, ct);
            (still.Status, still.Reason, still.Attempts).ShouldBe(("NeedsReview", "return_requires_review", attempt));
        }

        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[^1]));
        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);

        // Out of tries it rests with the fact unjudged, and the count starts again for the next fact.
        var rested = await h.ReadAsync(op.Id, ct);
        (rested.Status, rested.Reason, rested.Attempts).ShouldBe(("NeedsReview", "return_requires_review", 0));
        rested.DueAt.ShouldBe(DateTime.MaxValue, TimeSpan.FromSeconds(1));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payment_with_a_receipt_keeps_its_status_when_an_attempt_fails()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);
        await h.ObserveAsync(h.Observation(op, "PayoutPaid"), ct);

        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);

        var kept = await h.ReadAsync(op.Id, ct);
        (kept.Status, kept.Reason, kept.Attempts, kept.LeaseClaimId).ShouldBe(("Settled", (string?)null, 1, (Guid?)null));
        kept.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(1), TimeSpan.FromMilliseconds(1));
    }

    public static TheoryData<string, string, string, int> LateFailures() => new()
    {
        // Ended by its own facts, not held by a person: tried again like any other.
        { "Failed", "Processing", "technical_failure", 1 },
        // Closed by a person under a reason of its own: kept there.
        { "ReviewClosed", "ReviewClosed", "return_requires_review", 1 },
        // A technical failure out of tries is no reason of a person's: counted on, and still out of tries.
        { "out of tries", "NeedsReview", "technical_failure", PaymentEngine.RetrySeconds.Length + 2 },
    };

    [Theory]
    [MemberData(nameof(LateFailures))]
    public async Task An_attempt_that_fails_while_a_late_fact_is_judged_holds_only_what_a_person_holds(
        string state, string status, string reason, int attempts)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        switch (state)
        {
            case "Failed": await h.EmitAsync(h.Observation(op, "Failed"), ct); break;
            case "ReviewClosed":
                await h.EmitAsync(h.Observation(op, "Dispute"), ct);
                await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "Closed", ct), ct);
                break;
            default:
                await h.InOrg(async () =>
                {
                    var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
                    row.Status = "NeedsReview"; row.Reason = "technical_failure"; row.Attempts = PaymentEngine.RetrySeconds.Length + 1;
                    await h.Scope.Db.SaveChangesAsync(ct);
                }, ct);
                break;
        }
        await h.ObserveAsync(h.Observation(op, "Processing", "late-progress"), ct);

        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "technical_failure", ct);

        var after = await h.ReadAsync(op.Id, ct);
        (after.Status, after.Reason, after.Attempts).ShouldBe((status, reason, attempts));
    }

    [Fact]
    public async Task A_person_retries_a_technical_failure_and_only_that()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        await FailAsync(h, op.Id, await ClaimAsync(h, op.Id, ct), "processor_rejected", ct);

        await Should.ThrowAsync<PaymentConflictException>(() => h.InOrg(() => h.Payments.RetryAsync(h.Binding, op.Id, ct), ct));

        var technical = await SubmitAsync(h, ct);
        for (var attempt = 0; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            await FailAsync(h, technical.Id, await ClaimAsync(h, technical.Id, ct), "technical_failure", ct);
        }
        (await h.ReadAsync(technical.Id, ct)).Status.ShouldBe("NeedsReview");

        (await h.InOrg(() => h.Payments.RetryAsync(h.Binding, technical.Id, ct), ct)).ShouldBeTrue();

        var retried = await h.ReadAsync(technical.Id, ct);
        (retried.Status, retried.Reason, retried.Attempts).ShouldBe(("Processing", (string?)null, 0));
        retried.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        (await h.InOrg(() => h.Payments.RetryAsync(h.Binding, Guid.NewGuid(), ct), ct)).ShouldBeFalse();
        // Processing is not a failure to retry.
        await Should.ThrowAsync<PaymentConflictException>(() => h.InOrg(() => h.Payments.RetryAsync(h.Binding, technical.Id, ct), ct));
    }

    [Fact]
    public async Task Retrying_a_payment_with_a_receipt_is_already_done()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        (await h.InOrg(() => h.Payments.RetryAsync(h.Binding, op.Id, ct), ct)).ShouldBeTrue();

        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
    }

    [Fact]
    public async Task A_person_closes_a_review_with_a_note_and_a_later_fact_reopens_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "Dispute"), ct);

        var closed = await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "Settled by hand", ct), ct);

        (closed!.Status, closed.Reason, closed.ReviewNote, closed.ReviewClosedBy).ShouldBe(("ReviewClosed", "return_requires_review", "Settled by hand", (Guid?)h.UserId));
        closed.ReviewClosedAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime);
        // Closing again is the same close.
        (await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, op.Id, Guid.NewGuid(), "Again", ct), ct))!.ReviewNote.ShouldBe("Settled by hand");
        (await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, Guid.NewGuid(), h.UserId, "None", ct), ct)).ShouldBeNull();

        // A complete bank credit afterwards does not post: the review is reopened with its reason.
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);
        var reopened = await h.ReadAsync(op.Id, ct);
        (reopened.Status, reopened.Reason, reopened.ReviewNote).ShouldBe(("NeedsReview", "return_requires_review", "Settled by hand"));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_closed_review_of_an_overdue_payment_does_not_post_the_credit_that_arrives_later()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromMinutes(1));
        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeTrue();
        await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "Written off", ct), ct);

        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        var reopened = await h.ReadAsync(op.Id, ct);
        (reopened.Status, reopened.Reason).ShouldBe(("NeedsReview", "outcome_overdue"));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Only_a_review_is_closed_and_a_technical_failure_is_retried_instead()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var processing = await h.AcceptedAsync(100m, ct);
        var technical = await SubmitAsync(h, ct);
        await FailAsync(h, technical.Id, await ClaimAsync(h, technical.Id, ct), "technical_failure", ct);
        await h.InOrg(async () =>
        {
            var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == technical.Id, ct);
            row.Status = "NeedsReview";
            await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);

        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, processing.Id, h.UserId, "No", ct), ct));
        await Should.ThrowAsync<PaymentConflictException>(() =>
            h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, technical.Id, h.UserId, "No", ct), ct));
    }

    [Fact]
    public async Task A_payment_with_no_outcome_for_seven_days_goes_to_a_person_and_nothing_posts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);

        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter - TimeSpan.FromMinutes(1));
        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeFalse();
        h.Clock.Advance(TimeSpan.FromMinutes(2));
        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeTrue();

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "outcome_overdue", (Guid?)null));
        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeFalse();
        PaymentEngine.OutcomeOverdueAfter.ShouldBe(TimeSpan.FromDays(7));

        // A failure that arrives afterwards still ends it.
        await h.EmitAsync(h.Observation(op, "Failed"), ct);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Failed");
    }

    [Fact]
    public async Task A_payment_is_overdue_from_exactly_seven_days_after_it_was_made()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var made = (await h.ReadAsync(op.Id, ct)).CreatedAt;

        h.Clock.Advance(made + PaymentEngine.OutcomeOverdueAfter - h.Clock.GetUtcNow().UtcDateTime);

        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeTrue();
    }

    [Fact]
    public async Task A_lease_that_ends_at_this_moment_no_longer_holds_a_payment_back_from_aging()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));
        await h.InOrg(async () =>
        {
            var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
            row.LeaseUntil = h.Clock.GetUtcNow().UtcDateTime;
            await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);

        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct)).ShouldBeTrue();
    }

    public static TheoryData<string> NotAtRest() =>
        ["unjudged fact", "leased", "due", "due this moment", "reason", "paid", "failed", "no reference", "unknown"];

    [Theory]
    [MemberData(nameof(NotAtRest))]
    public async Task A_payment_that_may_still_end_by_itself_is_not_marked_overdue(string state)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = state == "no reference" ? await SubmitAsync(h, ct) : await h.AcceptedAsync(100m, ct);
        var id = state == "unknown" ? Guid.NewGuid() : op.Id;
        switch (state)
        {
            case "paid": await h.EmitAsync(h.Observation(op, "Succeeded"), ct); break;
            case "failed": await h.EmitAsync(h.Observation(op, "Failed"), ct); break;
            case "unjudged fact" or "leased" or "due" or "due this moment" or "reason":
                if (state == "unjudged fact") { await h.ObserveAsync(h.Observation(op, "Processing"), ct); }
                await h.InOrg(async () =>
                {
                    var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
                    var later = h.Clock.GetUtcNow().UtcDateTime + PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(2);
                    if (state == "leased") { row.LeaseUntil = later; }
                    if (state == "due") { row.DueAt = h.Clock.GetUtcNow().UtcDateTime; }
                    // Due at exactly the moment it is looked at, after the clock moves below.
                    if (state == "due this moment") { row.DueAt = later - TimeSpan.FromHours(1); }
                    if (state == "reason") { row.Reason = "technical_failure"; }
                    // Stored and not yet judged, with nothing else in the way: only the count says so.
                    if (state == "unjudged fact") { row.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc); }
                    await h.Scope.Db.SaveChangesAsync(ct);
                }, ct);
                break;
        }
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));

        (await h.InOrg(() => h.Payments.MarkOverdueAsync(h.Binding, id, ct), ct)).ShouldBeFalse();

        if (state != "unknown") { (await h.ReadAsync(op.Id, ct)).Reason.ShouldNotBe("outcome_overdue"); }
    }

    [Fact]
    public async Task The_waiting_payments_are_the_accepted_unpaid_ones_of_this_binding_oldest_first()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var first = await h.AcceptedAsync(100m, ct);
        var paid = await h.AcceptedAsync(101m, ct);
        var settled = await h.AcceptedAsync(102m, ct);
        var requested = await SubmitAsync(h, ct);
        var second = await h.AcceptedAsync(103m, ct);
        await h.EmitAsync(h.Observation(paid, "Succeeded"), ct);
        await h.EmitAsync(h.Observation(settled, "BankCredit"), ct);

        var waiting = await h.InOrg(() => h.Payments.WaitingAsync(h.Binding, ct), ct);

        waiting.Select(x => x.Id).ShouldBe([first.Id, second.Id]);
        waiting[0].CreatedAt.ShouldBeLessThanOrEqualTo(waiting[1].CreatedAt);
        waiting.ShouldNotContain(x => x.Id == requested.Id);
    }
}
