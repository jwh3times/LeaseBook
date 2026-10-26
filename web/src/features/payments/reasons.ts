import type { PaymentView } from '@/api';
import type { Wording } from './wording';

/**
 * Why a payment waits for a person, in staff words. Keyed by the server's stable reason code. Two
 * reasons are not here because their text is not fixed: `collection_failed` names the payment mode,
 * and `return_requires_review` follows what the payment's view says of its receipt. Read a payment's
 * reason through {@link paymentReason}.
 */
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
  outcome_overdue:
    'The processor reported no outcome for this payment within seven days. Nothing was posted. Check the payment with the processor, then close the review.',
  payout_closed:
    'The payout carrying this payment was closed with nothing posted, so it will not bring this payment to the bank. Nothing was posted. Check with the processor where the money went, then close the review.',
  accounting_rejected: 'Accounting rejected the receipt. Review the operation and ledger.',
  fixture_unavailable:
    'The fixture binding is unavailable. Ask the fixture operator to check its configuration.',
};

/**
 * Why a payment waits, for the staff list. A return in review says only what the view carries: whether
 * a receipt is on the ledger, and whether the evidence held can be posted as a return. Null when the
 * payment has no reason.
 */
export function paymentReason(
  payment: Pick<PaymentView, 'reason' | 'receiptRecorded' | 'canPostReturn'>,
  words: Wording,
): string | null {
  const reason = payment.reason;
  if (!reason) return null;
  if (reason === 'collection_failed') return words.collectionFailed;
  if (reason === 'return_requires_review') {
    if (!payment.receiptRecorded)
      return 'A return, refund or dispute was reported for this payment. No receipt has posted for it, so there is nothing to reverse and nothing was posted. Review the evidence, then close the review.';
    if (!payment.canPostReturn) return words.returnNotPostable;
  }
  return REASONS[reason] ?? 'Review required. Contact the fixture operator.';
}

/**
 * What a tenant is told about a payment of theirs that waits for a person. The reason code and the
 * staff text describe the ledger's and the owner's position, which is not the tenant's to read, so
 * a tenant gets this whatever the reason is.
 */
export function tenantReviewNote(payment: Pick<PaymentView, 'receiptRecorded'>): string {
  return payment.receiptRecorded
    ? 'This payment is waiting for your property manager to check it.'
    : 'This payment is waiting for your property manager to check it. Nothing has been recorded on your ledger.';
}

/**
 * Why a payout waits for a person (ADR-053). A payout posts completely or not at all. Every reason
 * here but `payout_failed` means nothing of it was posted; a payout can fail after it posted, so
 * that one is worded by {@link payoutReason} from whether it did.
 */
const PAYOUT_REASONS: Record<string, string> = {
  settlement_incomplete:
    'A line of this payout names a payment LeaseBook does not know yet, or returns one with no receipt yet, so nothing was posted. Post it again once that payment is recorded, or close the review after correcting the ledger.',
  settlement_untied:
    'The lines of this payout do not add up to the bank amount, or a charge differs from the payment, so nothing was posted. Correct the ledger, then close the review.',
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
  bank_amount_mismatch:
    'The bank received a different amount from the one the processor says it paid, so nothing was posted. Review it, then close the review.',
  bank_evidence_missing:
    'No bank has confirmed this payout, so an administrator cannot post it and nothing was posted. It can be posted once the bank’s evidence arrives.',
};

/** @param posted Whether the payout ever posted: its `postedAt` is set. */
export function payoutReason(reason: string, posted = false): string {
  if (reason === 'payout_failed')
    return posted
      ? 'The processor reported that this payout failed after it was posted. Nothing was reversed automatically, so the ledger still shows it. Correct the ledger, then close the review.'
      : 'The processor reported that this payout failed. The payout was not posted, and nothing was reversed. Review it, then close the review.';
  if (PAYOUT_REASONS[reason]) return PAYOUT_REASONS[reason];
  if (reason.startsWith('return_') && REASONS[reason])
    return `A return in this payout cannot be posted. ${REASONS[reason]}`;
  return 'Accounting refused this payout, so nothing was posted. Review the ledger, then close the review.';
}
