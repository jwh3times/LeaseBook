import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { type APIRequestContext, expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { runA11y, signIn } from './helpers';

interface Manifest {
  SigningKey: string;
  Fixtures: { Generation: string; Account: string; BankId: string }[];
}

const PASSWORD = 'Payment-Fixture-2026!';

function fixture() {
  const manifest = (
    JSON.parse(readFileSync(process.env.Payments__ManifestPath!, 'utf8')) as { Payments: Manifest }
  ).Payments;
  const binding = manifest.Fixtures[0];
  if (!binding) throw new Error('Payment fixture binding missing');
  return { manifest, binding };
}

// Signed as the fixture processor signs: the timestamp, a dot, and the exact body bytes.
async function deliver(request: APIRequestContext, key: string, path: string, body: string) {
  const timestamp = Math.floor(Date.now() / 1000).toString();
  const signature = createHmac('sha256', key).update(`${timestamp}.${body}`).digest('hex');
  const response = await request.post(`http://localhost:5080${path}`, {
    data: body,
    headers: {
      'X-Simulation-Signature': `${timestamp}.${signature}`,
      'Content-Type': 'application/json',
    },
  });
  expect(response.status()).toBe(204);
}

/**
 * Fees and payouts (ADR-053): the tenant confirms a quoted fee; one payout carries two payments, one
 * with a surplus and one with a shortfall the surplus covers, and posts by itself; a payout with a
 * return waits for an administrator, who posts it from the keyboard.
 */
test('a quoted fee, a payout with a shortfall, and a payout with a return an administrator posts', async ({
  page,
  request,
  browser,
}) => {
  test.skip(
    process.env.PAYMENT_SIMULATION_E2E !== '1',
    'Requires the isolated payment fixture database.',
  );
  const { manifest, binding } = fixture();
  const generationN = binding.Generation.replaceAll('-', '');
  const today = new Date().toISOString().slice(0, 10);
  const run = Date.now().toString(36);

  // An administrator sets the card rule on the settings screen: 2.9% plus $0.30, no cap.
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
  await runA11y(admin);

  // The tenant is quoted the fee before confirming, and pays it on top of the ledger amount.
  await signIn(page, { email: 'tenant-a2@payments.test', password: PASSWORD });
  await expect(page.getByText('Simulation — no real money moves')).toBeVisible();
  const pay = async (amount: string, quote: string) => {
    await page.getByLabel('Amount (USD)').fill(amount);
    await page.getByLabel('Pay with').selectOption('card');
    await expect(page.getByText(quote)).toBeVisible();
    const submitted = page.waitForResponse(
      (r) => r.url().endsWith('/api/portal/tenant/payments') && r.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Submit simulated payment' }).click();
    const payment = (await (await submitted).json()) as { id: string };
    await expect(page.getByText(`Payment reference: ${payment.id}`)).toBeVisible();
    return payment.id;
  };
  const first = await pay('100.00', 'Convenience fee: $3.30 · Total charged: $103.30');
  const second = await pay('50.00', 'Convenience fee: $1.80 · Total charged: $51.80');
  // Each payment is found by its own reference, so earlier payments on the fixture do not matter.
  const mine = (id: string) => page.getByRole('listitem').filter({ hasText: id });
  await expect(mine(first)).toContainText('Plus a $3.30 convenience fee: $103.30 charged');
  // A payout can name a payment only once the processor has accepted it.
  for (const id of [first, second])
    await expect(mine(id)).toContainText('Simulated payment processing — not yet on your ledger');

  const line = (
    item: string,
    kind: string,
    id: string,
    gross: number,
    fee: number,
    net: number,
  ) => ({
    Item: item,
    Kind: kind,
    ProviderId: `sim_${generationN}_${id.replaceAll('-', '')}`,
    Gross: gross,
    Fee: fee,
    Net: net,
    Currency: 'USD',
  });
  const payout = (payoutId: string, bankAmount: number, items: ReturnType<typeof line>[]) =>
    deliver(
      request,
      manifest.SigningKey,
      '/callbacks/payments/simulation/payout',
      JSON.stringify({
        PayoutId: payoutId,
        Account: binding.Account,
        Mode: 'Simulation',
        Generation: binding.Generation,
        PayoutType: 'standard',
        BankAmount: bankAmount,
        Currency: 'USD',
        BankId: binding.BankId,
        BankDate: today,
        EvidenceId: `bank-${payoutId}`,
        ObservedAt: new Date().toISOString(),
        Items: items,
      }),
    );

  // One deposit of $150.50 for both payments. The processor kept $2.30 from each: $1.00 less than
  // the first quote (a surplus) and $0.50 more than the second (a shortfall the surplus covers).
  const clean = `po_${run}_1`;
  await payout(clean, 150.5, [
    line('1', 'Payment', first, 103.3, 2.3, 101),
    line('2', 'Payment', second, 51.8, 2.3, 49.5),
  ]);

  await admin.goto('/operations');
  const posted = admin.getByRole('listitem', { name: `Payout ${clean}` });
  await expect(posted.getByText('Posted', { exact: true })).toBeVisible();
  await expect(posted).toContainText('$150.50 at the bank');
  await expect(posted.getByRole('row').nth(1)).toContainText(
    'On the ledger, with a fee difference',
  );
  await expect(posted.getByRole('row').nth(2)).toContainText(
    'On the ledger, with a fee difference',
  );
  await expect(posted.getByRole('button')).toHaveCount(0);
  // The tenant is credited what they paid toward the ledger, whatever the processor kept.
  for (const id of [first, second])
    await expect(mine(id).getByText('Simulated payment recorded', { exact: true })).toBeVisible();

  // The bank takes the first payment back, giving back the whole fee. A payout with a return posts
  // nothing by itself: it waits for an administrator.
  const returned = `po_${run}_2`;
  await payout(returned, -100, [line('1', 'Return', first, 103.3, 3.3, -100)]);
  const waiting = admin.getByRole('listitem', { name: `Payout ${returned}` });
  await expect(waiting.getByText('Needs review — nothing posted')).toBeVisible();
  await expect(waiting).toContainText('This payout contains a returned payment');
  await expect(waiting.getByRole('row').nth(1)).toContainText('Not on the ledger');
  await expect(mine(first).getByText('Simulated payment recorded', { exact: true })).toBeVisible();

  // The payout list with a posted payout and a waiting one, actions showing. `nested-interactive` is
  // the Operations page's documented deferral for the disbursement rows (see a11y.spec.ts).
  await runA11y(admin, { disableRules: ['nested-interactive'] });

  await waiting.getByRole('button', { name: `Post payout ${returned}` }).focus();
  await admin.keyboard.press('Enter');
  await expect(waiting.getByText('Posted', { exact: true })).toBeVisible();
  await expect(waiting.getByRole('row').nth(1)).toContainText('On the ledger');
  await expect(waiting.getByRole('button')).toHaveCount(0);
  await staff.close();

  await expect(mine(first)).toContainText(
    'Simulated payment returned by the bank — the receipt was reversed',
  );
  await expect(mine(second).getByText('Simulated payment recorded', { exact: true })).toBeVisible();
});

test('simulation survives reload and posts only after separate bank evidence', async ({
  page,
  request,
  browser,
}) => {
  test.skip(
    process.env.PAYMENT_SIMULATION_E2E !== '1',
    'Requires the isolated payment fixture database.',
  );
  const manifest = (
    JSON.parse(readFileSync(process.env.Payments__ManifestPath!, 'utf8')) as {
      Payments: {
        SigningKey: string;
        Fixtures: { Generation: string; Account: string; BankId: string }[];
      };
    }
  ).Payments;
  const binding = manifest.Fixtures[0];
  if (!binding) throw new Error('Payment fixture binding missing');
  await signIn(page, { email: 'tenant-a1@payments.test', password: 'Payment-Fixture-2026!' });
  await expect(page.getByText('Simulation — no real money moves')).toBeVisible();
  await page.getByLabel('Amount (USD)').fill('125.50');
  // The fee is quoted before the tenant can confirm. This fixture's rules charge nothing.
  await expect(page.getByText('No convenience fee · Total charged: $125.50')).toBeVisible();
  const submitted = page.waitForResponse(
    (r) => r.url().endsWith('/api/portal/tenant/payments') && r.request().method() === 'POST',
  );
  await page.getByRole('button', { name: 'Submit simulated payment' }).focus();
  await page.keyboard.press('Enter');
  const operation = (await (await submitted).json()) as { id: string };
  await page.reload();
  await expect(page.getByText(`Payment reference: ${operation.id}`)).toBeVisible();
  await expect(
    page.getByText('Simulated payment processing — not yet on your ledger'),
  ).toBeVisible();
  const operationN = operation.id.replaceAll('-', '');
  const generationN = binding.Generation.replaceAll('-', '');
  const emit = async (kind: string) => {
    const body = JSON.stringify({
      EventId: `${operationN}:${kind}`,
      ProviderId: `sim_${generationN}_${operationN}`,
      Account: binding.Account,
      Mode: 'Simulation',
      Generation: binding.Generation,
      Kind: kind,
      Gross: 125.5,
      Fee: 0,
      Net: 125.5,
      Currency: 'USD',
      BankId: binding.BankId,
      BankDate: new Date().toISOString().slice(0, 10),
      EvidenceId: `bank-${operationN}`,
      PayoutId: `payout-${operationN}`,
      Complete: true,
      ObservedAt: new Date().toISOString(),
    });
    const timestamp = Math.floor(Date.now() / 1000).toString();
    const signature = createHmac('sha256', manifest.SigningKey)
      .update(`${timestamp}.${body}`)
      .digest('hex');
    const response = await request.post('http://localhost:5080/callbacks/payments/simulation', {
      data: body,
      headers: {
        'X-Simulation-Signature': `${timestamp}.${signature}`,
        'Content-Type': 'application/json',
      },
    });
    expect(response.status()).toBe(204);
  };
  await emit('PayoutPaid');
  await expect(page.getByText('Simulated payment recorded', { exact: true })).toHaveCount(0);
  await emit('BankCredit');
  await expect(page.getByText('Simulated payment recorded', { exact: true })).toBeVisible();
  await expect(page.getByRole('row', { name: /Payment Posted/ })).toContainText('$125.50');
  const violations = (await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa']).analyze())
    .violations;
  expect(violations).toEqual([]);
  await emit('Return');
  await expect(
    page.getByText('Simulated payment needs review — original receipt remains recorded'),
  ).toBeVisible();

  // Return evidence posts nothing by itself. An administrator posts the return (#490); the tenant
  // then sees the payment as returned and the rent owed again.
  const staff = await browser.newContext();
  const admin = await staff.newPage();
  await signIn(admin, { email: 'admin-a@payments.test', password: 'Payment-Fixture-2026!' });
  await admin.goto('/operations');
  const payment = admin.getByRole('listitem').filter({ hasText: operation.id });
  await payment.getByRole('button', { name: 'Post return', exact: true }).click();
  await expect(
    payment.getByText('Simulated payment returned by the bank — the receipt was reversed'),
  ).toBeVisible();
  await expect(payment.getByText(/^Reversal entry: /)).toBeVisible();
  await staff.close();

  await expect(
    page.getByText('Simulated payment returned by the bank — the receipt was reversed'),
  ).toBeVisible();
});
