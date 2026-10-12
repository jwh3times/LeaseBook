import { describe, expect, it } from 'vitest';
import { paymentReason, payoutReason, REASONS, tenantReviewNote } from './reasons';
import { wording } from './wording';

describe('payment reasons', () => {
  const view = (reason: string | null, receiptRecorded = true, canPostReturn = false) => ({
    reason,
    receiptRecorded,
    canPostReturn,
  });

  it('has no text for a payment with no reason', () => {
    expect(paymentReason(view(null), wording(null))).toBeNull();
  });

  it('words a failed collection by mode and every fixed reason the same in both', () => {
    expect(paymentReason(view('collection_failed'), wording('Simulation'))).toBe(
      'Collection failed. The tenant may submit a new simulated payment.',
    );
    expect(paymentReason(view('collection_failed'), wording('StripeSandbox'))).toBe(
      'Collection failed. The tenant may submit a new test payment.',
    );
    for (const [code, text] of Object.entries(REASONS).filter(
      ([code]) => code !== 'return_requires_review',
    )) {
      expect(paymentReason(view(code), wording('StripeSandbox'))).toBe(text);
      expect(text).not.toMatch(/simulat/i);
    }
  });

  it('falls back to a sentence for a reason it does not know', () => {
    expect(paymentReason(view('something_new'), wording(null))).toBe(
      'Review required. Contact the fixture operator.',
    );
  });

  it('follows the receipt and the offered action for a return in review', () => {
    const words = wording(null);
    expect(paymentReason(view('return_requires_review', true, true), words)).toMatch(
      /after the receipt posted\. No automatic reversal was made\. An administrator can post a full return/,
    );
    const unpostable = paymentReason(view('return_requires_review', true, false), words);
    expect(unpostable).toMatch(/its receipt is on the ledger\. No automatic reversal was made\./);
    expect(unpostable).not.toMatch(/can post a full return/);
    expect(unpostable).not.toMatch(/leave this review open/);
    // In the sandbox a bank's return posts with its payout, and a closed review blocks that payout.
    expect(
      paymentReason(view('return_requires_review', true, false), wording('StripeSandbox')),
    ).toMatch(/leave this review open: the return posts with the payout that carries it/);
    const unreceipted = paymentReason(view('return_requires_review', false, false), words);
    expect(unreceipted).toMatch(/No receipt has posted for it/);
    expect(unreceipted).not.toMatch(/after the receipt posted|can post a full return/);
  });

  it('gives a tenant one sentence that claims nothing about the ledger unless no receipt posted', () => {
    expect(tenantReviewNote({ receiptRecorded: true })).toBe(
      'This payment is waiting for your property manager to check it.',
    );
    expect(tenantReviewNote({ receiptRecorded: false })).toBe(
      'This payment is waiting for your property manager to check it. Nothing has been recorded on your ledger.',
    );
  });
});

describe('payout reasons', () => {
  it('words the sandbox payout reasons', () => {
    expect(payoutReason('bank_amount_mismatch')).toBe(
      'The bank received a different amount from the one the processor says it paid, so nothing was posted. Review it, then close the review.',
    );
    expect(payoutReason('bank_evidence_missing')).toBe(
      'No bank has confirmed this payout, so an administrator cannot post it and nothing was posted. It can be posted once the bank’s evidence arrives.',
    );
  });

  it('says of a failed payout whether it had posted', () => {
    expect(payoutReason('payout_failed')).toMatch(
      /The payout was not posted, and nothing was reversed/,
    );
    expect(payoutReason('payout_failed', true)).toMatch(
      /failed after it was posted\. Nothing was reversed automatically, so the ledger still shows it\. Correct the ledger/,
    );
    expect(payoutReason('payout_failed', true)).not.toMatch(/was not posted/);
  });
});
