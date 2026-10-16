using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Processing;
using LeaseBook.Tests.Common;
using LeaseBook.Tests.Payments.Support;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// Posting a bank's return of a settled receipt (#490, ADR-052): only a person starts it, only one
/// complete return of the whole ledger amount posts, and every refusal names what stopped it.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class PaymentReturnTests(PostgresFixture fixture)
{
    // A settled payment of 100.00 with a return of all of it in review.
    private static async Task<PaymentOperation> ReturnedInReviewAsync(PaymentsHarness h, CancellationToken ct,
        Func<ProcessorObservation, ProcessorObservation>? shape = null)
    {
        var op = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(op, "BankCredit"), ct);
        var returned = h.Observation(op, "Return") with { BankDate = h.Today.AddDays(3) };
        await h.EmitAsync(shape is null ? returned : shape(returned), ct);
        return await h.ReadAsync(op.Id, ct);
    }

    private static Task<PaymentReturnDecision?> PostAsync(PaymentsHarness h, Guid id, CancellationToken ct) =>
        h.InOrg(() => h.Payments.PostReturnAsync(h.Binding, id, ct), ct);

    public static TheoryData<string, string> Notes() => new()
    {
        { PaymentModes.Simulation, "Bank returned this simulated tenant payment." },
        { PaymentModes.StripeSandbox, "Bank returned this Stripe sandbox tenant payment." },
    };

    [Theory]
    [MemberData(nameof(Notes))]
    public async Task A_full_return_posts_as_the_receipts_reversal_on_the_return_date_once(string mode, string note)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct, mode: mode);
        var op = await ReturnedInReviewAsync(h, ct);
        (op.Status, op.Reason).ShouldBe(("NeedsReview", "return_requires_review"));
        PaymentEngine.ReturnAwaitsDecision(op).ShouldBeTrue();

        var decision = await PostAsync(h, op.Id, ct);

        decision!.Refusal.ShouldBeNull();
        decision.Operation.Status.ShouldBe("Returned");
        h.Ledger.Returns.ShouldBe([new ReturnRequest(op.JournalId!.Value, h.Today.AddDays(3), $"sim-payment:{op.Id:N}:return", note)]);
        var returned = await h.ReadAsync(op.Id, ct);
        (returned.Status, returned.Reason, returned.JournalId).ShouldBe(("Returned", (string?)null, op.JournalId));
        returned.LastAttemptAt!.Value.ShouldBe(h.Clock.GetUtcNow().UtcDateTime, TimeSpan.FromMilliseconds(1));
        var effect = (await h.ReadEffectsAsync(ct)).Single(x => x.Kind == "Return");
        var evidence = (await h.ReadObservationsAsync(ct)).Single(x => x.Kind == "Return");
        (effect.OperationId, effect.ObservationId, effect.SettlementId).ShouldBe((op.Id, (Guid?)evidence.Id, (Guid?)null));

        // Posting again finds the return and posts nothing.
        (await PostAsync(h, op.Id, ct))!.Refusal.ShouldBeNull();
        h.Ledger.Returns.Count.ShouldBe(1);

        // Later news that agrees with the return keeps it returned.
        await h.EmitAsync(h.Observation(op, "PayoutPaid"), ct);
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Returned");
    }

    public static TheoryData<string> LaterNews() => ["Refund", "Dispute", "another return"];

    [Theory]
    [MemberData(nameof(LaterNews))]
    public async Task News_that_disagrees_with_a_posted_return_goes_back_to_a_person(string news)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnedInReviewAsync(h, ct);
        await PostAsync(h, op.Id, ct);

        await h.EmitAsync(news == "another return"
            ? h.Observation(op, "Return", "second-return") with { EvidenceId = "evidence-other" }
            : h.Observation(op, news), ct);

        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason).ShouldBe(("NeedsReview", "evidence_after_return"));
        h.Ledger.Returns.Count.ShouldBe(1);
    }

    [Fact]
    public async Task The_same_return_reported_twice_keeps_the_posted_return()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnedInReviewAsync(h, ct);
        await PostAsync(h, op.Id, ct);
        var first = (await h.ReadObservationsAsync(ct)).Single(x => x.Kind == "Return");

        await h.EmitAsync(h.Observation(op, "Return", "same-return-again") with { BankDate = first.BankDate }, ct);

        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Returned");
    }

    public static TheoryData<string, string> Refused() => new()
    {
        { "part of the amount", "return_partial_unsupported" },
        { "a fee kept", "return_partial_unsupported" },
        { "not complete", "return_partial_unsupported" },
        { "another currency", "return_partial_unsupported" },
        { "another bank", "return_partial_unsupported" },
        { "no bank date", "return_partial_unsupported" },
        { "two returns that disagree", "return_conflicting_evidence" },
        { "a refund beside it", "return_kind_unsupported" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_return_that_cannot_be_posted_is_refused_with_its_reason_and_posts_nothing(string scenario, string reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnedInReviewAsync(h, ct, scenario switch
        {
            "part of the amount" => x => x with { Gross = 60m, Net = 60m },
            "a fee kept" => x => x with { Fee = 1m, Net = 99m },
            "not complete" => x => x with { Complete = false },
            "another currency" => x => x with { Currency = "CAD" },
            "another bank" => x => x with { BankId = Guid.NewGuid() },
            "no bank date" => x => x with { BankDate = default },
            _ => null,
        });
        switch (scenario)
        {
            case "two returns that disagree":
                await h.EmitAsync(h.Observation(op, "Return", "second-return") with { BankDate = h.Today.AddDays(4) }, ct);
                break;
            case "a refund beside it": await h.EmitAsync(h.Observation(op, "Refund"), ct); break;
        }
        var before = await h.ReadAsync(op.Id, ct);
        PaymentEngine.ReturnAwaitsDecision(before).ShouldBeTrue();

        var decision = await PostAsync(h, op.Id, ct);

        decision!.Refusal.ShouldBe(reason);
        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason).ShouldBe(("NeedsReview", reason));
        h.Ledger.Returns.ShouldBeEmpty();
        (await h.ReadEffectsAsync(ct)).ShouldNotContain(x => x.Kind == "Return");
        // A refused return is still awaiting a decision: its reason is a return_* one.
        PaymentEngine.ReturnAwaitsDecision(held).ShouldBeTrue();
    }

    [Fact]
    public async Task A_return_the_ledger_refuses_stays_in_review_under_the_ledgers_reason()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var op = await ReturnedInReviewAsync(h, ct);
        h.Ledger.ReturnRefusal = "return_owner_funds_disbursed";

        var decision = await PostAsync(h, op.Id, ct);

        decision!.Refusal.ShouldBe("return_owner_funds_disbursed");
        var held = await h.ReadAsync(op.Id, ct);
        (held.Status, held.Reason).ShouldBe(("NeedsReview", "return_owner_funds_disbursed"));
        h.Ledger.Returns.Count.ShouldBe(1);
        (await h.ReadEffectsAsync(ct)).ShouldNotContain(x => x.Kind == "Return");

        // Mended, a person asks again and it posts.
        h.Ledger.ReturnRefusal = null;
        (await PostAsync(h, op.Id, ct))!.Refusal.ShouldBeNull();
        (await h.ReadAsync(op.Id, ct)).Status.ShouldBe("Returned");
    }

    [Fact]
    public async Task Only_a_settled_payment_in_review_for_a_return_can_have_one_posted()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var h = await PaymentsHarness.CreateAsync(fixture, ct);
        var waiting = await h.AcceptedAsync(100m, ct);
        var unsettled = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(unsettled, "Return"), ct);
        var conflicted = await h.AcceptedAsync(100m, ct);
        await h.EmitAsync(h.Observation(conflicted, "BankCredit"), ct);
        await h.EmitAsync(h.Observation(conflicted, "Failed"), ct);

        foreach (var id in new[] { waiting.Id, unsettled.Id, conflicted.Id })
        {
            await Should.ThrowAsync<PaymentConflictException>(() => PostAsync(h, id, ct));
        }
        (await PostAsync(h, Guid.NewGuid(), ct)).ShouldBeNull();
        h.Ledger.Returns.ShouldBeEmpty();
    }

    public static TheoryData<string, string?, bool, bool, bool> Awaiting() => new()
    {
        { "NeedsReview", "return_requires_review", true, true, true },
        { "NeedsReview", "return_partial_unsupported", true, true, true },
        { "NeedsReview", "conflicting_evidence", true, true, false },
        { "NeedsReview", null, true, true, false },
        { "Settled", "return_requires_review", true, true, false },
        { "NeedsReview", "return_requires_review", false, true, false },
        { "NeedsReview", "return_requires_review", true, false, false },
        { "NeedsReview", "requires_return_review", true, true, false },
    };

    [Theory]
    [MemberData(nameof(Awaiting))]
    public void A_return_awaits_a_decision_only_in_review_for_a_return_with_a_receipt_and_a_reference(
        string status, string? reason, bool journal, bool reference, bool expected)
    {
        var op = new PaymentOperation
        {
            Status = status,
            Reason = reason,
            JournalId = journal ? Guid.NewGuid() : null,
            ProviderId = reference ? "prov" : null,
        };

        PaymentEngine.ReturnAwaitsDecision(op).ShouldBe(expected);
    }

    private static PaymentObservation Fact(string kind, decimal gross = 100m, decimal fee = 0m, decimal? net = null,
        string evidence = "ev", int day = 1, bool complete = true) => new()
        {
            Kind = kind,
            Gross = gross,
            Fee = fee,
            Net = net ?? gross - fee,
            Currency = "USD",
            EvidenceId = evidence,
            BankDate = new DateOnly(2026, 3, day),
            Complete = complete,
        };

    [Fact]
    public void The_return_rule_admits_one_complete_return_of_the_whole_amount_and_names_each_refusal()
    {
        var op = new PaymentOperation { Amount = 100m, QuotedFee = 2m, Currency = "USD" };

        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return")]).ShouldBeNull();
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Return"), Fact("BankCredit", 102m, 2m)]).ShouldBeNull();
        PaymentEngine.ReturnEvidenceRefusal(op, []).ShouldBe("return_kind_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("BankCredit")]).ShouldBe("return_kind_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Dispute")]).ShouldBe("return_kind_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact(PaymentEngine.ConflictKind)]).ShouldBe("return_conflicting_evidence");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Return", evidence: "other")]).ShouldBe("return_conflicting_evidence");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Return", day: 2)]).ShouldBe("return_conflicting_evidence");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Return", gross: 50m)]).ShouldBe("return_conflicting_evidence");
        // The ledger amount goes back, not the charge with the fee on it.
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return", gross: 102m)]).ShouldBe("return_partial_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return", net: 99.99m)]).ShouldBe("return_partial_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return", complete: false)]).ShouldBe("return_partial_unsupported");
        // Each of the whole: the same evidence reported again, once not complete, does not pass.
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return"), Fact("Return", complete: false)]).ShouldBe("return_partial_unsupported");
        // Each figure on its own.
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return", gross: 102m, net: 100m)]).ShouldBe("return_partial_unsupported");
        PaymentEngine.ReturnEvidenceRefusal(op, [Fact("Return", fee: 1m, net: 100m)]).ShouldBe("return_partial_unsupported");
    }
}
