import { expect, type Page } from '@playwright/test';
import { runA11y, visualSnapshot } from './helpers';
import { test } from './test';

// The M4 banking budgeted flows (§D step 5), run against the seeded demo org. The seeded admin (Renée
// Calloway) has no MFA, so login is email + password. These specs are SERIAL and designed to run
// against a FRESHLY SEEDED org: finalizing a reconciliation is a per-account-month lock that only a
// PMAdmin unlock can release, so the flow is intentionally single-run per seed (the §D gate runs
// `reset-db` + `seed --org demo` before `npm run e2e`). The import spec runs first; the reconcile spec
// finalizes whatever remains uncleared, so it is robust to the import spec having cleared a line. The
// unlock spec ends with the month finalized again, so the specs after this file see it locked.
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
  test('imports a CSV, auto-matches + clears a register line, and de-dups on re-import @scenario:bank-statement-import-and-match', async ({
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

  test('lists the rows a statement import left out, by file line @scenario:bank-statement-import-and-match', async ({
    page,
  }) => {
    await login(page);
    await gotoBanking(page);

    // No row here is readable, so the import stores no line for the specs after this one. The long cell
    // and the row count are what jsdom cannot judge: the reason wraps, and the list scrolls inside the dialog.
    const csv = [
      'Date,Description,Amount',
      `2026-06-03,Unreadable,${'9'.repeat(200)}x`,
      ...Array.from({ length: 40 }, (_, i) => `2026-06-03,No amount ${i},`),
      '',
    ].join('\n');
    await page.getByRole('button', { name: 'Import statement' }).click();
    const wizard = page.getByRole('dialog', { name: 'Import bank statement' });
    await wizard.getByLabel('Statement CSV').setInputFiles({
      name: 'statement.csv',
      mimeType: 'text/csv',
      buffer: Buffer.from(csv),
    });
    await mapAmountColumns(wizard);
    await wizard.getByRole('button', { name: 'Preview matches' }).click();

    await expect(wizard.getByRole('alert')).toContainText('41 rows were not imported.');
    await expect(wizard.getByRole('alert')).toContainText('Correct the file and import it again.');
    const list = wizard.getByRole('region', { name: 'Rows not imported' });
    await expect(list.getByRole('row', { name: '3 amount is empty.', exact: true })).toBeVisible();
    const size = await list.evaluate((el) => ({
      scrollWidth: el.scrollWidth,
      clientWidth: el.clientWidth,
      scrollHeight: el.scrollHeight,
      clientHeight: el.clientHeight,
    }));
    expect(size.scrollWidth).toBeLessThanOrEqual(size.clientWidth);
    expect(size.scrollHeight).toBeGreaterThan(size.clientHeight);
    await expect(wizard.getByRole('button', { name: /Confirm/ })).toBeInViewport();
    await expect(wizard.getByRole('button', { name: /Confirm/ })).toBeDisabled();
    await runA11y(page);
    await page.screenshot({ path: 'e2e-results/m4-import-row-errors.png' });

    await wizard.getByRole('button', { name: 'Cancel' }).click();
  });

  test('offers the line of a cancelled import again when the file is uploaded again @scenario:bank-statement-import-and-match', async ({
    page,
  }) => {
    await login(page);
    await gotoBanking(page);

    // Nothing in the register matches this amount, so confirming it clears nothing for the specs after this one.
    const previewStatement = async () => {
      await page.getByRole('button', { name: 'Import statement' }).click();
      const dialog = page.getByRole('dialog', { name: 'Import bank statement' });
      await dialog.getByLabel('Statement CSV').setInputFiles({
        name: 'statement.csv',
        mimeType: 'text/csv',
        buffer: Buffer.from('Date,Description,Amount\n2026-06-20,Cancelled import line,-17.31\n'),
      });
      await mapAmountColumns(dialog);
      await dialog.getByRole('button', { name: 'Preview matches' }).click();
      return dialog;
    };

    const first = await previewStatement();
    await expect(first.getByText('Imported 1 line', { exact: true })).toBeVisible();
    await first.getByRole('button', { name: 'Cancel' }).click();

    const second = await previewStatement();
    await expect(
      second.getByText('Imported 0 lines · 1 line from an earlier import not yet confirmed'),
    ).toBeVisible();
    await expect(second.getByRole('cell', { name: 'Cancelled import line' })).toBeVisible();
    await second.getByRole('button', { name: /Confirm/ }).click();
    await expect(second).toBeHidden();

    // Decided now, so the same file adds nothing and offers nothing.
    const third = await previewStatement();
    await expect(third.getByText(/duplicate skipped/)).toBeVisible();
    await expect(third.getByText('No statement lines to review.')).toBeVisible();
    await third.getByRole('button', { name: 'Cancel' }).click();
  });

  test('reconciles Operating Trust to $0.00, finalizes, and locks the month against new postings @scenario:bank-reconcile-finalize-and-lock', async ({
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

    // A tenant payment dated into the locked month (today, the reconciled month) is rejected in place.
    await page.getByRole('button', { name: 'Tenants' }).click();
    await page.getByText('Jasmine Carter').click();
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();

    await page.getByRole('button', { name: 'Record payment' }).click();
    await page.getByLabel('Amount').fill('123.45');
    await page.getByRole('button', { name: 'Post payment' }).click();
    await expect(page.getByText(/reconciled and locked/)).toBeVisible();

    // The same payment dated into the open month (next month) still posts — the lock is account-month-scoped.
    const now = new Date();
    const openMonth = new Date(Date.UTC(now.getFullYear(), now.getMonth() + 1, 15));
    await page.getByLabel('Date', { exact: true }).fill(openMonth.toISOString().slice(0, 10));
    await page.getByRole('button', { name: 'Post payment' }).click();
    await expect(
      page.getByRole('row').filter({ hasText: '$123.45' }).filter({ hasText: 'Payment' }),
    ).toBeVisible();
  });

  // #554: the month the spec above locked is unlocked from its history row, takes a posting, and is
  // finalized again from the same reconcile-in-place screen. The posting is voided before the month
  // closes again, so Jasmine Carter's balance ends where it began.
  test('an administrator unlocks the finalized month with a reason, posts into it, and finalizes it again @scenario:bank-reconciliation-unlock-and-refinalize', async ({
    page,
  }) => {
    const now = new Date();
    const month = `${now.toLocaleString('en-US', { month: 'short' })} ${now.getFullYear()}`;
    await login(page);
    await gotoBanking(page);

    const historyRow = page.locator('.pf-recon-histrow').filter({ hasText: month });
    // The status badge, not the row: its "Finalized {date}" line outlives an unlock.
    const status = historyRow.locator('.pf-badge');
    await expect(status).toHaveText('Finalized');
    await historyRow.getByRole('button', { name: `Unlock ${month}` }).click();
    const dialog = page.getByRole('dialog', { name: `Unlock ${month}` });
    await dialog.getByLabel('Reason').fill('e2e: bank statement showed a missing deposit');
    await dialog.getByRole('button', { name: 'Unlock month' }).click();
    await expect(dialog).toBeHidden();
    await expect(status).toHaveText('Reopened');
    await expect(historyRow.getByRole('button', { name: `Unlock ${month}` })).toHaveCount(0);

    // A payment dated today, which the lock refused in the spec above, now posts.
    await page.getByRole('button', { name: 'Tenants' }).click();
    await page.getByText('Jasmine Carter').click();
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
    await page.getByRole('button', { name: 'Record payment' }).click();
    await page.getByLabel('Amount').fill('67.89');
    await page.getByRole('button', { name: 'Post payment' }).click();
    const paymentRow = page
      .getByRole('row')
      .filter({ hasText: '$67.89' })
      .filter({ hasText: 'Payment' })
      .filter({ hasNotText: 'Void —' });
    await expect(paymentRow).toBeVisible();

    await paymentRow.getByRole('button', { name: 'Void entry' }).click();
    const voidDialog = page.getByRole('dialog', { name: 'Void entry' });
    await voidDialog.getByLabel('Reason (internal note)').fill('e2e cleanup');
    await voidDialog.getByRole('button', { name: 'Void entry' }).click();
    await expect(paymentRow).toContainText('Voided');

    // Finalize the month again: the history row reads Finalized and offers Unlock once more.
    await gotoBanking(page);
    await page.getByRole('button', { name: 'Reconcile account' }).click();
    await page.getByRole('button', { name: 'Select all uncleared' }).click();
    await expect(page.getByLabel('Difference')).toHaveText('$0.00');
    await page.getByRole('button', { name: 'Finalize' }).click();
    await expect(page.getByRole('button', { name: 'Reconcile account' })).toBeVisible();
    await expect(status).toHaveText('Finalized');
    await expect(historyRow.getByRole('button', { name: `Unlock ${month}` })).toBeVisible();
  });
});
