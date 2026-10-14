import { writeFileSync } from 'node:fs';
import { type Locator, type Page, expect } from '@playwright/test';
import { signIn } from './helpers';
import {
  type Method,
  PASSWORD,
  administrator,
  chargeStripesFee,
  staffPayout,
} from './stripe-screens';
import { test } from './test';

// A maintainer's run against a real Stripe sandbox (ADR-054, #546). Never run by `npm run e2e`, by
// the stand-in pass or by the coverage run: only `scripts/stripe-sandbox-run.mjs` sets
// STRIPE_SANDBOX_E2E=1, after it has copied the sandbox fixture, started a host on the copy with the
// sandbox's key, started `stripe listen` and checked that the connected account is quiet. That script
// also runs `check-invariants` and owns every Stripe secret; nothing here sees one.
//
// Each test is one step of the run, chosen by STRIPE_SANDBOX_E2E_STEP. The `start` step submits two
// real test charges, so the pass never retries (`playwright.config.ts`). What a step saw is written to
// STRIPE_SANDBOX_E2E_RESULT as it goes, for the script to read: before the first charge is submitted
// the file already says one may have been, so a run that stops halfway never discards the only record
// of a charge Stripe holds.
//
// Stripe acts in its own time here. A card payment can succeed before the tenant's row has shown it
// processing, and an ACH debit takes about half a minute, so a payment is waited for until it reads
// one of its end states, and only then held to the one it should be.

const step =
  process.env.STRIPE_SANDBOX_E2E === '1' ? process.env.STRIPE_SANDBOX_E2E_STEP : undefined;

const PAID = /Paid on \d{4}-\d{2}-\d{2} — on its way to the bank, not yet on your ledger/;
// Every state a test payment can rest in, as the tenant's row words it.
const SETTLED_STATES = [
  PAID,
  /Test payment failed — no payment recorded/,
  /Test payment needs review/,
  /Test payment recorded/,
];

// The run's two payments, and the fee the tenant is quoted for each under Stripe's own rates.
const PAYMENTS: { method: Method; quote: string }[] = [
  { method: 'ach', quote: 'Convenience fee: $4.03 · Total charged: $504.03' },
  { method: 'card', quote: 'Convenience fee: $15.24 · Total charged: $515.24' },
];

function report(result: object): void {
  writeFileSync(process.env.STRIPE_SANDBOX_E2E_RESULT!, `${JSON.stringify(result)}\n`);
}

// A Stripe object's id as the script may print it: the prefix and the last four characters.
const masked = (id: string) => id.replace(/^([a-z]+)_.*(.{4})$/, '$1_…$2');

// The tenant's row's words for a payment once it has stopped moving, waited for as long as Stripe
// may take.
async function restingState(row: Locator): Promise<string> {
  let text = '';
  await expect
    .poll(
      async () => {
        text = await row.innerText();
        return SETTLED_STATES.some((state) => state.test(text));
      },
      { timeout: 5 * 60_000, intervals: [2_000] },
    )
    .toBe(true);
  const line = text.split('\n').find((part) => SETTLED_STATES.some((state) => state.test(part)));
  return (line ?? text).trim();
}

// A tenant submits a test payment from the portal, by keyboard, after reading the quote.
async function submit(
  page: Page,
  method: Method,
  quote: string,
  before: () => void,
): Promise<{ id: string; row: Locator }> {
  await page.getByLabel('Amount (USD)').fill('500.00');
  await page.getByLabel('Pay with').selectOption(method);
  await expect(page.getByText(quote)).toBeVisible();
  const submitted = page.waitForResponse(
    (r) => r.url().endsWith('/api/portal/tenant/payments') && r.request().method() === 'POST',
  );
  before();
  await page.getByRole('button', { name: 'Submit test payment' }).focus();
  await page.keyboard.press('Enter');
  const response = await submitted;
  expect(response.status()).toBeLessThan(300);
  const { id } = (await response.json()) as { id: string };
  await expect(page.getByText(`Payment reference: ${id}`)).toBeVisible();
  return { id, row: page.getByRole('listitem').filter({ hasText: id }) };
}

/**
 * The first half of the runbook's sandbox run: Stripe's fees set as the organization's own, then
 * $500.00 by ACH and $500.00 by card, each charged at Stripe and each paid. Nothing posts: a paid
 * payment waits for Stripe's payout and for the bank.
 */
test('a maintainer’s sandbox run: two test charges, each paid and on its way to the bank @scenario:stripe-sandbox-start', async ({
  page,
  browser,
}) => {
  test.skip(
    step !== 'start',
    'Run only by scripts/stripe-sandbox-run.mjs start, against a real Stripe sandbox.',
  );
  test.setTimeout(15 * 60_000);
  const result: {
    submitting: boolean;
    payments: { method: Method; id: string; state?: string }[];
  } = { submitting: false, payments: [] };
  report(result);

  // The fees the sandbox kept in the runs ADR-054 records. With them, the fee quoted is the fee
  // Stripe keeps, so the payout has nothing left over either way.
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');
  await chargeStripesFee(admin, 'ach');
  await staff.close();

  await signIn(page, { email: 'tenant-a1@payments.test', password: PASSWORD });
  await expect(page.getByRole('heading', { name: 'Test payments' })).toBeVisible();
  await expect(page.getByText('Test payment — no real money moves')).toBeVisible();

  const rows: Locator[] = [];
  for (const { method, quote } of PAYMENTS) {
    const made = await submit(page, method, quote, () => {
      // Said before the click: from here a charge may exist at Stripe.
      result.submitting = true;
      report(result);
    });
    result.payments.push({ method, id: made.id });
    report(result);
    rows.push(made.row);
  }

  for (const [index, row] of rows.entries()) {
    result.payments[index]!.state = await restingState(row);
    report(result);
  }
  for (const [index, row] of rows.entries()) {
    expect(result.payments[index]!.state, `${result.payments[index]!.method} payment`).toMatch(
      PAID,
    );
    await expect(row).toContainText(PAID);
  }
});

interface Line {
  kind: string;
  paymentId: string | null;
}
interface Settlement {
  payoutReference: string;
  status: string;
  reason: string | null;
  createdAt: string;
  processorAmount?: number | string | null;
  items: Line[];
}

// The payout of this run's payments, if Stripe has made one. With the payments unknown, any payout
// stored since the run started.
function thisRunsPayout(items: Settlement[], payments: string[], since: string): Settlement | null {
  const ours = items.filter((payout) =>
    payments.length > 0
      ? payout.items.some((line) => line.paymentId && payments.includes(line.paymentId))
      : Date.parse(payout.createdAt) >= Date.parse(since),
  );
  ours.sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt));
  return ours[0] ?? null;
}

/**
 * The second half, days later: has Stripe paid the two payments out, and how does the host hold the
 * payout? Bank evidence stays a person's step, so nothing posts here either.
 */
test('a maintainer’s sandbox run, resumed: Stripe’s payout of the two payments, held for the bank @scenario:stripe-sandbox-resume', async ({
  browser,
}) => {
  test.skip(
    step !== 'resume',
    'Run only by scripts/stripe-sandbox-run.mjs resume, against a real Stripe sandbox.',
  );
  test.setTimeout(5 * 60_000);
  const payments = (process.env.STRIPE_SANDBOX_E2E_PAYMENTS ?? '').split(',').filter(Boolean);
  const since = process.env.STRIPE_SANDBOX_E2E_SINCE ?? new Date(0).toISOString();

  const { staff, admin } = await administrator(browser, 'a');
  await admin.goto('/operations');
  await expect(admin.getByRole('heading', { name: 'Payouts' })).toBeVisible();
  await expect(admin.getByText('Loading payouts…')).toHaveCount(0);
  const listed = await admin.request.get('/api/payments/settlements');
  expect(listed.status()).toBe(200);
  const { items } = (await listed.json()) as { items: Settlement[] };
  const payout = thisRunsPayout(items, payments, since);

  if (!payout) {
    report({ outcome: 'none', others: items.length });
    if (items.length === 0) {
      await expect(
        admin.getByText('No payouts yet. A payout appears here when the processor reports it.'),
      ).toBeVisible();
    }
    await staff.close();
    return;
  }

  const held = staffPayout(admin, payout.payoutReference);
  await expect(held).toBeVisible();
  const reference = masked(payout.payoutReference);
  if (payout.status === 'NeedsReview') {
    const said = await held.innerText();
    const explanation = (await held.locator('p').first().innerText()).trim();
    report({
      outcome: 'review',
      payout: reference,
      reason: /Reason: (\S+)/.exec(said)?.[1] ?? payout.reason,
      explanation,
    });
    await expect(held.getByText(/^Needs review — /)).toBeVisible();
    await staff.close();
    return;
  }
  if (payout.status !== 'AwaitingBank') {
    report({ outcome: 'other', payout: reference, status: payout.status });
    await staff.close();
    return;
  }

  report({
    outcome: 'held',
    payout: reference,
    amount: String(payout.processorAmount ?? ''),
    lines: payout.items.length,
  });
  await expect(held.getByText('Waiting for the bank', { exact: true })).toBeVisible();
  await expect(held).toContainText(
    'No bank evidence has arrived for this payout. Nothing posts until the bank says what it received.',
  );
  await expect(held).toContainText('Processor’s payout amount: $1,000.00');
  // The header row, then one line for each payment.
  const rows = held.getByRole('row');
  await expect(rows).toHaveCount(3);
  for (const index of [1, 2]) {
    await expect(rows.nth(index).getByRole('cell').nth(1)).toHaveText('Payment');
    await expect(rows.nth(index).getByRole('cell').nth(5)).toHaveText('Not on the ledger');
  }
  // No bank has spoken, so there is nothing for an administrator to post.
  await expect(
    held.getByRole('button', { name: `Post payout ${payout.payoutReference}` }),
  ).toHaveCount(0);
  await staff.close();
});
