using System.Net.Http.Json;
using LeaseBook.Modules.Accounting.Domain;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// The worker's recovery pass in a simulation host (#513). The simulator has nothing to ask again,
// so the pass comes to the aging alone, which is the engine's and applies to every processor.
public sealed partial class SimulatedPaymentTests
{
    [Fact]
    public async Task The_simulator_has_nothing_to_recover()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        var processor = h.App.Services.GetRequiredService<IPaymentProcessor>().ShouldBeOfType<SimulatedProcessor>();

        var recovered = await processor.RecoverObservationsAsync(h.Binding, h.Clock.GetUtcNow().UtcDateTime.AddDays(-29), ct);

        (recovered.Observations.Count, recovered.Listed, recovered.Unreadable).ShouldBe((0, 0, 0));
    }

    [Fact]
    public async Task A_simulated_payment_with_no_outcome_in_seven_days_goes_to_review_and_later_bank_evidence_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var client = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(client, 600m, ct);
        await h.Tick(ct);
        var runner = h.App.Services.GetRequiredService<PaymentRunner>();
        // Its age is set by moving its creation time, not the clock: a week on, nobody here is still signed in.
        Task Created(DateTime at) => h.InOrg(sp => sp.GetRequiredService<AppDbContext>().Database
            .ExecuteSqlAsync($"UPDATE payment_operations SET created_at = {at} WHERE id = {op.Id}", ct), ct);

        // Six days old, it waits.
        await Created(h.Clock.GetUtcNow().UtcDateTime.AddDays(-6));
        await runner.RecoverOnceAsync(ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("Processing");

        h.Clock.Advance(TimeSpan.FromMinutes(5));
        await Created(h.Clock.GetUtcNow().UtcDateTime.AddDays(-7).AddMinutes(-1));
        await runner.RecoverOnceAsync(ct);

        var overdue = await Staff(admin, op.Id, ct);
        (overdue.Status, overdue.Reason, overdue.CanCloseReview, overdue.CanRetry, overdue.ReceiptRecorded).ShouldBe(("NeedsReview", "outcome_overdue", true, false, false));
        // The tenant is told the same reason: nothing in it is staff knowledge.
        (await Read(client, op.Id, ct)).Reason.ShouldBe("outcome_overdue");

        // Bank evidence that would have settled it while it waited is kept and posts nothing: a
        // person may already have put the books right by hand.
        await h.Emit(op, "BankCredit", ct);
        var after = await Staff(admin, op.Id, ct);
        (after.Status, after.Reason, after.ReceiptRecorded).ShouldBe(("NeedsReview", "outcome_overdue", false));
        (await h.Balance(1, ct)).ShouldBe(1000m);
        await h.InOrg(async sp => (await sp.GetRequiredService<AppDbContext>().Set<PaymentEffect>().CountAsync(ct)).ShouldBe(0), ct);
    }

    // A payment a person holds, by whatever reason, stays theirs when the first try at judging a late
    // fact fails for a passing cause. Before the fix that try made it an ordinary payment in
    // processing, and the retry posted the receipt.
    [Theory]
    [InlineData("outcome_overdue", false)]
    [InlineData("outcome_overdue", true)]
    [InlineData("conflicting_evidence", false)]
    [InlineData("conflicting_evidence", true)]
    public async Task Bank_evidence_whose_first_judging_fails_posts_nothing_for_a_payment_a_person_holds(string reason, bool closedFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        FaultingProcessor? processor = null;
        await using var h = await Setup(ct, processor: inner => processor = new FaultingProcessor(inner));
        using var client = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Submit(client, 600m, ct);
        await h.Tick(ct);
        var facts = 1;
        if (reason == "outcome_overdue")
        {
            await h.InOrg(sp => sp.GetRequiredService<AppDbContext>().Database
                .ExecuteSqlAsync($"UPDATE payment_operations SET created_at = {h.Clock.GetUtcNow().UtcDateTime.AddDays(-8)} WHERE id = {op.Id}", ct), ct);
            await h.App.Services.GetRequiredService<PaymentRunner>().RecoverOnceAsync(ct);
        }
        else
        {
            // A reason that is held on the payment alone: the processor disagreed once, and no fact says so.
            processor!.Next = new PaymentConflictException();
            await h.Emit(op, "Processing", ct);
            facts = 2;
        }
        if (closedFirst)
        { (await admin.PostAsJsonAsync($"/api/payments/{op.Id}/close-review", new ClosePaymentReviewBody("Corrected by hand."), ct)).EnsureSuccessStatusCode(); }
        var held = closedFirst ? "ReviewClosed" : "NeedsReview";
        var before = await Staff(admin, op.Id, ct);
        (before.Status, before.Reason).ShouldBe((held, reason));

        // Complete bank evidence, which settles a payment in processing. The lookup fails once.
        processor!.Next = new IOException("Injected failure reaching the processor");
        await h.Emit(op, "BankCredit", ct);

        var failed = await Staff(admin, op.Id, ct);
        (failed.Status, failed.Reason, failed.CanRetry, failed.ReceiptRecorded).ShouldBe((held, reason, false, false));
        processor.Next.ShouldBeNull();

        // Past the longest wait either case can be on: the second step of the schedule, where an earlier failure has used the first.
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[1]));
        await h.Tick(ct);

        // Judged on the retry, and still with a person: reopened if it had been closed. Nothing posted.
        var after = await Staff(admin, op.Id, ct);
        (after.Status, after.Reason, after.CanRetry, after.CanCloseReview, after.ReceiptRecorded).ShouldBe(("NeedsReview", reason, false, true, false));
        (await h.Balance(1, ct)).ShouldBe(1000m);
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            (await db.Set<PaymentEffect>().CountAsync(ct)).ShouldBe(0);
            (await db.Set<JournalEntry>().CountAsync(x => x.SourceRef == $"sim-payment:{op.Id:N}:receipt", ct)).ShouldBe(0);
            var operation = await db.Set<PaymentOperation>().AsNoTracking().SingleAsync(x => x.Id == op.Id, ct);
            (operation.ProcessedCount, operation.Attempts, operation.DueAt).ShouldBe((facts, 0, DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc)));
        }, ct);
    }

    // The simulator, with a failure a test hands it to throw at the next lookup, once.
    private sealed class FaultingProcessor(IPaymentProcessor inner) : IPaymentProcessor
    {
        private Exception? _next;
        public Exception? Next { get => Volatile.Read(ref _next); set => Volatile.Write(ref _next, value); }
        public Task<ProcessorResult> SubmitAsync(ProcessorRequest request, CancellationToken ct) => inner.SubmitAsync(request, ct);
        public Task<ProcessorResult> LookupAsync(ProcessorRequest request, CancellationToken ct) =>
            Interlocked.Exchange(ref _next, null) is { } failure ? Task.FromException<ProcessorResult>(failure) : inner.LookupAsync(request, ct);
        public ProcessorNotice? Authenticate(byte[] rawBody, string signature) => inner.Authenticate(rawBody, signature);
        public Task<ProcessorRead<ProcessorObservation>> ReadObservationAsync(ProcessorNotice notice, CancellationToken ct) => inner.ReadObservationAsync(notice, ct);
        public Task<ProcessorRead<ProcessorSettlement>> ReadSettlementAsync(ProcessorNotice notice, CancellationToken ct) => inner.ReadSettlementAsync(notice, ct);
        public Task<ProcessorRecovery> RecoverObservationsAsync(FixtureBinding binding, DateTime since, CancellationToken ct) => inner.RecoverObservationsAsync(binding, since, ct);
    }
}
