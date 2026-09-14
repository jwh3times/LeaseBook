import { expect, test, type Page } from '@playwright/test';
import type { BudgetTelemetryRequest } from '@/api';
import { seedTheme, visualSnapshot } from './helpers';

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
  // Visual regression (CI-only): the composer open BEFORE the random amount is typed. Mask the Date
  // field — it defaults to today's date (wall-clock), which would otherwise drift the baseline.
  await visualSnapshot(page.locator('.pf-composer'), 'ledger-composer-open.png', {
    mask: [page.locator('.pf-composer-field').filter({ hasText: 'Date' })],
  });
  await page.getByLabel('Amount').fill(UNIQUE_AMOUNT);
  await page.getByLabel('Amount').press('Enter');

  const event = JSON.parse((await budget).postData() ?? '{}') as BudgetTelemetryRequest;
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
    page
      .getByRole('row')
      .filter({ hasText: `$${UNIQUE_AMOUNT}` })
      .filter({ hasText: 'Reversal' }),
  ).toBeVisible();
  // The original payment row (category "Payment", to exclude the "EntryVoided" reversal) now reads Voided.
  await expect(
    page
      .getByRole('row')
      .filter({ hasText: `$${UNIQUE_AMOUNT}` })
      .filter({ hasText: 'Payment' }),
  ).toContainText('Voided');
});

// #377: a payment that lands in a month whose owner statement was already issued shows a non-blocking
// notice — and the record-payment budget is unchanged, because the notice asks for nothing. Setup issues
// Jasmine Carter's owner's (O1) current-month CASH statement through the real deliver API, so the
// default-dated payment (which settles her open receivable, a cash owner-equity line) is covered without
// touching the Date field. Delivering writes only an artifact row, never a journal entry, and the payment
// is voided back to baseline, so the demo org's golden figures stay reproducible.
const DEMO_OWNER_O1 = '01923000-0000-7000-8000-000000000a01';

test('a payment into a month with an issued owner statement shows a notice and stays within 3 interactions', async ({
  page,
}) => {
  await login(page);
  await openTenantLedger(page);

  const amount = (40 + Math.floor(Math.random() * 90) / 100).toFixed(2);
  const budget = page.waitForRequest(
    (request) => request.url().includes('/api/telemetry/budget') && request.method() === 'POST',
  );
  await page.getByRole('button', { name: 'Record payment' }).click();

  // Issue the statement for the month the payment will actually be dated — read from the composer's own
  // Date field, not the runner's clock, so a run straddling midnight cannot split the two. An API call,
  // not a UI interaction, so it does not count toward the budget.
  const paymentDate = await page
    .locator('.pf-composer-field')
    .filter({ hasText: 'Date' })
    .locator('input')
    .inputValue();
  const [year, month] = paymentDate.split('-').map(Number);
  await page.request.get('/api/auth/csrf');
  const xsrf = (await page.context().cookies()).find((c) => c.name === 'XSRF-TOKEN')?.value ?? '';
  const issued = await page.request.post(
    `/api/statements/${DEMO_OWNER_O1}/deliver?year=${year}&month=${month}&basis=cash&toEmail=owner%40e2e.test`,
    { headers: { 'X-XSRF-TOKEN': decodeURIComponent(xsrf) } },
  );
  expect(issued.ok(), await issued.text()).toBe(true);

  await page.getByLabel('Amount').fill(amount);
  await page.getByLabel('Amount').press('Enter');

  const event = JSON.parse((await budget).postData() ?? '{}') as BudgetTelemetryRequest;
  expect(event.task).toBe('record-payment');
  expect(event.met).toBe(true);
  expect(event.interactions).toBeLessThanOrEqual(3);

  const notice = page.getByRole('status').filter({ hasText: 'was already issued' });
  await expect(notice).toContainText(/cash statement for \w+ \d{4} was already issued/);
  await expect(notice).toContainText('prior-period adjustment');

  // Dismissing is optional, and it clears the notice.
  await notice.getByRole('button', { name: 'Dismiss' }).click();
  await expect(notice).toBeHidden();

  // Back to baseline.
  const paymentRow = page
    .getByRole('row')
    .filter({ hasText: `$${amount}` })
    .filter({ hasText: 'Payment' });
  await paymentRow.getByRole('button', { name: 'Void entry' }).click();
  const voidDialog = page.getByRole('dialog', { name: 'Void entry' });
  await voidDialog.getByLabel('Reason').fill('e2e cleanup');
  await voidDialog.getByRole('button', { name: 'Void entry' }).click();
  await expect(paymentRow).toContainText('Voided');
});

// WP-3 (ADR-023's deferred dark coverage): the dark twin of the composer shot above. Deliberately
// stops at "composer open" and never posts — the light test already covers the post/void round trip,
// and repeating the mutation would write a second payment+reversal pair into the demo org for a
// picture we already take. Same Date mask (the field defaults to wall-clock today). The data-theme
// assertion guards the bootstrap-from-CI-actuals flow: a failed seed would bake a light baseline.
test('the ledger composer renders in the dark theme', async ({ page }) => {
  await seedTheme(page, 'dark'); // before login — ThemeProvider reads storage on boot
  await login(page);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await openTenantLedger(page);

  await page.getByRole('button', { name: 'Record payment' }).click();
  const composer = page.locator('.pf-composer');
  await expect(composer).toBeVisible();
  await visualSnapshot(composer, 'ledger-composer-open-dark.png', {
    mask: [page.locator('.pf-composer-field').filter({ hasText: 'Date' })],
  });
});

test('an over-application of held funds is blocked with a warning and the modal stays open', async ({
  page,
}) => {
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
