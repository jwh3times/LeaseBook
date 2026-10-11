/**
 * What the payment screens call a payment, by the host's payment mode (ADR-054). A host runs one
 * mode: the simulation, or the Stripe sandbox, where a real processor is asked and still no money
 * moves. Every word a person reads that names the mode comes from here, so the two cannot drift
 * apart screen by screen.
 */
export interface Wording {
  /** The panel's heading. */
  heading: string;
  /** The badge under the heading: what this is, and that no money moves. */
  badge: string;
  /** Title and message of a list that could not be read. */
  loadFailedTitle: string;
  loadFailed: string;
  /** The tenant's submit button, and what is said when submitting or retrying could not finish. */
  submit: string;
  submitFailed: string;
  retryFailed: string;
  /** A list with nothing in it. */
  empty: string;
  /** Staff text for a collection that failed. */
  collectionFailed: string;
  /** A list of payouts with nothing in it: says who a payout is first heard of from. */
  noPayouts: string;
  /** One line per payment status. */
  status: {
    requested: string;
    processing: string;
    failed: string;
    settled: string;
    returned: string;
    reviewClosedWithReceipt: string;
    reviewClosedNoReceipt: string;
    needsReviewWithReceipt: string;
    needsReviewNoReceipt: string;
  };
}

const SIMULATION: Wording = {
  heading: 'Simulated payments',
  badge: 'Simulation — no real money moves',
  loadFailedTitle: 'Couldn’t load simulated payments',
  loadFailed: 'Unable to load simulated payments.',
  submit: 'Submit simulated payment',
  submitFailed: 'Unable to confirm the simulated payment request.',
  retryFailed: 'Unable to retry the simulated payment.',
  empty: 'No simulated payments yet.',
  collectionFailed: 'Collection failed. The tenant may submit a new simulated payment.',
  noPayouts: 'No payouts yet. A payout appears here when the bank reports it.',
  status: {
    requested: 'Simulation requested',
    processing: 'Simulated payment processing — not yet on your ledger',
    failed: 'Simulated payment failed — no payment recorded',
    settled: 'Simulated payment recorded',
    returned: 'Simulated payment returned by the bank — the receipt was reversed',
    reviewClosedWithReceipt: 'Simulated payment review closed — original receipt remains recorded',
    reviewClosedNoReceipt: 'Simulated payment review closed — no receipt recorded',
    needsReviewWithReceipt: 'Simulated payment needs review — original receipt remains recorded',
    needsReviewNoReceipt: 'Simulated payment needs review — no receipt recorded',
  },
};

const STRIPE_SANDBOX: Wording = {
  heading: 'Test payments',
  badge: 'Test payment — no real money moves',
  loadFailedTitle: 'Couldn’t load test payments',
  loadFailed: 'Unable to load test payments.',
  submit: 'Submit test payment',
  submitFailed: 'Unable to confirm the test payment request.',
  retryFailed: 'Unable to retry the test payment.',
  empty: 'No test payments yet.',
  collectionFailed: 'Collection failed. The tenant may submit a new test payment.',
  // The processor reports a sandbox payout before any bank does; it then waits for the bank.
  noPayouts: 'No payouts yet. A payout appears here when the processor reports it.',
  status: {
    requested: 'Test payment requested',
    processing: 'Test payment processing — not yet on your ledger',
    failed: 'Test payment failed — no payment recorded',
    settled: 'Test payment recorded',
    returned: 'Test payment returned by the bank — the receipt was reversed',
    reviewClosedWithReceipt: 'Test payment review closed — original receipt remains recorded',
    reviewClosedNoReceipt: 'Test payment review closed — no receipt recorded',
    needsReviewWithReceipt: 'Test payment needs review — original receipt remains recorded',
    needsReviewNoReceipt: 'Test payment needs review — no receipt recorded',
  },
};

/**
 * The wording for a mode as the server names it. A mode that is absent or not known here reads as
 * the simulation: that is what every screen said before there was a second mode.
 */
export function wording(mode: string | null | undefined): Wording {
  return mode === 'StripeSandbox' ? STRIPE_SANDBOX : SIMULATION;
}
