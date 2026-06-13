import { expect, test, type Page } from '@playwright/test';

// The M3 ledger-hub budgeted flows (§D step 6), run against the seeded demo org. The seeded admin
// (Renée Calloway) has no MFA, so login is email + password. Each spec mutates only with entries it
// then voids back to baseline (or an apply the engine rejects), so the demo org's golden figures stay
// reproducible (M3-E9).
const ADMIN = 'renee.calloway@tarheelpg.test';
const PASSWORD = 'Tarheel-Trust-2026!';
// A small per-run amount unlikely to collide with seeded figures (or with a prior run's leftover rows).
const UNIQUE_AMOUNT = (12 + Math.floor(Math.random() * 90) / 100).toFixed(2);

async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/dashboard/);
}

async function openTenantLedger(page: Page) {
  await page.getByRole('button', { name: 'Tenants' }).click();
  await expect(page).toHaveURL(/\/tenants$/);
  await page.getByText('Jasmine Carter').click();
  await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
}

test('records a payment in ≤ 3 interactions, then voids it with a linked reversal and audit trail', async ({
  page,
}) => {
  await login(page);
  await openTenantLedger(page);

  // Record a payment: open (1) → type the autofocused amount → Enter (2). The budget telemetry fires.
  const budget = page.waitForRequest(
    (request) => request.url().includes('/api/telemetry/budget') && request.method() === 'POST',
  );
  await page.getByRole('button', { name: 'Record payment' }).click();
  await page.getByLabel('Amount').fill(UNIQUE_AMOUNT);
  await page.getByLabel('Amount').press('Enter');

  const event = JSON.parse((await budget).postData() ?? '{}');
  expect(event.task).toBe('record-payment');
  expect(event.met).toBe(true);
  expect(event.interactions).toBeLessThanOrEqual(3);

  // The new row appears without navigation.
  const paymentRow = page.getByRole('row').filter({ hasText: `$${UNIQUE_AMOUNT}` });
  await expect(paymentRow).toBeVisible();
  await page.screenshot({ path: 'e2e-results/m3-payment-posted.png', fullPage: true });

  // The audit drawer shows the acting user.
  await paymentRow.getByRole('button', { name: 'History' }).click();
  const history = page.getByRole('dialog', { name: 'History' });
  await expect(history).toContainText('Renée');
  await history.getByRole('button', { name: 'Close' }).click();

  // Void it → a linked reversal renders, the original is marked voided (back to baseline).
  await paymentRow.getByRole('button', { name: 'Void entry' }).click();
  const voidDialog = page.getByRole('dialog', { name: 'Void entry' });
  await voidDialog.getByLabel('Reason').fill('e2e cleanup');
  await voidDialog.getByRole('button', { name: 'Void entry' }).click();

  // This run's reversal (scoped by the unique amount, since prior runs may have left reversal rows).
  await expect(
    page.getByRole('row').filter({ hasText: `$${UNIQUE_AMOUNT}` }).filter({ hasText: 'Reversal' }),
  ).toBeVisible();
  // The original payment row (category "Payment", to exclude the "EntryVoided" reversal) now reads Voided.
  await expect(
    page.getByRole('row').filter({ hasText: `$${UNIQUE_AMOUNT}` }).filter({ hasText: 'Payment' }),
  ).toContainText('Voided');
});

test('an over-application of held funds is blocked with a warning and the modal stays open', async ({ page }) => {
  await login(page);
  await openTenantLedger(page);

  // Apply far more than is held/owed → the engine rejects it; the warning shows in place (no posting).
  await page.getByRole('button', { name: 'Apply…' }).click();
  const apply = page.getByRole('dialog', { name: 'Apply held funds' });
  await apply.getByLabel('Amount').fill('999999');
  await apply.getByRole('button', { name: 'Apply', exact: true }).click();

  await expect(apply.getByRole('alert')).toBeVisible();
  await expect(apply).toBeVisible(); // stays open so the user can lower the amount
  await page.screenshot({ path: 'e2e-results/m3-apply-warn.png', fullPage: true });
  await apply.getByRole('button', { name: 'Cancel' }).click();
});
