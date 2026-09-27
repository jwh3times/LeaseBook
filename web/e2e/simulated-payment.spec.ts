import { createHmac } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { expect, test } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { signIn } from './helpers';

test('simulation survives reload and posts only after separate bank evidence', async ({
  page,
  request,
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
});
