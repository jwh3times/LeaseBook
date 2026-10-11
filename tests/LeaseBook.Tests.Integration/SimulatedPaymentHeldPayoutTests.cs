using System.Net.Http.Json;
using System.Text.Json;
using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Web.Payments;
using LeaseBook.Web.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace LeaseBook.Tests.Integration;

// What step 7 of #513 changed in the engine every processor shares, shown in a simulation host: the
// rules a payout line is judged by, the bookkeeping of a failed attempt, and the payout's mode. And
// that bank evidence apart from a payout is a Stripe sandbox path: a simulated payout brings its own.
public sealed partial class SimulatedPaymentTests
{
    // A dispute or a refund raises the same review reason a returned debit does. Only the bank's
    // return of the debit is posted as a return: before the fix all three reached an administrator
    // as a payout to confirm, and confirming it posted the reversal.
    [Theory]
    [InlineData("Return", "settlement_requires_confirmation")]
    [InlineData("Dispute", "conflicting_evidence")]
    [InlineData("Refund", "conflicting_evidence")]
    public async Task A_payouts_return_line_is_taken_only_for_a_review_that_a_return_raised(string raisedBy, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        await h.Emit(op, raisedBy, ct);
        var review = await Staff(admin, op.Id, ct);
        (review.Status, review.Reason, review.ReceiptRecorded).ShouldBe(("NeedsReview", "return_requires_review", true));
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct));

        await h.Settle(h.Payout("po_return", -600m, h.Line("1", "Return", op, 600m, 0m, -600m)), ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason, payout.CanPost).ShouldBe(("NeedsReview", reason, raisedBy == "Return"));
        (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe(before);
        var posted = await admin.PostAsync($"{Settlements}/{payout.Id}/post", null, ct);
        if (raisedBy == "Return")
        {
            // A returned debit is what an administrator may post: the receipt's reversal, and nothing else.
            posted.StatusCode.ShouldBe(System.Net.HttpStatusCode.OK);
            (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe((before.Item1 - 600m, 1000m, before.Item3 + 1));
            (await Staff(admin, op.Id, ct)).Status.ShouldBe("Returned");
            return;
        }
        // A dispute or a refund is not: the payout can only be closed, and the ledger is as it was.
        posted.StatusCode.ShouldBe(System.Net.HttpStatusCode.Conflict);
        (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe(before);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("NeedsReview");
    }

    // A fact is stored in one transaction and judged in another. Between the two the payment's
    // status still says it is waiting. A payout that names it then must not receipt it: before the
    // fix it did, and the failure or dispute was judged against a payment already on the ledger.
    [Theory]
    [InlineData("Failed")]
    [InlineData("Return")]
    [InlineData("Dispute")]
    [InlineData("Refund")]
    public async Task A_payout_does_not_receipt_a_payment_of_which_a_failure_or_a_dispute_is_stored_and_not_yet_judged(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        // Stored, and the worker has not run since.
        await h.Deliver(h.Observation(op, kind), ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("Processing");
        var before = (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct));

        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);

        var payout = (await List(admin, ct)).Single();
        (payout.Status, payout.Reason).ShouldBe(("NeedsReview", "conflicting_evidence"));
        (await BankBook(h, ct), await h.Balance(1, ct), await JournalEntries(h, ct)).ShouldBe(before);
        // Judged now, the payment is what the fact says, with no receipt behind it.
        await h.Tick(ct);
        var judged = await Staff(admin, op.Id, ct);
        (judged.Status, judged.ReceiptRecorded).ShouldBe((kind == "Failed" ? "Failed" : "NeedsReview", false));

        // A payment nothing has been said against is receipted by the same payout line: the fact was what held it.
        using var two = await h.Login(2, ct);
        var clean = await Pay(h, two, 400m, ct);
        await h.Deliver(h.Observation(clean, "Succeeded"), ct);
        await h.Settle(h.Payout("po_2", 400m, h.Line("1", "Payment", clean, 400m, 0m, 400m)), ct);
        (await List(admin, ct)).Single(x => x.PayoutReference == "po_2").Status.ShouldBe("Posted");
    }

    // A payment with a receipt is woken by a later fact, and the attempt to judge it fails. Before
    // the fix the failure was not recorded at all: the lease and the due time stayed as the claim
    // left them, so the worker took the payment again each time the lease ran out, for ever.
    [Fact]
    public async Task A_settled_payment_whose_late_fact_cannot_be_judged_gives_up_its_lease_and_backs_off()
    {
        var ct = TestContext.Current.CancellationToken;
        FaultingProcessor? processor = null;
        await using var h = await Setup(ct, processor: inner => processor = new FaultingProcessor(inner));
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        await h.Emit(op, "BankCredit", ct);
        (await Staff(admin, op.Id, ct)).Status.ShouldBe("Settled");
        async Task<PaymentOperation> Operation()
        {
            PaymentOperation? operation = null;
            await h.InOrg(async sp => operation = await sp.GetRequiredService<AppDbContext>().Set<PaymentOperation>().AsNoTracking().SingleAsync(x => x.Id == op.Id, ct), ct);
            return operation!;
        }
        var never = DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc);

        for (var attempt = 1; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            processor!.Next = new IOException("Injected failure reaching the processor");
            if (attempt == 1) { await h.Deliver(h.Observation(op, "Processing"), ct); }
            else { h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[attempt - 2])); }
            await h.Tick(ct);

            processor.Next.ShouldBeNull();
            var failed = await Operation();
            // Still settled, with its receipt. The claim is given up and the next attempt is scheduled.
            (failed.Status, failed.Reason, failed.JournalId.HasValue, failed.LeaseUntil, failed.LeaseClaimId, failed.Attempts)
                .ShouldBe(("Settled", null, true, null, null, attempt));
            // To the microsecond the database keeps.
            failed.DueAt.ShouldBe(h.Clock.GetUtcNow().UtcDateTime.AddSeconds(PaymentEngine.RetrySeconds[attempt - 1]), TimeSpan.FromMilliseconds(1));
        }

        // Out of tries, it rests until the next fact, and is not claimed again however long passes.
        processor!.Next = new IOException("Injected failure reaching the processor");
        h.Clock.Advance(TimeSpan.FromSeconds(PaymentEngine.RetrySeconds[^1]));
        await h.Tick(ct);
        var resting = await Operation();
        (resting.Status, resting.LeaseUntil, resting.Attempts, resting.DueAt, resting.ProcessedCount).ShouldBe(("Settled", null, 0, never, 1));
        processor.Next = new IOException("Never asked");
        h.Clock.Advance(TimeSpan.FromHours(1));
        await h.Tick(ct);
        processor.Next.ShouldNotBeNull();
        processor.Next = null;

        // The next fact wakes it, and with the processor answering it is judged: both facts, and still settled.
        await h.Emit(op, "Succeeded", ct);
        var judged = await Operation();
        (judged.Status, judged.Reason, judged.ProcessedCount, judged.Attempts, judged.DueAt).ShouldBe(("Settled", null, 3, 0, never));
        (await h.Balance(1, ct)).ShouldBe(400m);
    }

    // A payout is found by its mode as well as its account and reference. Before the column existed
    // the lookup was by account alone, which was safe only while one account implied one mode.
    [Fact]
    public async Task A_payout_is_found_by_its_mode_as_well_as_its_account_and_reference()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        // The same account and reference under another mode, closed long ago.
        await h.InOrg(async sp =>
        {
            var db = sp.GetRequiredService<AppDbContext>();
            db.Add(new PaymentSettlement
            {
                Id = UuidV7.NewId(),
                Generation = h.Binding.Generation,
                Account = h.Binding.Account,
                Mode = PaymentModes.StripeSandbox,
                PayoutId = "po_1",
                BankId = h.Binding.BankId,
                ProcessorAmount = 600m,
                Fingerprint = new string('0', 64),
                Status = SettlementStatuses.Closed,
            });
            await db.SaveChangesAsync(ct);
        }, ct);

        await h.Settle(h.Payout("po_1", 600m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);

        // The simulator's payout is its own, and posts. The other is neither found nor touched.
        await h.InOrg(async sp =>
        {
            var payouts = await sp.GetRequiredService<AppDbContext>().Set<PaymentSettlement>().AsNoTracking().Where(x => x.PayoutId == "po_1").ToListAsync(ct);
            payouts.Select(x => (x.Mode, x.Status, x.Reason)).ShouldBe(
                [(PaymentModes.Simulation, "Posted", null), (PaymentModes.StripeSandbox, "Closed", null)], ignoreOrder: true);
            // Stored with its mode, and with no figure of the processor's apart from the bank's.
            var simulated = payouts.Single(x => x.Mode == PaymentModes.Simulation);
            (simulated.ProcessorAmount, simulated.Banked, simulated.FailedAt).ShouldBe((null, true, null));
        }, ct);
        (await h.Balance(1, ct)).ShouldBe(400m);
        // The other mode's payout cannot be posted or closed through this host's binding.
        var other = (await List(admin, ct)).Single(x => x.Status == "Closed");
        (await admin.PostAsync($"{Settlements}/{other.Id}/post", null, ct)).IsSuccessStatusCode.ShouldBeFalse();
        (await h.Balance(1, ct)).ShouldBe(400m);
    }

    // Bank evidence apart from a payout is how a Stripe sandbox payout is released. A simulated
    // payout arrives with its bank evidence, so in a simulation host the command is refused and
    // evidence signed with this fixture's own key is not taken, whatever payout it names.
    [Fact]
    public async Task Bank_evidence_is_refused_outside_a_stripe_sandbox_host()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await Setup(ct);
        using var one = await h.Login(1, ct);
        using var admin = await h.LoginAdmin(ct);
        var op = await Pay(h, one, 600m, ct);
        // A payout held for review, which evidence could seem to answer for.
        await h.Settle(h.Payout("po_1", 599m, h.Line("1", "Payment", op, 600m, 0m, 600m)), ct);
        var before = (await List(admin, ct)).Single();
        (before.Status, before.Reason).ShouldBe(("NeedsReview", "settlement_untied"));
        var today = DateOnly.FromDateTime(h.Clock.GetUtcNow().UtcDateTime);

        (await h.Cli(ct, "bank", h.Binding.OrgId.ToString(), "po_1", "600.00", today.ToString("yyyy-MM-dd"))).ShouldBe(1);
        var signer = h.App.Services.GetRequiredService<BankEvidenceSigner>();
        var body = JsonSerializer.SerializeToUtf8Bytes(new BankEvidenceNotice(h.Binding.Account, "po_1", 600m, today));
        signer.Verify(body, signer.Sign(body)).ShouldBeNull();
        var delivery = await h.App.Services.GetRequiredService<PaymentRunner>().ReceiveBankEvidenceAsync(body, signer.Sign(body), ct);

        delivery.ShouldBe(new BankEvidenceDelivery(BankEvidenceDelivery.Refused));
        var after = (await List(admin, ct)).Single();
        (after.Status, after.Reason, after.BankAmount).ShouldBe(("NeedsReview", "settlement_untied", 599m));
        (await h.Balance(1, ct)).ShouldBe(1000m);
        // Even handed to the engine, a payout that arrived with its bank evidence is not one a bank has still to speak for.
        await h.InOrg(async sp => (await sp.GetRequiredService<SettlementEngine>().ReceiveBankEvidenceAsync(h.Binding,
            new BankEvidence("po_1", h.Binding.Account, h.Binding.Mode, h.Binding.Generation, 600m, today, "bank-x"), ct))
            .ShouldBe(new BankEvidenceResult(BankEvidenceOutcomes.Unknown)), ct);
        (await List(admin, ct)).Single().BankAmount.ShouldBe(599m);
    }
}
