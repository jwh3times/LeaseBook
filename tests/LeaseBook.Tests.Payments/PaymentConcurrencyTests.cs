using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using static LeaseBook.Tests.Payments.Support.PaymentsHarness;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Two pieces of payment work on one payment at the same moment: two requests, a request and the
/// worker, or a notice and the worker. Each test holds the first open at the end of its work, lets
/// the second run until it finishes or waits on a lock, and only then lets the first commit (see
/// <see cref="PaymentsHarness.RaceAsync{T1, T2}"/>), so the outcome asserted is the one the locks
/// make whatever the timing.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentConcurrencyTests(PostgresFixture fixture)
{
    private static async Task<bool> Done(Task work)
    {
        await work;
        return true;
    }

    private static Task<PaymentOperation> SubmitAsync(PaymentsHarness h, CancellationToken ct) =>
        h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, UuidV7.NewId(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);

    private static async Task<Guid> ClaimAsync(PaymentsHarness h, Guid id, CancellationToken ct) =>
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, id, ct), ct))!.LeaseClaimId!.Value;

    [Fact]
    public async Task Two_submissions_of_one_request_at_once_make_one_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var key = UuidV7.NewId();
        Task<PaymentOperation> Submit(Lane lane) =>
            lane.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, key, 100m, "USD", PaymentMethods.Ach, 0m, ct);

        var race = await h.RaceAsync(Submit, Submit, ct);

        race.SecondError.ShouldBeNull();
        race.Second!.Id.ShouldBe(race.First.Id);
        (await h.InOrg(() => h.Scope.Db.Set<PaymentOperation>().CountAsync(ct), ct)).ShouldBe(1);
    }

    [Fact]
    public async Task A_notice_reused_with_other_content_while_the_first_is_stored_is_kept_as_a_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var progress = h.Observation(op, "Processing");

        var race = await h.RaceAsync(
            lane => Done(lane.Payments.ReceiveAsync(h.Binding, progress, ct)),
            lane => Done(lane.Payments.ReceiveAsync(h.Binding, progress with { Kind = "Succeeded" }, ct)), ct);

        race.SecondError.ShouldBeNull();
        (await h.ReadObservationsAsync(ct)).Select(x => x.Kind).ShouldBe(["Processing", PaymentEngine.ConflictKind], ignoreOrder: true);
    }

    [Fact]
    public async Task A_fact_stored_while_the_acceptance_naming_its_reference_commits_wakes_the_payment()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        var lease = await ClaimAsync(h, op.Id, ct);

        var race = await h.RaceAsync(
            lane => Done(lane.Payments.CompleteAsync(h.Binding, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct)),
            lane => Done(lane.Payments.ReceiveAsync(h.Binding, h.Observation(op, "Failed"), ct)), ct);

        race.SecondError.ShouldBeNull();
        // Woken by the fact, not left at rest with it unjudged.
        (await h.JudgeAsync(op.Id, ct)).ShouldBeTrue();
        var failed = await h.ReadAsync(op.Id, ct);
        (failed.Status, failed.Reason).ShouldBe(("Failed", "collection_failed"));
    }

    [Fact]
    public async Task An_acceptance_that_commits_while_a_fact_for_its_reference_is_stored_judges_that_fact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        var lease = await ClaimAsync(h, op.Id, ct);

        var race = await h.RaceAsync(
            lane => Done(lane.Payments.ReceiveAsync(h.Binding, h.Observation(op, "Failed"), ct)),
            lane => Done(lane.Payments.CompleteAsync(h.Binding, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct)), ct);

        race.SecondError.ShouldBeNull();
        var judged = await h.ReadAsync(op.Id, ct);
        (judged.Status, judged.Reason, judged.ProcessedCount).ShouldBe(("Failed", "collection_failed", 1));
    }

    [Fact]
    public async Task Two_workers_claiming_one_payment_at_once_get_one_lease()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);

        var race = await h.RaceAsync(
            lane => lane.Payments.ClaimAsync(h.Binding, op.Id, ct),
            lane => lane.Payments.ClaimAsync(h.Binding, op.Id, ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second.ShouldBeNull();
        (await h.ReadAsync(op.Id, ct)).LeaseClaimId.ShouldBe(race.First!.LeaseClaimId);
    }

    [Fact]
    public async Task A_review_closed_while_a_late_fact_is_judged_is_reopened_by_that_fact()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "Dispute"), ct);
        await h.ObserveAsync(h.Observation(op, "Processing", "late-progress"), ct);
        var lease = await ClaimAsync(h, op.Id, ct);

        var race = await h.RaceAsync(
            lane => lane.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "Settled by hand", ct),
            lane => Done(lane.Payments.CompleteAsync(h.Binding, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct)), ct);

        race.SecondError.ShouldBeNull();
        var reopened = await h.ReadAsync(op.Id, ct);
        (reopened.Status, reopened.Reason, reopened.ReviewNote, reopened.ProcessedCount)
            .ShouldBe(("NeedsReview", "return_requires_review", "Settled by hand", 2));
    }

    [Fact]
    public async Task A_failure_reported_while_the_attempt_completes_finds_the_lease_given_up_and_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        var lease = await ClaimAsync(h, op.Id, ct);

        var race = await h.RaceAsync(
            lane => Done(lane.Payments.CompleteAsync(h.Binding, op.Id, lease, new ProcessorResult("Accepted", ProviderOf(op)), ct)),
            lane => Done(lane.Payments.FailAsync(h.Binding, op.Id, lease, "technical_failure", ct)), ct);

        race.SecondError.ShouldBeNull();
        var completed = await h.ReadAsync(op.Id, ct);
        (completed.Status, completed.Reason, completed.Attempts, completed.ProviderId)
            .ShouldBe(("Processing", (string?)null, 0, ProviderOf(op)));
    }

    [Fact]
    public async Task Two_retries_of_one_technical_failure_at_once_retry_it_once()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await SubmitAsync(h, ct);
        await h.InOrg(async () =>
        {
            var row = await h.Scope.Db.Set<PaymentOperation>().SingleAsync(x => x.Id == op.Id, ct);
            row.Status = "NeedsReview"; row.Reason = "technical_failure"; row.Attempts = PaymentEngine.RetrySeconds.Length + 1;
            row.DueAt = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);
            await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);

        var race = await h.RaceAsync(
            lane => lane.Payments.RetryAsync(h.Binding, op.Id, ct),
            lane => lane.Payments.RetryAsync(h.Binding, op.Id, ct), ct);

        race.First.ShouldBeTrue();
        // The second finds it processing again, which is not a failure to retry.
        race.SecondError.ShouldBeOfType<PaymentConflictException>();
    }

    [Fact]
    public async Task Two_people_closing_one_review_at_once_close_it_once_with_the_first_note()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "Dispute"), ct);

        var race = await h.RaceAsync(
            lane => lane.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "First", ct),
            lane => lane.Payments.CloseReviewAsync(h.Binding, op.Id, UuidV7.NewId(), "Second", ct), ct);

        race.SecondError.ShouldBeNull();
        race.Second!.ReviewNote.ShouldBe("First");
        var closed = await h.ReadAsync(op.Id, ct);
        (closed.ReviewNote, closed.ReviewClosedBy).ShouldBe(("First", (Guid?)h.UserId));
    }

    [Fact]
    public async Task A_review_closed_while_the_payment_is_marked_overdue_closes_the_overdue_review()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));

        var race = await h.RaceAsync(
            lane => lane.Payments.MarkOverdueAsync(h.Binding, op.Id, ct),
            lane => lane.Payments.CloseReviewAsync(h.Binding, op.Id, h.UserId, "Written off", ct), ct);

        race.First.ShouldBeTrue();
        race.SecondError.ShouldBeNull();
        var closed = await h.ReadAsync(op.Id, ct);
        (closed.Status, closed.Reason, closed.ReviewNote).ShouldBe(("ReviewClosed", "outcome_overdue", "Written off"));
    }

    [Fact]
    public async Task A_payment_is_not_marked_overdue_while_a_fact_for_it_is_being_stored()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Clock.Advance(PaymentEngine.OutcomeOverdueAfter + TimeSpan.FromHours(1));
        var credit = h.Observation(op, "BankCredit");
        await using var holder = h.Lane();
        await using var receiver = h.Lane();
        await using var sweeper = h.Lane();

        // The notice is stored up to the point between keeping the fact and waking the payment.
        var held = holder.HoldNotice(h.Binding, credit.EventId, ct);
        await held.Done;
        var receiving = receiver.RunAsync(lane => Done(lane.Payments.ReceiveAsync(h.Binding, credit, ct)), ct);
        (await h.WaitingOrDoneAsync(receiver, receiving, ct)).ShouldBeTrue();
        var marking = sweeper.RunAsync(lane => lane.Payments.MarkOverdueAsync(h.Binding, op.Id, ct), ct);
        await h.WaitingOrDoneAsync(sweeper, marking, ct);
        held.Abort();
        await held.Ended();
        await receiving;

        (await marking).ShouldBeFalse();
        (await h.ReadAsync(op.Id, ct)).Reason.ShouldBeNull();
        // The fact is judged as it would have been with no sweep at all.
        (await h.JudgeAsync(op.Id, ct)).ShouldBeTrue();
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
    }

    [Fact]
    public async Task A_notice_whose_event_id_reads_as_another_reference_shares_no_lock_with_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        const string other = "prov_other";
        await using var a = h.Lane();
        await using var b = h.Lane();

        // One transaction stores a notice about this payment, and then one about another reference.
        // Between the two, a notice whose event id spells that other reference's lock arrives for
        // this payment and waits for the first. Were the event's lock that reference's lock, the
        // two would each hold what the other waits for.
        var held = a.Hold(lane => Done(lane.Payments.ReceiveAsync(h.Binding, h.Observation(op, "Processing"), ct)), ct,
            then: lane => lane.Payments.ReceiveAsync(h.Binding, h.Observation(op, "Processing", "other-progress") with { ProviderId = other }, ct));
        await held.Done;
        var named = b.RunAsync(lane => Done(lane.Payments.ReceiveAsync(h.Binding, h.Observation(op, "Succeeded", "reference:" + other), ct)), ct);
        (await h.WaitingOrDoneAsync(b, named, ct)).ShouldBeTrue();
        held.Commit();

        (await Lanes.ErrorOf(held.Ended())).ShouldBeNull();
        (await Lanes.ErrorOf(named)).ShouldBeNull();
        (await h.ReadObservationsAsync(ct)).Count.ShouldBe(3);
    }
}
