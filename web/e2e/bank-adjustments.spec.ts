import { expect, type Locator, type Page } from '@playwright/test';
import { SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// A bank fee and a transfer between trust accounts, recorded from the bank register (#553), a PM
// contribution (#720) and a sweep of the held fees to the PM operating account (#743), in the
// scenario organization (`seed --org scenario`, ADR-056) as a staff member.
//
// A fee, a transfer and a sweep are paid from the management fees the PM holds in the account, and
// the server refuses one past them. The scenario seed ends with 368.74 held in the operating trust and none in the deposit
// trust. Each run spends under a dollar of that on its fee, and its transfer goes out and comes back,
// so the account keeps covering reruns, and CI's retries against the journal a first attempt left.
//
// Every posting is dated today, the dialog's default. The scenario organization's reconciled months
// are March to May 2026, so today is always in an open month.
const OPERATING_TRUST = 'Scenario Operating Trust';
const DEPOSIT_TRUST = 'Scenario Deposit Trust';
const PM_OPERATING = 'Scenario PM Operating';

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

test.describe('bank fee, trust transfer, PM contribution and fee sweep from the register', () => {
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

  test('records a PM contribution so a bank fee the held fees could not cover posts @scenario:pm-contribution-from-register', async ({
    page,
  }) => {
    await signIn(page, SCENARIO_STAFF);
    // The deposit trust holds no fees at rest (a run's transfer comes back, and this test spends what
    // it contributes), so a fee there is refused until the PM puts its own money in. A retry after a
    // run that stopped between the two is covered too: the fee is sized from what the refusal says is
    // held, plus this run's contribution.
    await openRegister(page, DEPOSIT_TRUST);
    let dialog = await openDialog(page);
    await page.keyboard.press('Tab');
    await page.keyboard.type('999999');
    await dialog.getByRole('button', { name: 'Record fee' }).click();
    const refusal = dialog.getByText(/holds only [\d.]+ in management fees/);
    await expect(refusal).toBeVisible();
    await expect(refusal).toContainText(
      'A PM contribution to this account dated on or before the fee covers it.',
    );
    const held =
      /holds only ([\d.]+) in management fees/.exec((await refusal.textContent()) ?? '')?.[1] ?? '';
    const contribution = randomAmount(0);
    const feeCents = Math.round(Number(held) * 100) + Math.round(Number(contribution) * 100);
    const fee = `${Math.floor(feeCents / 100)}.${String(feeCents % 100).padStart(2, '0')}`;
    await dialog.getByRole('button', { name: 'Cancel' }).click();

    // The contribution, by keyboard: arrow from "Bank fee" past the transfer to it.
    const contributed = describeRun('pm contribution');
    await openDialog(page);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    dialog = page.getByRole('dialog', { name: 'Record a PM contribution' });
    await expect(dialog.getByRole('radio', { name: 'PM contribution' })).toBeChecked();
    await tabTo(page, dialog.getByLabel('Amount'));
    await page.keyboard.type(contribution);
    await tabTo(page, dialog.getByLabel('Description'));
    await page.keyboard.type(contributed);
    await tabTo(page, dialog.getByRole('button', { name: 'Record contribution' }));
    await page.keyboard.press('Enter');

    await expect(page.getByText(`PM contribution of $${contribution} recorded.`)).toBeVisible();
    await expect(registerRow(page, contributed)).toContainText(`$${contribution}`);
    await expect(registerRow(page, contributed)).toContainText('Uncleared');

    // The same fee now posts, paid from the held fees the contribution joined.
    const charged = describeRun('bank fee after contribution');
    dialog = await openDialog(page);
    await dialog.getByLabel('Amount').fill(fee);
    await dialog.getByLabel('Description').fill(charged);
    await dialog.getByRole('button', { name: 'Record fee' }).click();

    await expect(page.getByText(`Bank fee of $${fee} recorded.`)).toBeVisible();
    await expect(registerRow(page, charged)).toContainText(`$${fee}`);
  });

  test('sweeps the held fees to the PM operating account by keyboard, prefilled with what the floor allows @scenario:pm-fee-sweep-from-register', async ({
    page,
  }) => {
    await signIn(page, SCENARIO_STAFF);
    // In the deposit trust, which holds no fees at rest, so the operating trust keeps covering the
    // other tests' fees. A contribution puts some there, and the sweep takes everything held, which
    // leaves the account as it was found, whatever an earlier attempt left behind.
    await openRegister(page, DEPOSIT_TRUST);
    const contribution = randomAmount(0);
    let dialog = await openDialog(page);
    await dialog.getByRole('radio', { name: 'PM contribution' }).check();
    dialog = page.getByRole('dialog', { name: 'Record a PM contribution' });
    await dialog.getByLabel('Amount').fill(contribution);
    await dialog.getByLabel('Description').fill(describeRun('contribution to sweep'));
    await dialog.getByRole('button', { name: 'Record contribution' }).click();
    await expect(page.getByText(`PM contribution of $${contribution} recorded.`)).toBeVisible();

    // The sweep, by keyboard: arrow from "Bank fee" past the transfer and the contribution to it.
    await openDialog(page);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    dialog = page.getByRole('dialog', { name: 'Sweep PM fees to the PM operating account' });
    await expect(dialog.getByRole('radio', { name: 'Sweep PM fees' })).toBeChecked();
    // The amount starts at the most the server accepts today: at least this run's contribution.
    await expect(dialog.getByText(/can be swept on this date/)).toBeVisible();
    const amountField = dialog.getByLabel('Amount');
    await expect(amountField).toHaveValue(/^\d+\.\d{2}$/);
    const swept = await amountField.inputValue();
    expect(Number(swept)).toBeGreaterThanOrEqual(Number(contribution));
    // Only the PM operating account is offered, and as the only one it is chosen already.
    await expect(dialog.getByLabel('To account').locator('option:checked')).toHaveText(
      PM_OPERATING,
    );
    await expect(
      dialog.getByLabel('To account').locator('option', { hasText: OPERATING_TRUST }),
    ).toHaveCount(0);
    const description = describeRun('fee sweep');
    await tabTo(page, dialog.getByLabel('Description'));
    await page.keyboard.type(description);
    await tabTo(page, dialog.getByRole('button', { name: 'Record sweep' }));
    await page.keyboard.press('Enter');

    await expect(page.getByText(`Sweep of $${swept} to ${PM_OPERATING} recorded.`)).toBeVisible();
    await expect(page.getByRole('dialog')).toHaveCount(0);
    await expect(registerRow(page, description)).toContainText(`$${swept}`);
    await expect(registerRow(page, description)).toContainText('Uncleared');

    // Nothing is left to sweep from today on, and the PM operating account's register shows it in.
    await openDialog(page);
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await page.keyboard.press('ArrowDown');
    await expect(page.getByRole('dialog').getByText(/nothing to sweep/)).toBeVisible();
    await page.getByRole('dialog').getByRole('button', { name: 'Cancel' }).click();
    await openRegister(page, PM_OPERATING);
    await expect(registerRow(page, description)).toContainText(`$${swept}`);
  });
});
