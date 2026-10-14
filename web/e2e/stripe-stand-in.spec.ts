import { execFile } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { promisify } from 'node:util';
import { type Locator, type Page, expect } from '@playwright/test';
import { signIn } from './helpers';
import {
  type Method,
  PASSWORD,
  STRIPE_FEES,
  administrator,
  chargeStripesFee,
  scanStaff,
  scanTenant,
  setFeeRule,
  staffPayout,
} from './stripe-screens';
import { test } from './test';

// The Stripe sandbox mode, driven through the tenant and staff screens against a host whose Stripe
// is `scripts/stripe-stand-in.mjs` (ADR-054, #545). Nothing here calls Stripe. The stand-in answers
// the host from the recorded payloads and sends it signed deliveries, as Stripe does; a test tells
// it what happens next, standing in for the passing of time and for Stripe's own schedule.
//
// The database is the stand-in fixture's and is not reset between runs, so a test finds a payment
// and a payout by their own references and reads a balance as a difference.

const STAND_IN_KEY = 'sk_test_leasebook_stand_in';
const run = promisify(execFile);

interface Delivery {
  type: string;
  status: number | null;
}
interface Payout {
  id: string;
  net: string;
  deliveries: Delivery[];
}

const PAYOUT_EVENTS = [
  ['payout.created', 204],
  ['payout.reconciliation_completed', 204],
  ['payout.paid', 204],
  ['payout.updated', 204],
];

function enabled() {
  test.skip(
    process.env.STRIPE_STAND_IN_E2E !== '1',
    'Requires the stand-in payment fixture database and the Stripe stand-in.',
  );
}

// A fixture organization as the manifest binds it. The first is the one `tenant-a1`, `tenant-a2` and
// `admin-a` belong to, and the second the one `tenant-b1` and `admin-b` do.
function fixture(index = 0): { OrgId: string; Account: string } {
  const manifest = JSON.parse(readFileSync(process.env.Payments__ManifestPath!, 'utf8')) as {
    Payments: { Fixtures: { OrgId: string; Account: string }[] };
  };
  const binding = manifest.Payments.Fixtures[index];
  if (!binding) throw new Error('Payment fixture binding missing');
  return binding;
}

// What a test tells the stand-in, and what it reads back from it.
async function standIn<T>(path: string, body?: unknown): Promise<T> {
  const response = await fetch(`${process.env.Payments__Stripe__ApiBase}/stand-in/${path}`, {
    method: body === undefined ? 'GET' : 'POST',
    headers: {
      Authorization: `Bearer ${STAND_IN_KEY}`,
      ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
    },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const answer = (await response.json()) as T;
  expect(response.status, JSON.stringify(answer)).toBe(200);
  return answer;
}

// A fixture command, run as the runbook runs it: the host's own project, with the host's variables.
// The host built the project when it started, so this does not build it again under a running host.
// It answers with what the command printed and whether it was refused.
async function command(...args: string[]): Promise<{ said: string; refused: boolean }> {
  const options = {
    env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' },
    timeout: 120_000,
  };
  const dotnet = [
    'run',
    '--project',
    '../src/LeaseBook.Web',
    '--no-launch-profile',
    '--no-build',
    '--',
    ...args,
  ];
  try {
    const { stdout, stderr } = await run('dotnet', dotnet, options);
    return { said: stdout + stderr, refused: false };
  } catch (error) {
    const failed = error as { code?: unknown; stdout?: string; stderr?: string };
    // Only a command that ran and answered with a failure is a refusal.
    if (typeof failed.code !== 'number') throw error;
    return { said: `${failed.stdout ?? ''}${failed.stderr ?? ''}`, refused: true };
  }
}

async function cli(...args: string[]): Promise<string> {
  const { said, refused } = await command(...args);
  expect(refused, said).toBe(false);
  return said;
}

// What the bank received for a payout, said the only way it can be: by the fixture command.
function bank(org: number, payout: string, amount: string, date: string) {
  return command('payment-simulation', 'bank', fixture(org).OrgId, payout, amount, date);
}

// The tenant's balance as the portal shows it, in dollars: what is owed is positive, a credit is
// negative, and exactly nothing is a dash.
async function balance(page: Page): Promise<number> {
  const card = page.getByRole('heading', { name: 'Rent ledger balance' }).locator('..');
  const shown = /(−?)\$([\d,]+\.\d{2})|—/.exec(await card.innerText());
  if (!shown) throw new Error('The portal shows no rent ledger balance.');
  if (shown[2] === undefined) return 0;
  return (shown[1] ? -1 : 1) * Number(shown[2].replaceAll(',', ''));
}

const cents = (dollars: number) => Math.round(dollars * 100) / 100;

// The ledger rows that are a posted payment of one amount.
function receipts(page: Page, amount: string): Locator {
  return page
    .getByRole('table', { name: 'Rent ledger activity' })
    .getByRole('row')
    .filter({ hasText: 'Payment' })
    .filter({ hasText: 'Posted' })
    .filter({ hasText: amount });
}

// A tenant pays an amount toward the ledger from the portal, by keyboard, after reading the quote.
// The host then charges it at the stand-in, which has not yet said how it went.
async function pay(page: Page, amount: string, method: Method, quote: string) {
  await page.getByLabel('Amount (USD)').fill(amount);
  await page.getByLabel('Pay with').selectOption(method);
  await expect(page.getByText(quote)).toBeVisible();
  const submitted = page.waitForResponse(
    (r) => r.url().endsWith('/api/portal/tenant/payments') && r.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Submit test payment' }).focus();
  await page.keyboard.press('Enter');
  const { id } = (await (await submitted).json()) as { id: string };
  await expect(page.getByText(`Payment reference: ${id}`)).toBeVisible();
  const mine = page.getByRole('listitem').filter({ hasText: id });
  await expect(mine).toContainText('Test payment processing — not yet on your ledger');
  return { id, mine };
}

// Stripe's events for a payment, sent when the test says so: a recorded timeline, by name.
async function happens(payment: string, timeline: string, more: object = {}) {
  const moved = await standIn<{ deliveries: Delivery[] }>(`payments/${payment}/timeline`, {
    timeline,
    ...more,
  });
  return moved.deliveries.map((sent) => [sent.type, sent.status]);
}

// A card payment Stripe says succeeded: its four recorded events, each acknowledged.
async function cardSucceeds(payment: string): Promise<void> {
  expect(await happens(payment, 'card-succeeds')).toEqual([
    ['charge.succeeded', 204],
    ['payment_intent.succeeded', 204],
    ['payment_intent.created', 204],
    ['charge.updated', 204],
  ]);
}

// Stripe pays out, and says so with its four events. The payout is then stored and held.
async function paysOut(payments: string[], returns: string[] = []): Promise<Payout> {
  const payout = await standIn<Payout>('payouts', { payments, returns });
  expect(payout.deliveries.map((sent) => [sent.type, sent.status])).toEqual(PAYOUT_EVENTS);
  return payout;
}

// The staff Operations page, read again after something happened outside it: a delivery from the
// stand-in or the bank command. Its list of payouts reads itself every five seconds, which is as long
// as an assertion waits, so an assertion alone could look a moment too soon.
async function readAgain(admin: Page): Promise<void> {
  await admin.reload();
  await expect(admin.getByRole('heading', { name: 'Payouts' })).toBeVisible();
}

// A payment on the staff Operations page, by its own reference.
const staffPayment = (admin: Page, id: string) =>
  admin.getByRole('listitem').filter({ hasText: `Payment reference: ${id}` });

// An administrator closes a review from the keyboard, with a note: of a payment, or of a payout.
async function closeReview(admin: Page, item: Locator, open: Locator, note: string) {
  await open.focus();
  await admin.keyboard.press('Enter');
  await expect(item.getByLabel('How was this resolved? (staff only)')).toBeFocused();
  await expect(item.getByRole('button', { name: 'Close review', exact: true })).toBeDisabled();
  await expect(item).toContainText('Closing posts nothing. The evidence and this note are kept.');
  await admin.keyboard.type(note);
  await admin.keyboard.press('Enter');
  await expect(item).toContainText(`Review closed by staff: ${note}`);
}

// Everything the host asked the stand-in was a request the stand-in knows.
async function nothingUnexpected(): Promise<void> {
  const state = await standIn<{ unexpected: unknown[] }>('state');
  expect(state.unexpected).toEqual([]);
}

// Every organization of the fixture is checked after each scenario, whatever the scenario left.
async function invariantsHold(): Promise<void> {
  expect(await cli('check-invariants', '--all')).toContain('check-invariants: all clean');
}

test.afterEach(async () => {
  if (process.env.STRIPE_STAND_IN_E2E !== '1') return;
  await invariantsHold();
});

/**
 * A card payment that reaches a posted payout. Stripe says the payment succeeded, and nothing
 * posts. Stripe says it paid a payout, and the payout is held for the bank: an administrator cannot
 * post it. The bank's evidence comes from the fixture command and from nowhere else; it equals
 * Stripe's net, so the payout posts whole, with the tenant's receipt.
 */
test('a card payment succeeds, its payout waits for the bank, and bank evidence equal to the processor’s net posts it @scenario:stripe-stand-in-card-to-posted-payout', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);

  // The organization's card rule is the fee Stripe keeps, 2.9% plus $0.30, so the fee the tenant is
  // quoted is the fee the payout's line carries and nothing is left over either way.
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');

  // The tenant's screen says what kind of payment this is before anything is submitted.
  await signIn(page, { email: 'tenant-a1@payments.test', password: PASSWORD });
  await expect(page.getByRole('heading', { name: 'Test payments' })).toBeVisible();
  await expect(page.getByText('Test payment — no real money moves')).toBeVisible();
  await expect(page.getByText('Simulation — no real money moves')).toHaveCount(0);
  const owed = await balance(page);
  const receiptsBefore = await receipts(page, '$100.00').count();
  await scanTenant(page);

  const payment = await pay(
    page,
    '100.00',
    'card',
    'Convenience fee: $3.30 · Total charged: $103.30',
  );
  const mine = payment.mine;
  await expect(mine).toContainText('Plus a $3.30 convenience fee: $103.30 charged');

  // Stripe's four events for a card payment that succeeds, in the order it sent them. The host
  // acknowledges all four and reads one.
  await cardSucceeds(payment.id);
  // Paid is Stripe's word and not a bank's: the payment has a paid date and the ledger has not moved.
  await expect(mine).toContainText(
    `Paid on ${today} — on its way to the bank, not yet on your ledger`,
  );
  await expect(page.getByRole('status').filter({ hasText: 'In transit:' })).toBeVisible();
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);

  // Stripe pays out, and says so. The payout is stored with Stripe's lines and held.
  const payout = await paysOut([payment.id]);
  expect(payout.net).toBe('100.00');
  await admin.goto('/operations');
  await expect(admin.getByText('Test payment — no real money moves')).toBeVisible();
  const held = staffPayout(admin, payout.id);
  await expect(held.getByText('Waiting for the bank', { exact: true })).toBeVisible();
  await expect(held).toContainText(
    'No bank evidence has arrived for this payout. Nothing posts until the bank says what it received.',
  );
  await expect(held).toContainText('Processor’s payout amount: $100.00');
  await expect(held.getByRole('row').nth(1)).toContainText('Payment');
  await expect(held.getByRole('row').nth(1)).toContainText('Not on the ledger');
  // No bank has spoken, so there is nothing for an administrator to post.
  await expect(held.getByRole('button', { name: `Post payout ${payout.id}` })).toHaveCount(0);
  await expect(mine).toContainText('on its way to the bank, not yet on your ledger');
  await scanStaff(admin);

  // The bank received what Stripe said it paid. Only the fixture command can say so.
  const said = await bank(0, payout.id, payout.net, today);
  expect(said.refused, said.said).toBe(false);
  expect(said.said).toContain(`Payout ${payout.id}: bank amount 100.00 on ${today} recorded.`);
  expect(said.said).toContain('The payout is Posted.');

  await readAgain(admin);
  await expect(held.getByText('Posted', { exact: true })).toBeVisible();
  await expect(held).toContainText(`$100.00 at the bank on ${today}`);
  await expect(held.getByRole('row').nth(1)).toContainText('On the ledger');
  await expect(held.getByRole('button')).toHaveCount(0);
  await scanStaff(admin);
  await staff.close();

  // The tenant has a receipt for what they paid toward the ledger, and owes that much less.
  await expect(mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore + 1);
  await expect.poll(() => balance(page)).toBe(cents(owed - 100));
  await scanTenant(page);

  await nothingUnexpected();
});

/**
 * An ACH debit that reaches a posted payout. Stripe accepts the debit as processing, and reports it
 * succeeded some seconds later with eight events, two of them about the mandate. The organization's
 * ACH rule is set to what Stripe keeps, 0.8%, so the payout posts with no fee difference.
 */
test('an ACH debit is processing, then succeeds, and its payout posts when the bank evidence equals the processor’s net @scenario:stripe-stand-in-ach-to-posted-payout', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'ach');

  await signIn(page, { email: 'tenant-a2@payments.test', password: PASSWORD });
  await expect(page.getByText('Test payment — no real money moves')).toBeVisible();
  const owed = await balance(page);
  const receiptsBefore = await receipts(page, '$100.00').count();

  // $100.81 is the least charge that leaves $100.00 after 0.8% of it.
  const payment = await pay(
    page,
    '100.00',
    'ach',
    'Convenience fee: $0.81 · Total charged: $100.81',
  );
  const mine = payment.mine;
  await expect(mine).toContainText('Plus a $0.81 convenience fee: $100.81 charged');

  // Stripe's eight events for an ACH debit that succeeds, in the order it sent them. The host reads
  // that it is processing and that it succeeded, and acknowledges the rest.
  expect(await happens(payment.id, 'ach-succeeds')).toEqual([
    ['payment_intent.processing', 204],
    ['payment_intent.created', 204],
    ['charge.pending', 204],
    ['charge.updated', 204],
    ['mandate.updated', 204],
    ['mandate.updated', 204],
    ['payment_intent.succeeded', 204],
    ['charge.succeeded', 204],
  ]);
  await expect(mine).toContainText(
    `Paid on ${today} — on its way to the bank, not yet on your ledger`,
  );
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);
  await scanTenant(page);

  const payout = await paysOut([payment.id]);
  expect(payout.net).toBe('100.00');
  await admin.goto('/operations');
  const held = staffPayout(admin, payout.id);
  await expect(held.getByText('Waiting for the bank', { exact: true })).toBeVisible();
  await expect(held).toContainText('Processor’s payout amount: $100.00');
  // Stripe's line for the debit: what was charged, what Stripe kept, and what goes to the bank.
  const line = held.getByRole('row').nth(1);
  await expect(line.getByRole('cell').nth(1)).toHaveText('Payment');
  await expect(line.getByRole('cell').nth(2)).toHaveText('$100.81');
  await expect(line.getByRole('cell').nth(3)).toHaveText('$0.81');
  await expect(line.getByRole('cell').nth(4)).toHaveText('$100.00');
  await expect(held.getByRole('button', { name: `Post payout ${payout.id}` })).toHaveCount(0);
  await scanStaff(admin);

  const said = await bank(0, payout.id, payout.net, today);
  expect(said.said).toContain(`Payout ${payout.id}: bank amount 100.00 on ${today} recorded.`);
  expect(said.said).toContain('The payout is Posted.');
  await readAgain(admin);
  await expect(held.getByText('Posted', { exact: true })).toBeVisible();
  // The fee quoted is the fee kept, so the receipt is the only posting: no fee difference beside it.
  await expect(line.getByRole('cell').nth(5)).toHaveText('On the ledger');
  await expect(held.getByRole('button')).toHaveCount(0);
  await scanStaff(admin);
  await staff.close();

  await expect(mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore + 1);
  await expect.poll(() => balance(page)).toBe(cents(owed - 100));
  await scanTenant(page);

  await nothingUnexpected();
});

/**
 * A card dispute. Stripe reports one with two events that name the payment only by Stripe's id, so
 * the host asks the stand-in whose payment it is and how it was paid. The payment goes to a person
 * and nothing posts: it has no receipt, so there is nothing to reverse. An administrator can only
 * close the review.
 */
test('a card dispute sends the payment to staff review, posts nothing, and an administrator closes the review @scenario:stripe-stand-in-card-dispute', async ({
  page,
  browser,
}) => {
  enabled();
  const run = randomBytes(4).toString('hex');
  const note = `Dispute answered with the card network ${run}`;
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');

  await signIn(page, { email: 'tenant-a1@payments.test', password: PASSWORD });
  const owed = await balance(page);
  const receiptsBefore = await receipts(page, '$100.00').count();
  const payment = await pay(
    page,
    '100.00',
    'card',
    'Convenience fee: $3.30 · Total charged: $103.30',
  );
  const mine = payment.mine;
  await cardSucceeds(payment.id);
  await expect(mine).toContainText('on its way to the bank, not yet on your ledger');

  // The dispute as Stripe recorded one for a card: open, and the funds already withdrawn.
  expect(await happens(payment.id, 'card-disputed')).toEqual([
    ['charge.dispute.created', 204],
    ['charge.dispute.funds_withdrawn', 204],
  ]);
  const held = (await standIn<{ payments: { operation: string; dispute: string }[] }>('state'))
    .payments;
  const dispute = held.find((made) => made.operation === payment.id)!.dispute;

  // The tenant is told a person is checking it, and never why.
  await expect(mine).toContainText('Test payment needs review — no receipt recorded');
  await expect(mine).toContainText(
    'This payment is waiting for your property manager to check it. Nothing has been recorded on your ledger.',
  );
  await expect(mine).not.toContainText('return_requires_review');
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);
  await scanTenant(page);

  // Staff see the reason and the dispute's own reference, and no way to post anything.
  await admin.goto('/operations');
  const review = staffPayment(admin, payment.id);
  await expect(review).toContainText('Test payment needs review — no receipt recorded');
  await expect(review).toContainText(
    'A return, refund or dispute was reported for this payment. No receipt has posted for it, so there is nothing to reverse and nothing was posted. Review the evidence, then close the review.',
  );
  await expect(review).toContainText('Reason: return_requires_review');
  await expect(review).toContainText(`Evidence reference: ${dispute}`);
  await expect(review.getByRole('button', { name: 'Post return' })).toHaveCount(0);
  await expect(review.getByRole('button', { name: 'Retry operation' })).toHaveCount(0);
  await expect(review.getByRole('button')).toHaveCount(1);
  await scanStaff(admin);

  await closeReview(admin, review, review.getByRole('button', { name: 'Close review…' }), note);
  await expect(review).toContainText('Test payment review closed — no receipt recorded');
  await expect(review.getByRole('button')).toHaveCount(0);
  await scanStaff(admin);
  await staff.close();

  await expect(mine).toContainText('Test payment review closed — no receipt recorded');
  await expect(mine).not.toContainText(note);
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);

  await nothingUnexpected();
});

/**
 * A refund, in full and of a part, each of a payment whose receipt is already on the ledger. Stripe
 * sends four events for a refund and the host reads one, `charge.refunded`. Each payment goes to a
 * person. No reversal is made, and a refund cannot be posted as a return: an administrator
 * corrects the ledger by hand and closes the review.
 */
test('a refund in full and a refund of part each send a receipted payment to staff review, and nothing is reversed @scenario:stripe-stand-in-refund', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const run = randomBytes(4).toString('hex');
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');

  await signIn(page, { email: 'tenant-a2@payments.test', password: PASSWORD });
  const owed = await balance(page);
  const whole = await pay(
    page,
    '100.00',
    'card',
    'Convenience fee: $3.30 · Total charged: $103.30',
  );
  const part = await pay(page, '60.00', 'card', 'Convenience fee: $2.10 · Total charged: $62.10');
  await cardSucceeds(whole.id);
  await cardSucceeds(part.id);

  // One payout holds both, and posts both when the bank agrees.
  const payout = await paysOut([whole.id, part.id]);
  expect(payout.net).toBe('160.00');
  const said = await bank(0, payout.id, payout.net, today);
  expect(said.said).toContain('The payout is Posted.');
  await expect(whole.mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect(part.mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect.poll(() => balance(page)).toBe(cents(owed - 160));

  // Stripe refunds the first in full and $25.00 of the second.
  const refund = [
    ['refund.created', 204],
    ['charge.refunded', 204],
    ['refund.updated', 204],
    ['charge.refund.updated', 204],
  ];
  expect(await happens(whole.id, 'card-refunded')).toEqual(refund);
  expect(await happens(part.id, 'card-refunded-in-part', { refund: 2500 })).toEqual(refund);

  for (const mine of [whole.mine, part.mine]) {
    await expect(mine).toContainText(
      'Test payment needs review — original receipt remains recorded',
    );
    await expect(mine).toContainText(
      'This payment is waiting for your property manager to check it.',
    );
    await expect(mine).not.toContainText('Nothing has been recorded on your ledger.');
  }
  // Nothing was reversed: the tenant still owes what they owed once both receipts had posted.
  expect(await balance(page)).toBe(cents(owed - 160));
  await scanTenant(page);

  await admin.goto('/operations');
  for (const [made, how] of [
    [whole, 'in full'],
    [part, 'in part'],
  ] as const) {
    const review = staffPayment(admin, made.id);
    await expect(review).toContainText(
      'Test payment needs review — original receipt remains recorded',
    );
    // The sandbox's own text: a refund is not a bank's return, and is corrected by hand.
    await expect(review).toContainText(
      'The evidence held so far cannot be posted as a return. If the bank returned an ACH payment, leave this review open',
    );
    await expect(review).toContainText(
      'For a refund or a card dispute, correct the ledger, then close the review.',
    );
    await expect(review).toContainText('Reason: return_requires_review');
    await expect(review).toContainText('Receipt entry: ');
    await expect(review).not.toContainText('Reversal entry: ');
    await expect(review.getByRole('button', { name: 'Post return' })).toHaveCount(0);
    await expect(review.getByRole('button')).toHaveCount(1);
    if (how === 'in full') await scanStaff(admin);

    const note = `Refunded ${how} at the processor; ledger corrected ${run}`;
    await closeReview(admin, review, review.getByRole('button', { name: 'Close review…' }), note);
    await expect(review).toContainText(
      'Test payment review closed — original receipt remains recorded',
    );
    await expect(review.getByRole('button')).toHaveCount(0);
  }
  // The payout that receipted them is as it was.
  await expect(staffPayout(admin, payout.id).getByText('Posted', { exact: true })).toBeVisible();
  await scanStaff(admin);
  await staff.close();

  for (const mine of [whole.mine, part.mine])
    await expect(mine).toContainText(
      'Test payment review closed — original receipt remains recorded',
    );
  expect(await balance(page)).toBe(cents(owed - 160));

  await nothingUnexpected();
});

/**
 * A payout Stripe marked paid and later says failed: once while it still waits for the bank, and
 * once after it has posted. Either goes to a person and can only be closed. Nothing is reversed, so
 * the screen says which of the two it is. No payout has failed in a sandbox, so the stand-in builds
 * `payout.failed` from the payout's own paid event, as the adapter's tests do.
 */
test('a payout that fails goes to staff review whether or not it had posted, and nothing is reversed @scenario:stripe-stand-in-failed-payout', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const run = randomBytes(4).toString('hex');
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');

  await signIn(page, { email: 'tenant-a1@payments.test', password: PASSWORD });
  const owed = await balance(page);
  const first = await pay(
    page,
    '100.00',
    'card',
    'Convenience fee: $3.30 · Total charged: $103.30',
  );
  const second = await pay(page, '60.00', 'card', 'Convenience fee: $2.10 · Total charged: $62.10');
  await cardSucceeds(first.id);
  await cardSucceeds(second.id);

  // Two payouts. The first waits for the bank; the second posts.
  const waiting = await paysOut([first.id]);
  const posted = await paysOut([second.id]);
  expect((await bank(0, posted.id, posted.net, today)).said).toContain('The payout is Posted.');
  await expect(second.mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect.poll(() => balance(page)).toBe(cents(owed - 60));

  await admin.goto('/operations');
  const one = staffPayout(admin, waiting.id);
  const two = staffPayout(admin, posted.id);
  await expect(one.getByText('Waiting for the bank', { exact: true })).toBeVisible();
  await expect(two.getByText('Posted', { exact: true })).toBeVisible();

  // Stripe says each failed. The host stores it and asks Stripe nothing.
  for (const payout of [waiting, posted]) {
    const failed = await standIn<{ deliveries: Delivery[] }>(`payouts/${payout.id}/fail`, {});
    expect(failed.deliveries.map((sent) => [sent.type, sent.status])).toEqual([
      ['payout.failed', 204],
    ]);
  }

  await readAgain(admin);
  // The one that had not posted: nothing posted, and nothing to post.
  await expect(one.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
  await expect(one).toContainText('The processor reported that this payout failed on ');
  await expect(one).toContainText(
    'The processor reported that this payout failed. The payout was not posted, and nothing was reversed. Review it, then close the review.',
  );
  await expect(one).toContainText('Reason: payout_failed');
  await expect(one).toContainText('No bank evidence has arrived for this payout.');
  await expect(one.getByRole('row').nth(1)).toContainText('Not on the ledger');
  await expect(one.getByRole('button', { name: `Post payout ${waiting.id}` })).toHaveCount(0);
  await expect(one.getByRole('button')).toHaveCount(1);

  // The one that had posted: its receipt is still on the ledger, and the screen says so.
  await expect(
    two.getByText('Needs review — posted, nothing reversed', { exact: true }),
  ).toBeVisible();
  await expect(two).toContainText(
    'The processor reported that this payout failed after it was posted. Nothing was reversed automatically, so the ledger still shows it. Correct the ledger, then close the review.',
  );
  await expect(two).toContainText('Reason: payout_failed');
  await expect(two).toContainText(`$60.00 at the bank on ${today}`);
  await expect(two.getByRole('row').nth(1)).toContainText('On the ledger');
  await expect(two.getByRole('button', { name: `Post payout ${posted.id}` })).toHaveCount(0);
  await expect(two.getByRole('button')).toHaveCount(1);
  await scanStaff(admin);

  // The tenant's payments are where they were: one in transit, one recorded.
  await expect(first.mine).toContainText('on its way to the bank, not yet on your ledger');
  await expect(second.mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  expect(await balance(page)).toBe(cents(owed - 60));

  // A bank's evidence for a payout that failed is kept and releases nothing.
  const late = await bank(0, waiting.id, waiting.net, today);
  expect(late.said).toContain('The payout is NeedsReview (payout_failed).');
  await readAgain(admin);
  await expect(one).toContainText(`$100.00 at the bank on ${today}`);
  await expect(one.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
  await expect(one.getByRole('button', { name: `Post payout ${waiting.id}` })).toHaveCount(0);

  await closeReview(
    admin,
    one,
    one.getByRole('button', { name: `Close review of payout ${waiting.id}` }),
    `Payout failed before posting; processor to pay again ${run}`,
  );
  await expect(one.getByText('Closed — nothing posted', { exact: true })).toBeVisible();
  await closeReview(
    admin,
    two,
    two.getByRole('button', { name: `Close review of payout ${posted.id}` }),
    `Payout failed after posting; ledger corrected ${run}`,
  );
  await expect(two.getByText('Closed — posted, nothing reversed', { exact: true })).toBeVisible();
  await expect(two.getByRole('row').nth(1)).toContainText('On the ledger');
  await expect(one.getByRole('button')).toHaveCount(0);
  await expect(two.getByRole('button')).toHaveCount(0);
  await scanStaff(admin);
  await staff.close();

  await expect(first.mine).toContainText('on its way to the bank, not yet on your ledger');
  expect(await balance(page)).toBe(cents(owed - 60));

  await nothingUnexpected();
});

/**
 * The bank receives an amount that is not Stripe's net. The payout goes to a person and cannot be
 * posted, whatever its lines say. A later amount that does equal the net is refused as different
 * evidence and makes the payout conflicting. An administrator can only close it.
 */
test('a bank amount that differs from the processor’s net posts nothing, and an administrator can only close the payout @scenario:stripe-stand-in-bank-amount-differs', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const run = randomBytes(4).toString('hex');
  const note = `Bank short by one cent; processor asked ${run}`;
  const { staff, admin } = await administrator(browser, 'a');
  await chargeStripesFee(admin, 'card');

  await signIn(page, { email: 'tenant-a2@payments.test', password: PASSWORD });
  const owed = await balance(page);
  const receiptsBefore = await receipts(page, '$100.00').count();
  const payment = await pay(
    page,
    '100.00',
    'card',
    'Convenience fee: $3.30 · Total charged: $103.30',
  );
  await cardSucceeds(payment.id);
  const payout = await paysOut([payment.id]);
  expect(payout.net).toBe('100.00');

  await admin.goto('/operations');
  const held = staffPayout(admin, payout.id);
  await expect(held.getByText('Waiting for the bank', { exact: true })).toBeVisible();

  // The bank received a cent less than Stripe says it paid.
  const short = await bank(0, payout.id, '99.99', today);
  expect(short.refused, short.said).toBe(false);
  expect(short.said).toContain(`Payout ${payout.id}: bank amount 99.99 on ${today} recorded.`);
  expect(short.said).toContain('The payout is NeedsReview (bank_amount_mismatch).');
  await readAgain(admin);

  await expect(held.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
  await expect(held).toContainText(`$99.99 at the bank on ${today}`);
  await expect(held).toContainText('Processor’s payout amount: $100.00');
  await expect(held).toContainText(
    'The bank received a different amount from the one the processor says it paid, so nothing was posted. Review it, then close the review.',
  );
  await expect(held).toContainText('Reason: bank_amount_mismatch');
  await expect(held.getByRole('row').nth(1)).toContainText('Not on the ledger');
  await expect(held.getByRole('button', { name: `Post payout ${payout.id}` })).toHaveCount(0);
  await expect(held.getByRole('button')).toHaveCount(1);
  await scanStaff(admin);

  // The right amount afterwards is other evidence for the same payout. It is refused and not kept.
  const right = await bank(0, payout.id, payout.net, today);
  expect(right.refused, right.said).toBe(true);
  expect(right.said).toContain(
    `payout ${payout.id} already has other bank evidence; this was not recorded.`,
  );
  expect(right.said).toContain('The payout is NeedsReview (conflicting_evidence).');
  await readAgain(admin);
  await expect(held).toContainText('Reason: conflicting_evidence');
  await expect(held).toContainText(
    'The evidence for this payout conflicts with what is already recorded, so nothing was posted. Review it, then close the review.',
  );
  await expect(held).toContainText(`$99.99 at the bank on ${today}`);
  await expect(held.getByRole('button', { name: `Post payout ${payout.id}` })).toHaveCount(0);

  await closeReview(
    admin,
    held,
    held.getByRole('button', { name: `Close review of payout ${payout.id}` }),
    note,
  );
  await expect(held.getByText('Closed — nothing posted', { exact: true })).toBeVisible();
  await expect(held.getByRole('row').nth(1)).toContainText('Not on the ledger');
  await expect(held.getByRole('button')).toHaveCount(0);
  // Focus lands on the payout's name, so a keyboard user stays on the payout they closed.
  await expect(held.getByText(`Payout ${payout.id}`, { exact: true })).toBeFocused();
  await scanStaff(admin);
  await staff.close();

  // Nothing posted: the payment is still in transit, and the tenant owes what they owed.
  await expect(payment.mine).toContainText('on its way to the bank, not yet on your ledger');
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);

  await nothingUnexpected();
});

/**
 * The one payout a sandbox has really made: $1,470.60, with the fourteen balance lines Stripe
 * listed for it. The stand-in serves it as recorded, on the fixture's account and under a payout id
 * of its own for this run. Its lines name the payments of three earlier fixture generations and
 * none of this one's, and two of them are failed bank debits, which nothing posts. So it goes to a
 * person the moment it arrives, bank evidence equal to the net releases nothing, and an
 * administrator who posts it is refused.
 */
test('the recorded payout arrives with its fifteen lines, goes to staff review, and cannot be posted @scenario:stripe-stand-in-recorded-payout', async ({
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const run = randomBytes(4).toString('hex');
  const { staff, admin } = await administrator(browser, 'a');

  const payout = await standIn<Payout & { lines: number }>('payouts/recorded', {
    account: fixture(0).Account,
  });
  expect(payout.deliveries.map((sent) => [sent.type, sent.status])).toEqual(PAYOUT_EVENTS);
  expect([payout.net, payout.lines]).toEqual(['1470.60', 14]);
  await admin.goto('/operations');
  await expect(admin.getByText('Test payment — no real money moves')).toBeVisible();

  const held = staffPayout(admin, payout.id);
  await expect(held.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
  await expect(held).toContainText('No bank evidence has arrived for this payout.');
  await expect(held).toContainText('Processor’s payout amount: $1,470.60');
  await expect(held).toContainText(
    'This payout contains something LeaseBook does not post, such as a refund, a dispute, a partial return or a reserve. Correct the ledger, then close the review.',
  );
  await expect(held).toContainText('Reason: unsupported_settlement');
  // Thirteen lines beside the payout's own, and two of them a return with Stripe's fee apart.
  const lines = held.getByRole('row');
  await expect(lines).toHaveCount(16);
  const kind = (name: string) => held.getByRole('cell', { name, exact: true });
  await expect(kind('Payment')).toHaveCount(9);
  await expect(kind('Returned payment')).toHaveCount(2);
  await expect(kind('Processor fee')).toHaveCount(2);
  await expect(kind('Failed bank debit')).toHaveCount(2);
  await expect(kind('Not on the ledger')).toHaveCount(15);
  // One line of each kind, with Stripe's own figures.
  const line = (id: string) =>
    lines.filter({ has: admin.getByRole('cell', { name: id, exact: true }) });
  const figures = async (id: string) =>
    (await line(id).getByRole('cell').allInnerTexts()).slice(1, 5);
  expect(await figures('txn_synthetic000011')).toEqual(['Payment', '$504.03', '$4.03', '$500.00']);
  expect(await figures('txn_synthetic000012')).toEqual(['Payment', '$200.00', '$6.10', '$193.90']);
  expect(await figures('txn_synthetic000007')).toEqual([
    'Returned payment',
    '$200.00',
    '—',
    '−$200.00',
  ]);
  expect(await figures('txn_synthetic000007.fee')).toEqual([
    'Processor fee',
    '—',
    '$15.00',
    '−$15.00',
  ]);
  expect(await figures('txn_synthetic000009')).toEqual([
    'Failed bank debit',
    '−$200.00',
    '$2.40',
    '−$202.40',
  ]);
  // No bank has spoken, so it cannot be posted; it can be closed.
  await expect(held.getByRole('button', { name: `Post payout ${payout.id}` })).toHaveCount(0);
  await expect(held.getByRole('button')).toHaveCount(1);
  await scanStaff(admin);

  // What the bank received, to the cent, releases nothing.
  const said = await bank(0, payout.id, payout.net, today);
  expect(said.refused, said.said).toBe(false);
  expect(said.said).toContain('The payout is NeedsReview (unsupported_settlement).');
  await readAgain(admin);
  await expect(held).toContainText(`$1,470.60 at the bank on ${today}`);
  await expect(held.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();

  // With bank evidence an administrator may ask for it to be posted, and is refused.
  const refused = admin.waitForResponse(
    (r) =>
      /\/api\/payments\/settlements\/[^/]+\/post$/.test(r.url()) && r.request().method() === 'POST',
  );
  await held.getByRole('button', { name: `Post payout ${payout.id}` }).focus();
  await admin.keyboard.press('Enter');
  expect((await refused).status()).toBe(409);
  await expect(held.getByRole('alert')).toContainText(
    'This payout contains something LeaseBook does not post',
  );
  await expect(held.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
  await expect(kind('Not on the ledger')).toHaveCount(15);
  await scanStaff(admin);

  await closeReview(
    admin,
    held,
    held.getByRole('button', { name: `Close review of payout ${payout.id}` }),
    `Recorded sandbox payout; none of its payments is this fixture’s ${run}`,
  );
  await expect(held.getByText('Closed — nothing posted', { exact: true })).toBeVisible();
  await expect(kind('Not on the ledger')).toHaveCount(15);
  await expect(held.getByRole('button')).toHaveCount(0);
  await staff.close();

  await nothingUnexpected();
});

/**
 * The known limit, as an expected failure (ADR-054, Known limits: "A payout that holds both a
 * payment and that payment's return cannot post"). Stripe's ACH dispute test method disputes a
 * debit minutes after it succeeds, so in a sandbox the debit and its return usually reach the same
 * payout. This test asserts what should then happen: bank evidence equal to the net lets the payout
 * post, with the receipt and its reversal. Today the payment line is refused because a return is
 * stored for the payment, and the return line because the payment has no receipt to reverse, so
 * the payout cannot post and the test fails where it states that outcome.
 *
 * How to tell that failure from any other. Everything up to `test.fail()` is setup and is held to
 * pass: the test is marked as expected to fail only once the setup and today's refusal have been
 * seen, so a failure before that point is reported as a failure, never as the expected one. The
 * invariant check and the stand-in's list of unexpected requests run before that point too, for
 * the same reason; the `afterEach` check runs again afterwards. A report that says the test failed
 * as expected therefore failed at the intended outcome, in the step named for it.
 *
 * When a fix lands, either today's refusal changes, and the step that records it fails as an
 * ordinary failure, or the intended outcome holds and Playwright reports that a test expected to
 * fail passed. Either turns the run red. Then remove `test.fail()`, the step recording today's
 * refusal, and `expectedFailure` and the `@expected-failure` tag from the scenario catalogue.
 *
 * It runs on the second fixture organization, whose ACH rule charges a fixed $20.00 beside Stripe's
 * 0.8%. The payout then carries a fee surplus of $40.00 that covers what the return costs the
 * manager, the fee quoted on the returned debit and Stripe's $15.00 for the dispute, so no shortage
 * of held fees can stop a fixed engine from posting it.
 */
test('a payout that holds an ACH debit and that debit’s return posts both when the bank evidence equals the net @scenario:stripe-stand-in-payment-and-return-in-one-payout @expected-failure', async ({
  page,
  browser,
}) => {
  enabled();
  const today = new Date().toISOString().slice(0, 10);
  const { staff, admin } = await administrator(browser, 'b');
  await setFeeRule(admin, STRIPE_FEES.ach.group, STRIPE_FEES.ach.rate, '20.00');

  await signIn(page, { email: 'tenant-b1@payments.test', password: PASSWORD });
  const owed = await balance(page);
  // $120.97 is the least charge that leaves $100.00 after 0.8% of it and $20.00. Stripe keeps
  // $0.97 of it, so $120.00 of each debit reaches the payout.
  const quote = 'Convenience fee: $20.97 · Total charged: $120.97';
  const returned = await pay(page, '100.00', 'ach', quote);
  const kept = await pay(page, '100.00', 'ach', quote);
  for (const payment of [returned, kept])
    expect(await happens(payment.id, 'ach-succeeds')).toHaveLength(8);

  // The bank takes the first debit back. Stripe reports it as a dispute already lost, and the host
  // reads it as a return. The payment has no receipt yet, so nothing can be reversed.
  expect(await happens(returned.id, 'ach-disputed')).toEqual([
    ['charge.dispute.created', 204],
    ['charge.dispute.funds_withdrawn', 204],
    ['charge.dispute.closed', 204],
  ]);
  await expect(returned.mine).toContainText('Test payment needs review — no receipt recorded');
  await expect(kept.mine).toContainText('on its way to the bank, not yet on your ledger');

  // One payout: both debits, the first one's return, and Stripe's fee for the dispute.
  // 120.00 + 120.00 − 120.97 − 15.00 = 104.03.
  const payout = await paysOut([returned.id, kept.id], [returned.id]);
  expect(payout.net).toBe('104.03');
  await admin.goto('/operations');
  const held = staffPayout(admin, payout.id);
  const kind = (name: string) => held.getByRole('cell', { name, exact: true });
  await expect(held.getByRole('row')).toHaveCount(5);
  await expect(kind('Payment')).toHaveCount(2);
  await expect(kind('Returned payment')).toHaveCount(1);
  await expect(kind('Processor fee')).toHaveCount(1);
  await expect(held).toContainText('Processor’s payout amount: $104.03');

  // Today, the known limit. Whichever of its two lines is checked first is refused: the payment
  // because a return is stored for it, or the return because no receipt exists to reverse. If this
  // step fails, the limit may have been fixed; see the comment above.
  await test.step('today: the payout goes to review and cannot post (ADR-054 known limit)', async () => {
    await expect(held.getByText('Needs review — nothing posted', { exact: true })).toBeVisible();
    await expect(held).toContainText(/Reason: (conflicting_evidence|settlement_incomplete)/);
    await expect(kind('Not on the ledger')).toHaveCount(4);
  });
  await scanStaff(admin);

  const said = await bank(1, payout.id, payout.net, today);
  expect(said.refused, said.said).toBe(false);
  expect(said.said).toContain(`Payout ${payout.id}: bank amount 104.03 on ${today} recorded.`);
  await readAgain(admin);
  await expect(held).toContainText(`$104.03 at the bank on ${today}`);

  await invariantsHold();
  await nothingUnexpected();

  // Everything above passed. From here the test states the intended outcome, and fails today.
  test.fail(
    true,
    'ADR-054 Known limits: a payout that holds both a payment and that payment’s return cannot post.',
  );
  await test.step('intended: the payout posts, with the receipt and its reversal', async () => {
    // A payout that holds a return waits for an administrator, who posts it (ADR-053). A fix may
    // instead post it on the bank's word alone; either way it ends posted.
    const post = held.getByRole('button', { name: `Post payout ${payout.id}` });
    if (await post.isVisible()) {
      await post.focus();
      await admin.keyboard.press('Enter');
    }
    await expect(held.getByText('Posted', { exact: true })).toBeVisible();
    await expect(kind('Not on the ledger')).toHaveCount(0);
    await staff.close();
    await expect(returned.mine).toContainText(
      'Test payment returned by the bank — the receipt was reversed',
    );
    await expect(kept.mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
    await expect.poll(() => balance(page)).toBe(cents(owed - 100));
  });
});
