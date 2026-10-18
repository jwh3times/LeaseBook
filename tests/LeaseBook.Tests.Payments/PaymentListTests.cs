using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.SharedKernel;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// The payment lists (ADR-046, #490): staff see every payment with what it waits on and the actions it
/// offers; a tenant sees only their own, with no journal reference, no closing note, and a return
/// under review named only as that. Both are newest first, and each payment carries the newest bank
/// evidence reference held for it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentListTests(PostgresFixture fixture)
{
    // One payment in each state a list tells apart, all the first tenant's but the one requested.
    private sealed record Held(PaymentOperation Requested, PaymentOperation Settled, PaymentOperation Retryable,
        PaymentOperation Rejected, PaymentOperation ReturnOffered, PaymentOperation ReturnRefusedByLedger,
        PaymentOperation ReturnPartial, PaymentOperation Returned, PaymentOperation AfterReturn, PaymentOperation Closed,
        Guid ReturnedEntry, Guid AfterReturnEntry, DateTime PaidAt);

    private static Task<PaymentOperation> SubmitAsync(PaymentsHarness h, int tenant, decimal amount, CancellationToken ct) =>
        h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[tenant], h.UserId, UuidV7.NewId(), amount, "USD",
            PaymentMethods.Ach, 0m, ct), ct);

    private static async Task<Guid> ClaimAsync(PaymentsHarness h, Guid id, CancellationToken ct) =>
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, id, ct), ct))!.LeaseClaimId!.Value;

    // Settled by a bank credit, then a bank's return of it, in review.
    private static async Task<PaymentOperation> ReturnInReviewAsync(PaymentsHarness h, CancellationToken ct, decimal returned = 100m)
    {
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);
        await h.EmitAsync(h.Observation(op, "Return") with { Gross = returned, Net = returned, BankDate = h.Today.AddDays(3) }, ct);
        return await h.ReadAsync(op.Id, ct);
    }

    private static async Task<Guid> PostReturnAsync(PaymentsHarness h, PaymentOperation op, CancellationToken ct)
    {
        await h.InOrg(() => h.Payments.PostReturnAsync(h.Binding, op.Id, ct), ct);
        return (await h.ReadEffectsAsync(ct)).Single(x => x.OperationId == op.Id && x.Kind == "Return").JournalId;
    }

    private static async Task<Held> HoldEveryKindAsync(PaymentsHarness h, CancellationToken ct)
    {
        var requested = await SubmitAsync(h, 1, 10m, ct);

        var paidAt = h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-5);
        var settled = await h.AcceptedAsync(100m, ct, method: PaymentMethods.Card, fee: 2.50m);
        await h.EmitAsync(h.Observation(settled, "Succeeded") with { ObservedAt = paidAt }, ct);
        await h.EmitAsync(h.Observation(settled, "BankCredit"), ct);
        h.FeeRules.Fee = 0m;

        // Out of tries: the sixth technical failure goes to a person, who may try it again.
        var retryable = await SubmitAsync(h, 0, 30m, ct);
        for (var attempt = 0; attempt <= PaymentEngine.RetrySeconds.Length; attempt++)
        {
            h.Clock.Advance(TimeSpan.FromMinutes(11));
            var lease = await ClaimAsync(h, retryable.Id, ct);
            await h.InOrg(() => h.Payments.FailAsync(h.Binding, retryable.Id, lease, "technical_failure", ct), ct);
        }

        var rejected = await SubmitAsync(h, 0, 40m, ct);
        var claim = await ClaimAsync(h, rejected.Id, ct);
        await h.InOrg(() => h.Payments.FailAsync(h.Binding, rejected.Id, claim, "processor_rejected", ct), ct);

        var offered = await ReturnInReviewAsync(h, ct);

        var refused = await ReturnInReviewAsync(h, ct);
        h.Ledger.ReturnRefusal = "return_owner_funds_disbursed";
        await h.InOrg(() => h.Payments.PostReturnAsync(h.Binding, refused.Id, ct), ct);
        h.Ledger.ReturnRefusal = null;

        var partial = await ReturnInReviewAsync(h, ct, returned: 40m);

        var returned = await ReturnInReviewAsync(h, ct);
        var returnedEntry = await PostReturnAsync(h, returned, ct);

        var afterReturn = await ReturnInReviewAsync(h, ct);
        var afterReturnEntry = await PostReturnAsync(h, afterReturn, ct);
        await h.EmitAsync(h.Observation(afterReturn, "Refund"), ct);

        var closed = await ReturnInReviewAsync(h, ct);
        await h.InOrg(() => h.Payments.CloseReviewAsync(h.Binding, closed.Id, h.UserId, "Owner repaid the tenant directly.", ct), ct);

        return new(await h.ReadAsync(requested.Id, ct), await h.ReadAsync(settled.Id, ct), await h.ReadAsync(retryable.Id, ct),
            await h.ReadAsync(rejected.Id, ct), await h.ReadAsync(offered.Id, ct), await h.ReadAsync(refused.Id, ct),
            await h.ReadAsync(partial.Id, ct), await h.ReadAsync(returned.Id, ct), await h.ReadAsync(afterReturn.Id, ct),
            await h.ReadAsync(closed.Id, ct), returnedEntry, afterReturnEntry, paidAt);
    }

    private static (string Status, string? Reason, bool Receipt, bool Retry, bool Close, bool PostReturn, Guid? ReceiptEntry,
        Guid? ReturnEntry) Shown(PaymentView view) =>
        (view.Status, view.Reason, view.ReceiptRecorded, view.CanRetry, view.CanCloseReview, view.CanPostReturn,
            view.ReceiptEntryId, view.ReturnEntryId);

    [Fact]
    public async Task Staff_see_what_each_payment_waits_on_and_only_the_actions_that_can_succeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var held = await HoldEveryKindAsync(h, ct);

        var list = (await h.QueryAsync(new GetPayments(null), ct)).ToDictionary(x => x.Id);

        list.Count.ShouldBe(10);
        Shown(list[held.Requested.Id]).ShouldBe(("Requested", null, false, false, false, false, null, null));
        Shown(list[held.Settled.Id]).ShouldBe(("Settled", null, true, false, false, false, held.Settled.JournalId, null));
        // A technical failure is retried, not closed: closed, it would still post the next time evidence arrived.
        Shown(list[held.Retryable.Id]).ShouldBe(("NeedsReview", "technical_failure", false, true, false, false, null, null));
        Shown(list[held.Rejected.Id]).ShouldBe(("NeedsReview", "processor_rejected", false, false, true, false, null, null));
        Shown(list[held.ReturnOffered.Id]).ShouldBe(
            ("NeedsReview", "return_requires_review", true, false, true, true, held.ReturnOffered.JournalId, null));
        // The ledger's refusal can be mended, so the return is still offered, under the ledger's reason.
        Shown(list[held.ReturnRefusedByLedger.Id]).ShouldBe(
            ("NeedsReview", "return_owner_funds_disbursed", true, false, true, true, held.ReturnRefusedByLedger.JournalId, null));
        // Part of the amount returned is refused however often it is asked, so it is not offered.
        Shown(list[held.ReturnPartial.Id]).ShouldBe(
            ("NeedsReview", "return_requires_review", true, false, true, false, held.ReturnPartial.JournalId, null));
        Shown(list[held.Returned.Id]).ShouldBe(("Returned", null, true, false, false, false, held.Returned.JournalId, held.ReturnedEntry));
        Shown(list[held.AfterReturn.Id]).ShouldBe(
            ("NeedsReview", "evidence_after_return", true, false, true, false, held.AfterReturn.JournalId, held.AfterReturnEntry));
        Shown(list[held.Closed.Id]).ShouldBe(
            ("ReviewClosed", "return_requires_review", true, false, false, false, held.Closed.JournalId, null));

        var closed = list[held.Closed.Id];
        (closed.ReviewNote, closed.ReviewClosedAt).ShouldBe(("Owner repaid the tenant directly.", held.Closed.ReviewClosedAt));
        var settled = list[held.Settled.Id];
        (settled.Amount, settled.Currency, settled.Method, settled.QuotedFee, settled.ChargedAmount, settled.PaidAt)
            .ShouldBe((100m, "USD", PaymentMethods.Card, 2.50m, 102.50m, (DateTime?)held.PaidAt));
        (settled.CreatedAt, settled.LastAttemptAt).ShouldBe((held.Settled.CreatedAt, held.Settled.LastAttemptAt));
        settled.EvidenceReference.ShouldBe("evidence-" + held.Settled.Id.ToString("N"));
        list[held.Requested.Id].EvidenceReference.ShouldBeNull();
    }

    [Fact]
    public async Task A_tenant_sees_only_their_own_payments_and_a_return_under_review_named_only_as_that()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var held = await HoldEveryKindAsync(h, ct);

        var list = (await h.QueryAsync(new GetPayments(h.Tenants[0]), ct)).ToDictionary(x => x.Id);

        list.Keys.ShouldNotContain(held.Requested.Id);
        list.Count.ShouldBe(9);
        // Why a return could not post is the owner's and the ledger's position; the tenant is told it is under review.
        list.ToDictionary(x => x.Key, x => (x.Value.Status, x.Value.Reason)).ShouldBe(new Dictionary<Guid, (string, string?)>
        {
            [held.Settled.Id] = ("Settled", null),
            [held.Retryable.Id] = ("NeedsReview", "technical_failure"),
            [held.Rejected.Id] = ("NeedsReview", "processor_rejected"),
            [held.ReturnOffered.Id] = ("NeedsReview", "return_requires_review"),
            [held.ReturnRefusedByLedger.Id] = ("NeedsReview", "return_requires_review"),
            [held.ReturnPartial.Id] = ("NeedsReview", "return_requires_review"),
            [held.Returned.Id] = ("Returned", null),
            [held.AfterReturn.Id] = ("NeedsReview", "return_requires_review"),
            [held.Closed.Id] = ("ReviewClosed", "return_requires_review"),
        }, ignoreOrder: true);
        list.Values.Where(x => x.CanRetry).Select(x => x.Id).ShouldBe([held.Retryable.Id]);
        list.Values.Where(x => x.ReceiptRecorded).Count().ShouldBe(7);
        // Nothing written for staff reaches a tenant: no action, no journal reference, no closing note.
        list.Values.ShouldAllBe(x => !x.CanPostReturn && !x.CanCloseReview && x.ReceiptEntryId == null
            && x.ReturnEntryId == null && x.ReviewNote == null && x.ReviewClosedAt == null);
        var settled = list[held.Settled.Id];
        (settled.Method, settled.ChargedAmount, settled.PaidAt, settled.EvidenceReference)
            .ShouldBe((PaymentMethods.Card, 102.50m, (DateTime?)held.PaidAt, "evidence-" + held.Settled.Id.ToString("N")));

        (await h.QueryAsync(new GetPayments(h.Tenants[1]), ct)).Select(x => x.Id).ShouldBe([held.Requested.Id]);
    }

    [Fact]
    public async Task Payments_are_listed_newest_first_and_one_is_found_by_id_only_by_staff_or_its_own_tenant()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var a = await SubmitAsync(h, 0, 10m, ct);
        var b = await SubmitAsync(h, 0, 20m, ct);
        var c = await SubmitAsync(h, 1, 30m, ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await h.RestampAsync<PaymentOperation>(a.Id, now.AddHours(-2), ct);
        await h.RestampAsync<PaymentOperation>(b.Id, now.AddHours(-1), ct);
        await h.RestampAsync<PaymentOperation>(c.Id, now.AddHours(-1), ct);
        // b and c were stored at one instant: the later id comes first, as the database orders ids.
        Guid[] tied = string.CompareOrdinal(b.Id.ToString(), c.Id.ToString()) > 0 ? [b.Id, c.Id] : [c.Id, b.Id];

        (await h.QueryAsync(new GetPayments(null), ct)).Select(x => x.Id).ShouldBe([.. tied, a.Id]);
        (await h.QueryAsync(new GetPayments(h.Tenants[0]), ct)).Select(x => x.Id).ShouldBe([b.Id, a.Id]);

        (await h.QueryAsync(new GetPayments(null, a.Id), ct)).ShouldHaveSingleItem().Amount.ShouldBe(10m);
        (await h.QueryAsync(new GetPayments(h.Tenants[0], a.Id), ct)).ShouldHaveSingleItem().Amount.ShouldBe(10m);
        (await h.QueryAsync(new GetPayments(h.Tenants[1], a.Id), ct)).ShouldBeEmpty();
        (await h.QueryAsync(new GetPayments(null, UuidV7.NewId()), ct)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Each_payment_carries_the_newest_bank_evidence_reference_held_for_it()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var reported = await h.AcceptedAsync(100m, ct);
        var silent = await h.AcceptedAsync(50m, ct);
        var requested = await SubmitAsync(h, 0, 25m, ct);
        var now = h.Clock.GetUtcNow().UtcDateTime;
        await h.StoreObservationAsync(h.Observation(reported, "Processing") with { EvidenceId = "ev-first" }, now.AddMinutes(-3), ct);
        await h.StoreObservationAsync(h.Observation(reported, "Succeeded") with { EvidenceId = "ev-latest" }, now.AddMinutes(-2), ct);
        // A later notice with no evidence of its own does not hide the evidence before it.
        await h.StoreObservationAsync(h.Observation(reported, "Available") with { EvidenceId = "" }, now.AddMinutes(-1), ct);

        foreach (var list in new[] { await h.QueryAsync(new GetPayments(null), ct), await h.QueryAsync(new GetPayments(h.Tenants[0]), ct) })
        {
            list.ToDictionary(x => x.Id, x => x.EvidenceReference).ShouldBe(new Dictionary<Guid, string?>
            {
                [reported.Id] = "ev-latest",
                [silent.Id] = null,
                [requested.Id] = null,
            }, ignoreOrder: true);
        }
    }
}
