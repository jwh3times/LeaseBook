import { expect, type Locator, type Page } from '@playwright/test';
import { SCENARIO_ADMIN, signIn } from './helpers';
import { test } from './test';

// Finalizing a reopened past month again from its history row (#656, the ADR-014 addendum of
// 2026-11-09), in the scenario organization (`seed --org scenario`, ADR-056) as an administrator.
//
// The scenario seed finalizes March, April and May 2026 on every bank account. An account's months
// unlock and finalize in order, so April opens only after May does, and May closes only after April.
// The correction is the one a trust account takes for a bank charge: the April statement shows an
// analysis fee the register lacked, and the PM's own deposit that covered it. Both are dated in April
// and net to zero, so April's statement balance stands, and nothing after April moves. Unlocking is
// skipped for a month an earlier attempt already left reopened, so a CI retry resumes the story.
const OPERATING_TRUST = 'Scenario Operating Trust';
const APRIL_LAST_DAY = '2026-04-30';

/** A small amount with random cents, so a run's lines are its own. */
function randomAmount(): string {
  return `1.${String(1 + Math.floor(Math.random() * 98)).padStart(2, '0')}`;
}

/** A per-run description, so a line is found by text no other run wrote. */
function describeRun(what: string): string {
  return `e2e ${what} ${Math.random().toString(36).slice(2, 8)}`;
}

async function openOperatingTrust(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Banking' }).click();
  await expect(page).toHaveURL(/\/banking/);
  await page.getByRole('button', { name: new RegExp(OPERATING_TRUST) }).click();
  await expect(page.getByRole('button', { name: new RegExp(OPERATING_TRUST) })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
}

const historyRow = (page: Page, month: string) =>
  page.locator('.pf-recon-histrow').filter({ hasText: month });

/** The row's status badge, not the row: its "Finalized {date}" line outlives an unlock. */
const status = (page: Page, month: string) => historyRow(page, month).locator('.pf-badge');

async function unlock(page: Page, month: string): Promise<Locator> {
  await historyRow(page, month)
    .getByRole('button', { name: `Unlock ${month}` })
    .click();
  const dialog = page.getByRole('dialog', { name: `Unlock ${month}` });
  await dialog.getByLabel('Reason').fill('e2e: statement showed a fee the register was missing');
  await dialog.getByRole('button', { name: 'Unlock month' }).click();
  return dialog;
}

/** Records a bank adjustment dated on April's last day, from the register's dialog. */
async function recordInApril(
  page: Page,
  kind: 'Bank fee' | 'PM contribution',
  amount: string,
  description: string,
): Promise<Locator> {
  await page.getByRole('button', { name: 'Fee or transfer' }).click();
  const dialog = page.getByRole('dialog');
  await expect(dialog.getByLabel('Date')).not.toHaveValue('');
  await dialog.getByRole('radio', { name: kind }).check();
  await dialog.getByLabel('Amount').fill(amount);
  await dialog.getByLabel('Date').fill(APRIL_LAST_DAY);
  await dialog.getByLabel('Description').fill(description);
  await dialog
    .getByRole('button', { name: kind === 'Bank fee' ? 'Record fee' : 'Record contribution' })
    .click();
  return dialog;
}

async function finalizeAgain(page: Page, month: string): Promise<Locator> {
  await historyRow(page, month)
    .getByRole('button', { name: `Finalize ${month} again` })
    .click();
  const view = page.getByRole('region', { name: `Finalize ${month} again` });
  await expect(view.getByRole('heading', { name: `Finalize ${month} again` })).toBeFocused();
  return view;
}

test('an administrator reopens a past month, posts the correction its statement shows, and finalizes it again in order @scenario:bank-reconciliation-refinalize-past-month', async ({
  page,
}) => {
  await signIn(page, SCENARIO_ADMIN);
  await openOperatingTrust(page);

  // April cannot reopen while May stays finalized: the refusal names the month to unlock first.
  if ((await status(page, 'May 2026').textContent()) === 'Finalized') {
    if ((await status(page, 'Apr 2026').textContent()) === 'Finalized') {
      const refused = await unlock(page, 'Apr 2026');
      await expect(refused.getByRole('alert')).toContainText(
        '2026-05 is finalized for this account; unlock it before unlocking an earlier month.',
      );
      await refused.getByRole('button', { name: 'Cancel' }).click();
    }
    await expect(await unlock(page, 'May 2026')).toBeHidden();
  }
  await expect(status(page, 'May 2026')).toHaveText('Reopened');
  if ((await status(page, 'Apr 2026').textContent()) === 'Finalized') {
    await expect(await unlock(page, 'Apr 2026')).toBeHidden();
  }
  await expect(status(page, 'Apr 2026')).toHaveText('Reopened');

  // The correction, dated into the reopened April: the PM's deposit first, so the fee it covers posts.
  const amount = randomAmount();
  const contributed = describeRun('april pm contribution');
  const charged = describeRun('april analysis fee');
  await expect(await recordInApril(page, 'PM contribution', amount, contributed)).toBeHidden();
  await expect(await recordInApril(page, 'Bank fee', amount, charged)).toBeHidden();

  // May cannot close while April is reopened.
  let view = await finalizeAgain(page, 'May 2026');
  await view.getByRole('button', { name: 'Finalize May 2026' }).click();
  await expect(view.getByRole('alert')).toContainText(
    '2026-04 is reopened for this account; finalize it again before a later month.',
  );
  await view.getByRole('button', { name: 'Cancel' }).click();

  // April against its own statement: the stored balance stands, and the two lines it showed are
  // ticked. Both are uncleared, and finalizing clears and reconciles them.
  view = await finalizeAgain(page, 'Apr 2026');
  await expect(view.getByLabel('Difference')).toHaveText('$0.00');
  await view.getByRole('checkbox', { name: new RegExp(charged) }).check();
  await expect(view.getByLabel('Difference')).toHaveText(`+$${amount}`);
  await view.getByRole('checkbox', { name: new RegExp(contributed) }).check();
  await expect(view.getByLabel('Difference')).toHaveText('$0.00');
  await view.getByRole('button', { name: 'Finalize Apr 2026' }).click();
  await expect(view).toBeHidden();
  await expect(status(page, 'Apr 2026')).toHaveText('Finalized');

  // April is locked again: a bank posting dated into it is refused in place.
  const refused = await recordInApril(page, 'Bank fee', amount, describeRun('late april fee'));
  await expect(refused.getByText(/reconciled and locked/)).toBeVisible();
  await refused.getByRole('button', { name: 'Cancel' }).click();

  // May, now in order, finalizes again at its stored statement: nothing after April moved.
  view = await finalizeAgain(page, 'May 2026');
  await expect(view.getByLabel('Difference')).toHaveText('$0.00');
  await view.getByRole('button', { name: 'Finalize May 2026' }).click();
  await expect(view).toBeHidden();
  await expect(status(page, 'May 2026')).toHaveText('Finalized');
});
