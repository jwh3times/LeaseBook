import { expect, type Locator, type Page } from '@playwright/test';
import { SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// A bank fee and a transfer between trust accounts, recorded from the bank register (#553), in the
// scenario organization (`seed --org scenario`, ADR-056) as a staff member.
//
// Both are paid from the management fees the PM holds in the account, and the server refuses one
// past them. The scenario seed ends with 368.74 held in the operating trust and none in the deposit
// trust. Each run spends under a dollar of that on its fee, and its transfer goes out and comes back,
// so the account keeps covering reruns, and CI's retries against the journal a first attempt left.
//
// Every posting is dated today, the dialog's default. The scenario organization's reconciled months
// are March to May 2026, so today is always in an open month.
const OPERATING_TRUST = 'Scenario Operating Trust';
const DEPOSIT_TRUST = 'Scenario Deposit Trust';

/** A small amount with random cents, so a run's rows are its own. */
function randomAmount(wholeDollars: number): string {
  return `${wholeDollars}.${String(1 + Math.floor(Math.random() * 98)).padStart(2, '0')}`;
}

/** A per-run description, so a row is found by text no other run wrote. */
function describeRun(what: string): string {
  return `e2e ${what} ${Math.random().toString(36).slice(2, 8)}`;
}

async function openRegister(page: Page, account: string): Promise<void> {
  await page.getByRole('button', { name: 'Banking' }).click();
  await expect(page).toHaveURL(/\/banking/);
  await page.getByRole('button', { name: new RegExp(account) }).click();
  await expect(page.getByRole('button', { name: new RegExp(account) })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  await expect(page.getByRole('table')).toBeVisible();
}

/** Opens the dialog by keyboard and waits for it to know the organization's today. */
async function openDialog(page: Page): Promise<Locator> {
  await page.getByRole('button', { name: 'Fee or transfer' }).focus();
  await page.keyboard.press('Enter');
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('Date')).not.toHaveValue('');
  return dialog;
}

/**
 * Presses Tab until the target has focus. A date input takes a Tab per part (month, day, year) in
 * Chromium, so the number of presses is the browser's business, not the test's.
 */
async function tabTo(page: Page, target: Locator, max = 12): Promise<void> {
  for (let i = 0; i < max; i++) {
    await page.keyboard.press('Tab');
    if (await target.evaluate((el) => el === document.activeElement)) return;
  }
  await expect(target).toBeFocused(); // fails with the locator named
}

const registerRow = (page: Page, description: string) =>
  page.getByRole('table').getByRole('row').filter({ hasText: description });

test.describe('bank fee and trust transfer from the register', () => {
  test('records a bank fee by keyboard, and shows a fee past the held fees refused in place @scenario:bank-fee-from-register', async ({
    page,
  }) => {
    await signIn(page, SCENARIO_STAFF);
    await openRegister(page, OPERATING_TRUST);

    // No account holds a million dollars of fees: refused beside the amount, saying what is held.
    const dialog = await openDialog(page);
    await page.keyboard.press('Tab'); // from the kind choice to the amount
    await expect(dialog.getByLabel('Amount')).toBeFocused();
    await page.keyboard.type('999999');
    await dialog.getByRole('button', { name: 'Record fee' }).focus();
    await page.keyboard.press('Enter');
    await expect(dialog.getByText(/holds only [\d,]+\.\d{2} in management fees/)).toBeVisible();
    await expect(dialog.getByLabel('Amount')).toHaveAttribute('aria-invalid', 'true');

    // A fee the held fees cover posts, closes the dialog, and lands on the register.
    const amount = randomAmount(0);
    const description = describeRun('bank fee');
    await dialog.getByLabel('Amount').fill(amount);
    await dialog.getByLabel('Description').fill(description);
    await dialog.getByRole('button', { name: 'Record fee' }).focus();
    await page.keyboard.press('Enter');

    await expect(page.getByText(`Bank fee of $${amount} recorded.`)).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(registerRow(page, description)).toContainText(`$${amount}`);
    await expect(registerRow(page, description)).toContainText('Uncleared');
  });

  test('transfers to the other trust account and back, refreshing both registers @scenario:trust-transfer-from-register', async ({
    page,
  }) => {
    await signIn(page, SCENARIO_STAFF);
    await openRegister(page, OPERATING_TRUST);
    const amount = randomAmount(1);
    const out = describeRun('transfer out');
    const back = describeRun('transfer back');

    // The first leg by keyboard alone: arrow to the transfer, tab through the fields, Enter on save.
    await openDialog(page);
    await page.keyboard.press('ArrowDown'); // from "Bank fee" to the transfer
    let dialog = page.getByRole('dialog', { name: 'Transfer to another trust account' });
    await expect(
      dialog.getByRole('radio', { name: 'Transfer to another trust account' }),
    ).toBeChecked();
    // The PM operating account is not a trust account, so it is never offered, and the deposit
    // trust, the only other one, is chosen already.
    await expect(
      dialog.getByLabel('To account').locator('option', { hasText: 'Scenario PM Operating' }),
    ).toHaveCount(0);
    await expect(dialog.getByLabel('To account').locator('option:checked')).toHaveText(
      DEPOSIT_TRUST,
    );
    await tabTo(page, dialog.getByLabel('Amount'));
    await page.keyboard.type(amount);
    // Past the date (today, as it is) and the chosen account.
    await tabTo(page, dialog.getByLabel('Description'));
    await page.keyboard.type(out);
    await tabTo(page, dialog.getByRole('button', { name: 'Record transfer' }));
    await page.keyboard.press('Enter');

    await expect(
      page.getByText(`Transfer of $${amount} to ${DEPOSIT_TRUST} recorded.`),
    ).toBeVisible();
    await expect(registerRow(page, out)).toContainText(`$${amount}`);

    // The deposit trust's register shows the transfer in; sending it back keeps the fees where they were.
    await openRegister(page, DEPOSIT_TRUST);
    await expect(registerRow(page, out)).toContainText(`$${amount}`);
    dialog = await openDialog(page);
    await dialog.getByRole('radio', { name: 'Transfer to another trust account' }).check();
    dialog = page.getByRole('dialog', { name: 'Transfer to another trust account' });
    await dialog.getByLabel('To account').selectOption({ label: OPERATING_TRUST });
    await dialog.getByLabel('Amount').fill(amount);
    await dialog.getByLabel('Description').fill(back);
    await dialog.getByRole('button', { name: 'Record transfer' }).click();

    await expect(
      page.getByText(`Transfer of $${amount} to ${OPERATING_TRUST} recorded.`),
    ).toBeVisible();
    await expect(registerRow(page, back)).toContainText(`$${amount}`);
    await openRegister(page, OPERATING_TRUST);
    await expect(registerRow(page, back)).toContainText(`$${amount}`);
  });
});
