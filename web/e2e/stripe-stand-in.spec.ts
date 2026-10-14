import { execFile } from 'node:child_process';
import { readFileSync } from 'node:fs';
import { promisify } from 'node:util';
import { type Locator, type Page, expect } from '@playwright/test';
import { runA11y, signIn } from './helpers';
import { test } from './test';

// The Stripe sandbox mode, driven through the tenant and staff screens against a host whose Stripe
// is `scripts/stripe-stand-in.mjs` (ADR-054, #545). Nothing here calls Stripe. The stand-in answers
// the host from the recorded payloads and sends it signed deliveries, as Stripe does; a test tells
// it what happens next, standing in for the passing of time and for Stripe's own schedule.
//
// The database is the stand-in fixture's and is not reset between runs, so a test finds a payment
// and a payout by their own references and reads a balance as a difference.

const PASSWORD = 'Payment-Fixture-2026!';
const STAND_IN_KEY = 'sk_test_leasebook_stand_in';
const run = promisify(execFile);

interface Delivery {
  type: string;
  status: number | null;
}

function enabled() {
  test.skip(
    process.env.STRIPE_STAND_IN_E2E !== '1',
    'Requires the stand-in payment fixture database and the Stripe stand-in.',
  );
}

// The first fixture organization: the one `tenant-a1` and `admin-a` belong to.
function fixtureOrg(): string {
  const manifest = JSON.parse(readFileSync(process.env.Payments__ManifestPath!, 'utf8')) as {
    Payments: { Fixtures: { OrgId: string }[] };
  };
  const binding = manifest.Payments.Fixtures[0];
  if (!binding) throw new Error('Payment fixture binding missing');
  return binding.OrgId;
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
async function cli(...args: string[]): Promise<string> {
  const { stdout } = await run(
    'dotnet',
    [
      'run',
      '--project',
      '../src/LeaseBook.Web',
      '--no-launch-profile',
      '--no-build',
      '--',
      ...args,
    ],
    { env: { ...process.env, ASPNETCORE_ENVIRONMENT: 'Development' }, timeout: 120_000 },
  );
  return stdout;
}

// An axe scan reads colors as they are at that instant, so it waits for any transition to end.
async function settled(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(
        () => document.getAnimations().filter((motion) => motion.playState === 'running').length,
      ),
    )
    .toBe(0);
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

// The ledger rows that are a posted payment of one amount.
function receipts(page: Page, amount: string): Locator {
  return page
    .getByRole('table', { name: 'Rent ledger activity' })
    .getByRole('row')
    .filter({ hasText: 'Payment' })
    .filter({ hasText: 'Posted' })
    .filter({ hasText: amount });
}

// Every organization of the fixture is checked after each scenario, whatever the scenario left.
test.afterEach(async () => {
  if (process.env.STRIPE_STAND_IN_E2E !== '1') return;
  expect(await cli('check-invariants', '--all')).toContain('check-invariants: all clean');
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
  const staff = await browser.newContext();
  const admin = await staff.newPage();
  await signIn(admin, { email: 'admin-a@payments.test', password: PASSWORD });
  await admin.goto('/settings');
  const card = admin.getByRole('group', { name: 'Card' });
  await card.getByLabel('Rate (%)').fill('2.9');
  await card.getByLabel('Fixed amount ($)').fill('0.30');
  await card.getByLabel('Cap ($)').fill('');
  await card.getByLabel('Cap ($)').press('Enter');
  await expect(admin.getByText('Saved', { exact: true })).toBeVisible();

  // The tenant's screen says what kind of payment this is before anything is submitted.
  await signIn(page, { email: 'tenant-a1@payments.test', password: PASSWORD });
  await expect(page.getByRole('heading', { name: 'Test payments' })).toBeVisible();
  await expect(page.getByText('Test payment — no real money moves')).toBeVisible();
  await expect(page.getByText('Simulation — no real money moves')).toHaveCount(0);
  const owed = await balance(page);
  const receiptsBefore = await receipts(page, '$100.00').count();
  await settled(page);
  await runA11y(page);

  await page.getByLabel('Amount (USD)').fill('100.00');
  await page.getByLabel('Pay with').selectOption('card');
  await expect(page.getByText('Convenience fee: $3.30 · Total charged: $103.30')).toBeVisible();
  const submitted = page.waitForResponse(
    (r) => r.url().endsWith('/api/portal/tenant/payments') && r.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Submit test payment' }).focus();
  await page.keyboard.press('Enter');
  const payment = (await (await submitted).json()) as { id: string };
  await expect(page.getByText(`Payment reference: ${payment.id}`)).toBeVisible();
  const mine = page.getByRole('listitem').filter({ hasText: payment.id });
  await expect(mine).toContainText('Plus a $3.30 convenience fee: $103.30 charged');
  // The host has charged it at the stand-in, which has not yet said how it went.
  await expect(mine).toContainText('Test payment processing — not yet on your ledger');

  // Stripe's four events for a card payment that succeeds, in the order it sent them. The host
  // acknowledges all four and reads one.
  const moved = await standIn<{ deliveries: Delivery[] }>(`payments/${payment.id}/timeline`, {
    timeline: 'card-succeeds',
  });
  expect(moved.deliveries.map((sent) => [sent.type, sent.status])).toEqual([
    ['charge.succeeded', 204],
    ['payment_intent.succeeded', 204],
    ['payment_intent.created', 204],
    ['charge.updated', 204],
  ]);
  // Paid is Stripe's word and not a bank's: the payment has a paid date and the ledger has not moved.
  await expect(mine).toContainText(
    `Paid on ${today} — on its way to the bank, not yet on your ledger`,
  );
  await expect(page.getByRole('status').filter({ hasText: 'In transit:' })).toBeVisible();
  expect(await balance(page)).toBe(owed);
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore);

  // Stripe pays out, and says so. The payout is stored with Stripe's lines and held.
  const payout = await standIn<{ id: string; net: string; deliveries: Delivery[] }>('payouts', {
    payments: [payment.id],
  });
  expect(payout.net).toBe('100.00');
  expect(payout.deliveries.map((sent) => [sent.type, sent.status])).toEqual([
    ['payout.created', 204],
    ['payout.reconciliation_completed', 204],
    ['payout.paid', 204],
    ['payout.updated', 204],
  ]);
  await admin.goto('/operations');
  await expect(admin.getByText('Test payment — no real money moves')).toBeVisible();
  const held = admin.getByRole('listitem', { name: `Payout ${payout.id}` });
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
  // The held payout on the staff screen. `nested-interactive` is the Operations page's documented
  // deferral for the disbursement rows (see a11y.spec.ts).
  await settled(admin);
  await runA11y(admin, { disableRules: ['nested-interactive'] });

  // The bank received what Stripe said it paid. Only the fixture command can say so.
  const said = await cli('payment-simulation', 'bank', fixtureOrg(), payout.id, payout.net, today);
  expect(said).toContain(`Payout ${payout.id}: bank amount 100.00 on ${today} recorded.`);
  expect(said).toContain('The payout is Posted.');

  await expect(held.getByText('Posted', { exact: true })).toBeVisible();
  await expect(held).toContainText(`$100.00 at the bank on ${today}`);
  await expect(held.getByRole('row').nth(1)).toContainText('On the ledger');
  await expect(held.getByRole('button')).toHaveCount(0);
  await settled(admin);
  await runA11y(admin, { disableRules: ['nested-interactive'] });
  await staff.close();

  // The tenant has a receipt for what they paid toward the ledger, and owes that much less.
  await expect(mine.getByText('Test payment recorded', { exact: true })).toBeVisible();
  await expect(receipts(page, '$100.00')).toHaveCount(receiptsBefore + 1);
  await expect.poll(() => balance(page)).toBe(Math.round((owed - 100) * 100) / 100);
  await settled(page);
  await runA11y(page);

  // Everything the host asked the stand-in was a request the stand-in knows.
  const state = await standIn<{ unexpected: unknown[] }>('state');
  expect(state.unexpected).toEqual([]);
});
