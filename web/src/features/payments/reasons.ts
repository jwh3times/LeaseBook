/** Why a simulated payment waits for a person, in staff words. Keyed by the server's stable reason code. */
export const REASONS: Record<string, string> = {
  technical_failure:
    'Delivery or posting could not finish. An administrator can retry the same operation.',
  accounting_period_locked:
    'The evidence date is in a locked period. Accounting review is required; retry cannot change the date.',
  attribution_unavailable:
    'The trust bank or lease attribution is unavailable for the evidence date. Review the bank and lease records.',
  return_requires_review:
    'A return, refund or dispute arrived after the receipt posted. No automatic reversal was made. An administrator can post a full return, or close the review after correcting the ledger.',
  return_prepayment_consumed:
    'The return was not posted: part of this payment became prepaid credit that has since been used. Correct the ledger, then close the review.',
  return_owner_funds_disbursed:
    'The return was not posted: funds from this payment have since left the owner’s balance. Correct the ledger, then close the review.',
  return_precedes_receipt:
    'The return was not posted: it is dated before the payment it returns. Review the evidence, then close the review.',
  evidence_after_return:
    'More return, refund or dispute evidence arrived after the return was posted. Review the evidence and the ledger, then close the review.',
  return_period_locked:
    'The return was not posted: it is dated in a locked period, and the date cannot be changed. Correct the ledger, then close the review.',
  return_partial_unsupported:
    'The return was not posted: only a full return can be posted. Correct the ledger, then close the review.',
  return_kind_unsupported:
    'The return was not posted: a refund or dispute cannot be posted as a return. Correct the ledger, then close the review.',
  return_conflicting_evidence:
    'The return was not posted: the return evidence conflicts. Review the evidence, then close the review.',
  return_rejected:
    'The return was not posted: accounting refused it. Review the ledger, then close the review.',
  unsupported_settlement:
    'Settlement includes an unsupported amount, fee or destination. Review the evidence; no force-post action is available.',
  conflicting_evidence:
    'The evidence conflicts. Review the operation before taking further action.',
  collection_failed: 'Collection failed. The tenant may submit a new simulated payment.',
  accounting_rejected: 'Accounting rejected the receipt. Review the operation and ledger.',
  fixture_unavailable:
    'The fixture binding is unavailable. Ask the fixture operator to check its configuration.',
};

/**
 * Why a payout waits for a person (ADR-053). A payout posts completely or not at all, so every reason
 * here means nothing of it was posted.
 */
const PAYOUT_REASONS: Record<string, string> = {
  settlement_incomplete:
    'A line of this payout names a payment LeaseBook cannot identify, so nothing was posted. Close the review after correcting the ledger, or wait for corrected evidence.',
  settlement_untied:
    'The lines of this payout do not add up to the bank amount, or a charge differs from the payment, so nothing was posted. Close the review after correcting the ledger, or wait for corrected evidence.',
  unsupported_settlement:
    'This payout contains something LeaseBook does not post, such as a refund, a dispute, a partial return or a reserve. Correct the ledger, then close the review.',
  settlement_requires_confirmation:
    'This payout contains a returned payment, so it waits for an administrator. Posting it posts every line of the payout together, or none of them.',
  pm_fees_insufficient:
    'The management fees held in the account do not cover a processor fee shortfall in this payout, so nothing was posted. Fund the held fees, then post again.',
  settlement_period_locked:
    'The payout’s bank date is in a locked period, and the date cannot be changed. Correct the ledger, then close the review.',
  attribution_unavailable:
    'A tenant in this payout has no lease on the bank date, so nothing was posted. Review the lease, then post again.',
  technical_failure:
    'Checking this payout could not finish. Post it to try again, or close the review.',
  conflicting_evidence:
    'The evidence for this payout conflicts with what is already recorded, so nothing was posted. Review it, then close the review.',
};

export function payoutReason(reason: string): string {
  if (PAYOUT_REASONS[reason]) return PAYOUT_REASONS[reason];
  if (reason.startsWith('return_') && REASONS[reason])
    return `A return in this payout cannot be posted. ${REASONS[reason]}`;
  return 'Accounting refused this payout, so nothing was posted. Review the ledger, then close the review.';
}
