using LeaseBook.Modules.Payments.Domain;
using LeaseBook.Modules.Payments.Features;
using Shouldly;

namespace LeaseBook.Tests.Payments;

/// <summary>
/// What an administrator is told when a return or a payout did not post (#490, ADR-053). Each stable
/// reason has its own sentence saying what stopped it and what to do next; a reason with none of its
/// own gets the general one, and a return's reason inside a payout is told as the return's.
/// </summary>
public sealed class ReviewMessageTests
{
    public static TheoryData<string, string> ReturnRefusals => new()
    {
        { "return_prepayment_consumed",
            "Part of this payment became prepaid credit that has since been used, so the return cannot be posted. Correct the ledger, then close the review." },
        { "return_owner_funds_disbursed",
            "Funds from this payment have since left the owner's balance, so the return cannot be posted. Correct the ledger, then close the review." },
        { "return_precedes_receipt",
            "The return is dated before the payment it returns, so it cannot be posted. Review the evidence, then close the review." },
        { "return_period_locked",
            "The return is dated in a locked period, and the date cannot be changed. Correct the ledger, then close the review." },
        { "return_partial_unsupported", "Only a full return can be posted. Correct the ledger, then close the review." },
        { "return_kind_unsupported", "A refund or dispute cannot be posted as a return. Correct the ledger, then close the review." },
        { "return_conflicting_evidence", "The return evidence conflicts, so nothing was posted. Review the evidence, then close the review." },
        { "return_rejected", "Accounting refused the return, so nothing was posted. Review the ledger, then close the review." },
    };

    [Theory]
    [MemberData(nameof(ReturnRefusals))]
    public void Each_refused_return_is_explained_by_what_stopped_it(string refusal, string message) =>
        PaymentReviewResult.Describe(refusal).ShouldBe(message);

    public static TheoryData<string, string> PayoutRefusals => new()
    {
        { "settlement_incomplete", "A line of this payout names a payment LeaseBook cannot identify, so nothing was posted." },
        { "settlement_untied",
            "The lines of this payout do not add up to the bank amount, or a charge differs from the payment, so nothing was posted." },
        { "unsupported_settlement",
            "This payout contains something LeaseBook does not post, such as a refund, a dispute, a partial return or a reserve. Correct the ledger, then close the review." },
        { "pm_fees_insufficient",
            "The management fees held in the account do not cover a processor fee shortfall in this payout, so nothing was posted. Fund the held fees, then post again." },
        { "settlement_period_locked",
            "The payout's bank date is in a locked period, and the date cannot be changed. Correct the ledger, then close the review." },
        { "attribution_unavailable",
            "A tenant in this payout has no lease on the bank date, so nothing was posted. Review the lease, then post again." },
        { "technical_failure", "Checking this payout could not finish. Post it to try again, or close the review." },
        { "conflicting_evidence",
            "The evidence for this payout conflicts with what is already recorded, so nothing was posted. Review it, then close the review." },
        { SettlementReasons.BankAmountMismatch,
            "The bank received a different amount from the one the processor says it paid, so nothing was posted. Review it, then close the review." },
        { SettlementReasons.PayoutFailed,
            "The processor reports that this payout failed. Nothing was reversed. Review the ledger, then close the review." },
        { SettlementReasons.BankEvidenceMissing, "The bank has not yet confirmed this payout, so nothing was posted." },
        // A return's own refusal, inside a payout, is told as the return's.
        { "return_partial_unsupported",
            "A return in this payout cannot be posted: Only a full return can be posted. Correct the ledger, then close the review." },
        { "return_rejected",
            "A return in this payout cannot be posted: Accounting refused the return, so nothing was posted. Review the ledger, then close the review." },
        { "accounting_rejected",
            "Accounting refused this payout, so nothing was posted. Review the ledger, then close the review." },
    };

    [Theory]
    [MemberData(nameof(PayoutRefusals))]
    public void Each_payout_that_did_not_post_is_explained_by_what_stopped_it(string refusal, string message) =>
        SettlementReviewResult.Describe(refusal).ShouldBe(message);
}
