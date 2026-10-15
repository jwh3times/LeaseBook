using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// How a payment's outcome is judged from what the processor said of it (ADR-046, ADR-052, ADR-053):
/// what posts a receipt, what goes to a person and under which reason, and what keeps waiting. Each
/// judgement is the worker's: the operation is claimed and completed after the facts are stored.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentOutcomeTests(PostgresFixture fixture)
{
    [Fact]
    public async Task An_accepted_payment_waits_for_its_outcome_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);

        var op = await h.AcceptedAsync(100m, ct);

        (op.Status, op.Reason, op.ProviderId, op.JournalId, op.ProcessedCount, op.Attempts)
            .ShouldBe(("Processing", (string?)null, PaymentsHarness.ProviderOf(op), (Guid?)null, 0, 0));
        (op.LeaseUntil, op.LeaseClaimId).ShouldBe(((DateTime?)null, (Guid?)null));
        op.DueAt.ShouldBe(DateTime.MaxValue, TimeSpan.FromSeconds(1));
        h.Ledger.Receipts.ShouldBeEmpty();
        // At rest: nothing to claim until a fact wakes it.
        (await h.JudgeAsync(op.Id, ct)).ShouldBeFalse();
    }

    public static TheoryData<string, string> Memos() => new()
    {
        { PaymentModes.Simulation, "Simulated tenant payment" },
        { PaymentModes.StripeSandbox, "Stripe sandbox tenant payment" },
    };

    [Theory]
    [MemberData(nameof(Memos))]
    public async Task A_clean_complete_bank_credit_posts_the_ledger_amount_once_on_the_bank_date(string mode, string memo)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct, mode: mode);
        var op = await h.AcceptedAsync(250m, ct, tenant: 1, method: PaymentMethods.Card, fee: 7.78m);
        var credit = h.Observation(op, "BankCredit") with { BankDate = h.Today.AddDays(-2) };

        await h.EmitAsync(credit, ct);

        // The tenant is credited the ledger amount, never the charge: the fee rides on top of it.
        var receipt = h.Ledger.Receipts.ShouldHaveSingleItem();
        receipt.ShouldBe(new ReceiptRequest(h.Tenants[1], h.Binding.BankId, 250m, h.Today.AddDays(-2), PaymentMethods.Card,
            memo, $"sim-payment:{op.Id:N}:receipt", receipt.EntryId));
        var settled = await h.ReadAsync(op.Id, ct);
        (settled.Status, settled.Reason, settled.JournalId, settled.ProcessedCount).ShouldBe(("Settled", (string?)null, receipt.EntryId, 1));
        var effect = (await h.ReadEffectsAsync(ct)).ShouldHaveSingleItem();
        var observation = (await h.ReadObservationsAsync(ct)).ShouldHaveSingleItem();
        (effect.OperationId, effect.ObservationId, effect.SettlementId, effect.JournalId, effect.Kind)
            .ShouldBe((op.Id, (Guid?)observation.Id, (Guid?)null, receipt.EntryId, "Receipt"));
        h.Eligibility.Asked.Last().ShouldBe(new(h.Tenants[1], h.Binding.BankId, h.Today.AddDays(-2)));

        // The same notice again stores nothing and wakes nothing.
        await h.ObserveAsync(credit, ct);
        (await h.JudgeAsync(op.Id, ct)).ShouldBeFalse();
        // Later progress is judged, and a settled payment stays settled with its one receipt.
        await h.EmitAsync(h.Observation(op, "PayoutPaid"), ct);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
        h.Ledger.Receipts.Count.ShouldBe(1);
        (await h.ReadEffectsAsync(ct)).Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_bank_credit_that_is_not_complete_keeps_the_payment_waiting()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);

        await h.EmitAsync(h.Observation(op, "BankCredit") with { Complete = false }, ct);

        var waiting = await h.ReadAsync(op.Id, ct);
        (waiting.Status, waiting.Reason, waiting.ProcessedCount).ShouldBe(("Processing", (string?)null, 1));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    public static TheoryData<string> UncleanCredits() =>
        ["gross", "fee", "net", "bank", "currency", "evidence", "payout", "date"];

    [Theory]
    [MemberData(nameof(UncleanCredits))]
    public async Task A_bank_credit_that_is_not_the_clean_item_goes_to_review_and_posts_nothing(string differs)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct, fee: 3m);
        var credit = h.Observation(op, "BankCredit");
        credit = differs switch
        {
            "gross" => credit with { Gross = 103.01m },
            "fee" => credit with { Fee = 3.50m, Net = 99.50m, Gross = 103m },
            "net" => credit with { Net = 99.99m },
            "bank" => credit with { BankId = Guid.NewGuid() },
            "currency" => credit with { Currency = "EUR" },
            "evidence" => credit with { EvidenceId = " " },
            "payout" => credit with { PayoutId = "" },
            _ => credit with { BankDate = default },
        };

        await h.EmitAsync(credit, ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "unsupported_settlement", (Guid?)null));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_bank_credits_that_name_different_evidence_conflict()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var credit = h.Observation(op, "BankCredit") with { Complete = false };
        await h.ObserveAsync(credit, ct);

        await h.EmitAsync(credit with { EventId = "second-credit", EvidenceId = "evidence-other", Complete = true }, ct);

        (await h.ReadAsync(op.Id, ct)).Reason.ShouldBe("conflicting_evidence");
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task Two_bank_credits_for_the_same_evidence_post_one_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var credit = h.Observation(op, "BankCredit");
        await h.ObserveAsync(credit with { Complete = false }, ct);

        await h.EmitAsync(credit with { EventId = "second-credit" }, ct);

        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
        h.Ledger.Receipts.Count.ShouldBe(1);
    }

    [Fact]
    public async Task A_failed_collection_fails_the_payment_and_posts_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);

        await h.EmitAsync(h.Observation(op, "Failed"), ct);

        var failed = await h.ReadAsync(op.Id, ct);
        (failed.Status, failed.Reason, failed.JournalId).ShouldBe(("Failed", "collection_failed", (Guid?)null));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    public static TheoryData<string, bool> FailedBeside() => new()
    {
        { "Succeeded", true },
        { "Succeeded", false },
        { "BankCredit", true },
        { "BankCredit", false },
    };

    [Theory]
    [MemberData(nameof(FailedBeside))]
    public async Task A_failure_beside_a_success_or_a_credit_is_believed_in_neither_order(string other, bool failureFirst)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var first = h.Observation(op, failureFirst ? "Failed" : other);
        var second = h.Observation(op, failureFirst ? other : "Failed");

        await h.ObserveAsync(first, ct);
        await h.EmitAsync(second, ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "conflicting_evidence", (Guid?)null));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_failure_after_a_receipt_goes_to_review_and_the_receipt_stays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        await h.EmitAsync(h.Observation(op, "Failed"), ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "conflicting_evidence", h.Ledger.Receipts.Single().EntryId));
    }

    public static TheoryData<string> ReturnKinds() => ["Return", "Refund", "Dispute"];

    [Theory]
    [MemberData(nameof(ReturnKinds))]
    public async Task Money_going_back_sends_the_payment_to_a_person_whether_or_not_it_settled(string kind)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var waiting = await h.AcceptedAsync(100m, ct);
        var settled = await h.AcceptedAsync(200m, ct, tenant: 1);
        await h.EmitAsync(h.Observation(settled, "BankCredit"), ct);

        await h.EmitAsync(h.Observation(waiting, kind), ct);
        await h.EmitAsync(h.Observation(settled, kind), ct);

        (await h.ReadAsync(waiting.Id, ct)).Reason.ShouldBe("return_requires_review");
        var reviewed = await h.ReadAsync(settled.Id, ct);
        (reviewed.Status, reviewed.Reason).ShouldBe(("NeedsReview", "return_requires_review"));
        // Return evidence alone never posts: the receipt is the one posted before it.
        h.Ledger.Receipts.Count.ShouldBe(1);
        h.Ledger.Returns.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_bank_credit_beside_a_return_is_not_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.ObserveAsync(h.Observation(op, "Return"), ct);

        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        (await h.ReadAsync(op.Id, ct)).Reason.ShouldBe("return_requires_review");
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_notice_reused_with_other_content_is_kept_as_conflicting_evidence()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var progress = h.Observation(op, "Processing");
        await h.EmitAsync(progress, ct);

        await h.EmitAsync(progress with { Kind = "BankCredit", Gross = 100m, Net = 100m }, ct);

        var observations = await h.ReadObservationsAsync(ct);
        observations.Count.ShouldBe(2);
        var conflict = observations.Single(x => x.EventId != progress.EventId);
        (conflict.Kind, conflict.ProviderId).ShouldBe((PaymentEngine.ConflictKind, PaymentsHarness.ProviderOf(op)));
        conflict.EventId.ShouldBe("conflict:" + conflict.Fingerprint);
        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason).ShouldBe(("NeedsReview", "conflicting_evidence"));
        h.Ledger.Receipts.ShouldBeEmpty();

        // The same altered notice again is the same conflict, stored once.
        await h.ObserveAsync(progress with { Kind = "BankCredit", Gross = 100m, Net = 100m }, ct);
        (await h.ReadObservationsAsync(ct)).Count.ShouldBe(2);
    }

    [Fact]
    public async Task A_payout_reported_failed_for_the_credit_it_paid_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var credit = h.Observation(op, "BankCredit");
        await h.ObserveAsync(h.Observation(op, "PayoutFailed") with { PayoutId = credit.PayoutId }, ct);

        await h.EmitAsync(credit, ct);

        (await h.ReadAsync(op.Id, ct)).Reason.ShouldBe("conflicting_evidence");
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_payout_reported_failed_for_another_payout_does_not_stop_the_receipt()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.ObserveAsync(h.Observation(op, "PayoutFailed") with { PayoutId = "payout-other" }, ct);

        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Settled");
    }

    [Fact]
    public async Task A_credit_for_a_tenant_who_cannot_be_attributed_on_the_bank_date_goes_to_review()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        h.Eligibility.Eligible = false;

        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason, held.JournalId).ShouldBe(("NeedsReview", "attribution_unavailable", (Guid?)null));
        h.Ledger.Receipts.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_paid_date_is_the_earliest_success_the_processor_reported()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        var early = new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

        await h.EmitAsync(h.Observation(op, "Succeeded") with { ObservedAt = early.AddHours(1) }, ct);
        await h.EmitAsync(h.Observation(op, "Succeeded", "earlier-success") with { ObservedAt = early }, ct);
        await h.EmitAsync(h.Observation(op, "Succeeded", "later-success") with { ObservedAt = early.AddDays(1) }, ct);

        var paid = await h.ReadAsync(op.Id, ct);
        (paid.Status, paid.PaidAt).ShouldBe(("Processing", (DateTime?)early));
    }

    [Fact]
    public async Task An_acceptance_that_is_not_one_is_uncertain_and_another_reference_conflicts()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        h.FeeRules.Fee = 0m;
        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, Guid.NewGuid(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        var claim = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!;

        await Should.ThrowAsync<IOException>(() => h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id,
            claim.LeaseClaimId!.Value, new ProcessorResult("Declined", "prov_x"), ct), ct));
        await Should.ThrowAsync<IOException>(() => h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id,
            claim.LeaseClaimId!.Value, new ProcessorResult("Accepted", " "), ct), ct));
        await h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id, claim.LeaseClaimId!.Value,
            new ProcessorResult("Accepted", "prov_first"), ct), ct);

        await h.ObserveAsync(h.Observation(op, "Processing") with { ProviderId = "prov_first" }, ct);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var again = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!;
        await Should.ThrowAsync<PaymentConflictException>(() => h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id,
            again.LeaseClaimId!.Value, new ProcessorResult("Accepted", "prov_second"), ct), ct));
        (await h.ReadAsync(op.Id, ct)).ProviderId.ShouldBe("prov_first");
    }

    [Fact]
    public async Task A_completion_under_a_lease_that_is_no_longer_held_changes_nothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, Guid.NewGuid(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);
        await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct);

        await h.InOrg(() => h.Payments.CompleteAsync(h.Binding, op.Id, Guid.NewGuid(), new ProcessorResult("Accepted", "prov_x"), ct), ct);

        var unchanged = await h.ReadAsync(op.Id, ct);
        (unchanged.Status, unchanged.ProviderId).ShouldBe(("Requested", (string?)null));
        unchanged.LeaseClaimId.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_claim_is_a_lease_and_a_held_one_cannot_be_claimed_again()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.InOrg(() => h.Payments.SubmitAsync(h.Binding, h.Tenants[0], h.UserId, Guid.NewGuid(), 100m, "USD",
            PaymentMethods.Ach, 0m, ct), ct);

        var claim = (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!;

        var now = h.Clock.GetUtcNow().UtcDateTime;
        (claim.LeaseUntil, claim.LastAttemptAt).ShouldBe((now.AddSeconds(30), now));
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct)).ShouldBeNull();
        h.Clock.Advance(TimeSpan.FromSeconds(29));
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct)).ShouldBeNull();
        // The lease runs out at its time, not a tick after it.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, op.Id, ct), ct))!.LeaseClaimId.ShouldNotBe(claim.LeaseClaimId);
        (await h.InOrg(() => h.Payments.ClaimAsync(h.Binding, Guid.NewGuid(), ct), ct)).ShouldBeNull();
    }

    public static TheoryData<string> Ended() => ["Settled", "Failed", "NeedsReview"];

    [Theory]
    [MemberData(nameof(Ended))]
    public async Task An_ended_payment_is_claimed_again_only_for_a_fact_it_has_not_judged(string outcome)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, outcome switch { "Settled" => "BankCredit", "Failed" => "Failed", _ => "Dispute" }), ct);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe(outcome);

        // Woken with nothing new, it is not claimed.
        await h.InOrg(async () =>
        {
            var row = await h.Scope.Db.Set<PaymentOperation>().FindAsync([op.Id], ct);
            row!.DueAt = h.Clock.GetUtcNow().UtcDateTime;
            await h.Scope.Db.SaveChangesAsync(ct);
        }, ct);
        (await h.JudgeAsync(op.Id, ct)).ShouldBeFalse();

        await h.ObserveAsync(h.Observation(op, "Processing", "late-progress"), ct);
        (await h.JudgeAsync(op.Id, ct)).ShouldBeTrue();
    }
}
