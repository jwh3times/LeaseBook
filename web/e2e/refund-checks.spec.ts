import { expect, test, type Page } from '@playwright/test';
import type { BudgetTelemetryRequest } from '@/api';
import { captureDownload, DEMO_ADMIN, runA11y, signIn } from './helpers';

// Refund checks (#473), end to end against the seeded demo org: issue a check for Jasmine Carter's
// held security deposit (1,450.00 on the Security Deposit Trust — DemoJournalSeed's `DepositCr(T1, O1)`)
// inside the ≤ 4 interaction budget, print it, find it Outstanding on Banking, void it, and see the
// deposit held restored.
//
// Like the M3 ledger specs, this mutates the demo org only with a check it then voids: the reversal
// restores the held deposit and nets the withdrawal out of the register, so the golden journal figures
// return to baseline. What persists is the voided check record itself — by design every issued number
// stays on record — so a second run on the same database prefills the next number rather than
// colliding. It touches only the Security Deposit Trust, whose month m4-banking never locks.

// The first number entered on a database that has never issued a refund check on this account. The
// server answers `nextCheckNumber: null` until then, and the operator types the stock's number.
const FIRST_STOCK_NUMBER = '5001';

async function openJasmine(page: Page) {
  await page.getByRole('button', { name: 'Tenants' }).click();
  await expect(page).toHaveURL(/\/tenants$/);
  await page.getByText('Jasmine Carter').click();
  await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
}

function depositHeld(page: Page) {
  return page.locator('.pf-tstat').filter({ hasText: 'Deposit held' }).locator('.pf-money');
}

test('issues, prints and voids a deposit refund check within the ≤ 4 interaction budget', async ({
  page,
}) => {
  await signIn(page, DEMO_ADMIN);
  await openJasmine(page);
  const ledgerUrl = page.url();

  await expect(depositHeld(page)).not.toHaveText('$0.00');
  const heldBefore = (await depositHeld(page).textContent()) ?? '';

  // The budget sample posts on the first successful print. Its response is asserted too: the host
  // accepts only the tasks it publishes, and the client swallows a rejection, so a 204 is the only
  // proof the sample was recorded rather than silently dropped.
  const budget = page.waitForRequest(
    (request) =>
      request.url().includes('/api/telemetry/budget') &&
      request.method() === 'POST' &&
      (request.postData() ?? '').includes('"task":"issue-refund-check"'),
  );
  const budgetResponse = page.waitForResponse(
    (response) =>
      response.url().includes('/api/telemetry/budget') &&
      (response.request().postData() ?? '').includes('"task":"issue-refund-check"'),
  );

  // (1) Open Refund…
  await page.getByRole('button', { name: 'Refund…' }).click();
  const dialog = page.getByRole('dialog', { name: 'Refund check' });
  await expect(dialog.getByLabel('Amount')).toBeVisible();

  // Another spec may have left Jasmine a prepaid credit too; then the deposit is chosen explicitly
  // (2), which the budget counts — and still meets.
  const funds = dialog.getByRole('group', { name: 'Refund from' });
  if (await funds.isVisible()) {
    await funds.getByRole('radio', { name: /Security deposit/ }).check();
  } else {
    await expect(dialog.getByText('Security deposit', { exact: true })).toBeVisible();
  }
  await expect(dialog.getByText('Security Deposit Trust')).toBeVisible();

  // Confirm the prefill. The held amount, payee and street line come from the ledger page; the
  // tenant record carries no city/state/ZIP, so those are typed — data entry, not interactions.
  await expect(dialog.getByLabel('Amount')).toHaveValue(/^\d+\.\d{2}$/);
  await expect(dialog.getByLabel('Pay to the order of')).toHaveValue('Jasmine Carter');
  await expect(dialog.getByLabel('Mailing address')).toHaveValue('412 Oakmont Ave');
  const number = dialog.getByLabel('Check number');
  if ((await number.inputValue()) === '') await number.fill(FIRST_STOCK_NUMBER);
  await dialog.getByLabel('City').fill('Asheville');
  await dialog.getByLabel('State').fill('NC');
  await dialog.getByLabel('ZIP').fill('28801');

  // (2/3) Issue.
  await dialog.getByRole('button', { name: 'Issue check' }).click();
  const issued = page.getByRole('dialog', { name: 'Refund check issued' });
  const summary = issued.getByRole('status');
  await expect(summary).toContainText('Security Deposit Trust');
  const checkNumber = /Check #(\d+)/.exec((await summary.textContent()) ?? '')?.[1];
  expect(checkNumber).toBeTruthy();
  // The same key is never offered again.
  await expect(issued.getByRole('button', { name: 'Issue check' })).toHaveCount(0);
  // The a11y gate's routed scans run on a fresh seed with no checks; the issued step is only
  // reachable here.
  await runA11y(page);

  // (3/4) Print — a real PDF from the host.
  const pdf = await captureDownload(page, issued.getByRole('button', { name: 'Print check' }), {
    urlPart: '/api/refund-checks/',
    contentType: 'application/pdf',
  });
  expect(pdf.subarray(0, 5).toString('latin1')).toBe('%PDF-');

  const event = JSON.parse((await budget).postData() ?? '{}') as BudgetTelemetryRequest;
  expect(event.task).toBe('issue-refund-check');
  expect(event.met).toBe(true);
  expect(Number(event.interactions)).toBeLessThanOrEqual(4);
  expect((await budgetResponse).status()).toBe(204);

  await issued.getByRole('button', { name: 'Done' }).click();
  await expect(issued).toBeHidden();

  // The deposit held dropped. A deposit refund posts no receivable or prepayment line, so it has no
  // ledger row here: security deposits are tracked apart from the receivable ledger.
  await expect(depositHeld(page)).not.toHaveText(heldBefore);

  // On Banking → Security Deposit Trust the check is Outstanding and printed once.
  await page.goto('/banking');
  await page.getByRole('button', { name: /Security Deposit Trust/ }).click();
  const checks = page.getByRole('table', { name: 'Refund checks on Security Deposit Trust' });
  const row = checks.getByRole('row').filter({
    has: page.getByRole('button', { name: `Void check #${checkNumber}` }),
  });
  await expect(row).toContainText('Outstanding');
  await expect(row).toContainText('1×');

  // Void it with a reason.
  await row.getByRole('button', { name: `Void check #${checkNumber}` }).click();
  const voidDialog = page.getByRole('dialog', { name: `Void check #${checkNumber}` });
  await voidDialog.getByLabel('Reason (internal note)').fill('e2e cleanup');
  await voidDialog.getByRole('button', { name: 'Void check' }).click();
  await expect(voidDialog).toBeHidden();
  await expect(row).toContainText('Voided');
  await expect(row.getByRole('button', { name: `Void check #${checkNumber}` })).toBeDisabled();
  await expect(row.getByRole('button', { name: /print check #/i })).toBeDisabled();
  // A populated list — every status badge and disabled-with-reason action — is likewise only
  // reachable after a check exists.
  await runA11y(page);

  // Back on the ledger the held deposit is restored.
  await page.goto(ledgerUrl);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
  await expect(depositHeld(page)).toHaveText(heldBefore);
});
