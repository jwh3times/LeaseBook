import { expect, test, type Page } from '@playwright/test';
import { visualSnapshot } from './helpers';

// The M4 banking budgeted flows (§D step 5), run against the seeded demo org. The seeded admin (Renée
// Calloway) has no MFA, so login is email + password. These two specs are SERIAL and designed to run
// against a FRESHLY SEEDED org: finalizing a reconciliation is a per-account-month lock that only a
// PMAdmin unlock can release, so the flow is intentionally single-run per seed (the §D gate runs
// `reset-db` + `seed --org demo` before `npm run e2e`). The import spec runs first; the reconcile spec
// finalizes whatever remains uncleared, so it is robust to the import spec having cleared a line.
const ADMIN = 'renee.calloway@tarheelpg.test';
const PASSWORD = 'Tarheel-Trust-2026!';

// A statement line that exactly matches an uncleared register line on the seeded Operating Trust: Devon
// Pryor's June rent deposit (+1,380 on 2026-06-03, one of the seed's three uncleared items, P72).
const PRYOR_CSV = 'Date,Description,Amount\n2026-06-03,ACH deposit Pryor,1380.00\n';

async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/dashboard/);
}

async function gotoBanking(page: Page) {
  await page.getByRole('button', { name: 'Banking' }).click();
  await expect(page).toHaveURL(/\/banking$/);
  // Operating Trust is the first (alphabetical) account tab and the default selection.
  await expect(page.getByRole('button', { name: /Operating Trust/ })).toBeVisible();
}

async function mapAmountColumns(dialog: ReturnType<Page['getByRole']>) {
  await dialog.getByLabel('Date column').selectOption('Date');
  await dialog.getByLabel('Description column').selectOption('Description');
  await dialog.getByLabel('Amount column').selectOption('Amount');
}

test.describe.serial('M4 banking', () => {
  test('imports a CSV, auto-matches + clears a register line, and de-dups on re-import', async ({
    page,
  }) => {
    await login(page);
    await gotoBanking(page);

    // The Pryor deposit starts uncleared.
    const pryorRow = page.getByRole('row').filter({ hasText: 'Pryor' });
    await expect(pryorRow.getByText('Uncleared')).toBeVisible();

    // Import wizard: upload → map → preview (the line auto-matches) → confirm clears it via the port.
    await page.getByRole('button', { name: 'Import statement' }).click();
    const wizard = page.getByRole('dialog', { name: 'Import bank statement' });
    await wizard.getByLabel('Statement CSV').setInputFiles({
      name: 'statement.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(PRYOR_CSV),
    });
    await mapAmountColumns(wizard);
    await wizard.getByRole('button', { name: 'Preview matches' }).click();
    await expect(wizard.getByText('Matched')).toBeVisible();
    await wizard.getByRole('button', { name: /Confirm/ }).click();

    // The matched register line flips to cleared (proving the clearance went through Accounting).
    await expect(pryorRow.getByText('Cleared')).toBeVisible();
    await page.screenshot({ path: 'e2e-results/m4-import-cleared.png', fullPage: true });

    // Re-importing the identical CSV is de-duplicated and reported.
    await page.getByRole('button', { name: 'Import statement' }).click();
    const wizard2 = page.getByRole('dialog', { name: 'Import bank statement' });
    await wizard2.getByLabel('Statement CSV').setInputFiles({
      name: 'statement.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(PRYOR_CSV),
    });
    await mapAmountColumns(wizard2);
    await wizard2.getByRole('button', { name: 'Preview matches' }).click();
    await expect(wizard2.getByText(/duplicate skipped/)).toBeVisible();
    await wizard2.getByRole('button', { name: 'Cancel' }).click();
  });

  test('reconciles Operating Trust to $0.00, finalizes, and locks the month against new postings', async ({
    page,
  }) => {
    await login(page);
    await gotoBanking(page);

    // Reconcile in place: enter (1 click) → tick all uncleared → difference $0.00 → finalize.
    await page.getByRole('button', { name: 'Reconcile account' }).click();
    await page.getByRole('button', { name: 'Select all uncleared' }).click();
    await expect(page.getByLabel('Difference')).toHaveText('$0.00');
    // Visual regression (CI-only): the reconcile bar at difference $0.00, before finalize.
    await visualSnapshot(page.locator('.pf-recon-bar'), 'reconcile-zero-strip.png');
    await page.getByRole('button', { name: 'Finalize' }).click();

    // Back to the balance strip; the reconciliation is recorded as finalized in history.
    await expect(page.getByRole('button', { name: 'Reconcile account' })).toBeVisible();
    await expect(page.getByText('Finalized').first()).toBeVisible();
    await page.screenshot({ path: 'e2e-results/m4-reconciled.png', fullPage: true });

    // A tenant payment dated into the locked month (June 2026, the demo's reconciled period) is rejected in place.
    await page.getByRole('button', { name: 'Tenants' }).click();
    await page.getByText('Jasmine Carter').click();
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();

    await page.getByRole('button', { name: 'Record payment' }).click();
    await page.getByLabel('Amount').fill('123.45');
    // Explicitly date into the locked month (June 2026 — the demo's reconciled period) so this
    // assertion is wall-clock-independent even after June 30 UTC passes.
    await page.getByLabel('Date', { exact: true }).fill('2026-06-15');
    await page.getByRole('button', { name: 'Post payment' }).click();
    await expect(page.getByText(/reconciled and locked/)).toBeVisible();

    // The same payment dated into a clearly-open month (August 2026 — after the locked June and
    // outside the demo's data range) still posts — the lock is account-month-scoped.
    // Both dates are fixed relative to the demo's June-2026 reconciled period, not wall-clock.
    await page.getByLabel('Date', { exact: true }).fill('2026-08-15');
    await page.getByRole('button', { name: 'Post payment' }).click();
    await expect(
      page.getByRole('row').filter({ hasText: '$123.45' }).filter({ hasText: 'Payment' }),
    ).toBeVisible();
  });
});
