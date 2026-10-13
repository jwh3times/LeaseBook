import { readFile } from 'node:fs/promises';
import { expect, type Locator, type Page } from '@playwright/test';
import { captureDownload, SCENARIO_ADMIN, SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// Bank accounts, their check print settings, and a finalized reconciliation's stored report (#544),
// driven in the scenario organization (`seed --org scenario`, ADR-056).
//
// Nothing here posts, clears, reconciles or changes a seeded record, so the organization's figures
// and the invariants are left as the seed wrote them:
//
// - The report test reads the reports of the months the seed already finalized on the operating
//   trust (March to May 2026, `ScenarioSeeder`). It finalizes nothing, so it locks nothing.
// - The other two tests each create a bank account under a name no other run used, change only that
//   account, and deactivate it at the end. A bank account cannot be deleted, so each run leaves two
//   inactive, empty accounts behind.
//
// Three choices keep those accounts out of every other spec's way:
//
// - Their purpose is the PM operating account. The ledger composer and the apply dialog pick their
//   default bank as the first trust or deposit account in a newest-first list, so a new account of
//   either purpose would become the default the ledger-posting scenarios assert on.
// - Their name starts with "Z", so they sort after the three seeded "Scenario …" accounts and the
//   Banking screen still opens on the account it opened on before.
// - They end inactive, which takes them out of the posting pickers.
const OPERATING_TRUST = 'Scenario Operating Trust';

// The purpose the new accounts are created with, as the list shows it.
const PURPOSE_LABEL = 'PM operating account';
const PURPOSE_BOUNDARY = 'Outside the trust equation.';

// A routing number is nine digits whose last is a check digit. The first is valid; the second is the
// same number with the check digit wrong. Neither is a real bank's.
const ROUTING = '111000012';
const ROUTING_BAD_CHECK_DIGIT = '111000013';
const ON_US = '123456789U';

/** A per-run suffix, so a record is found by text that no other run wrote. */
function runId(): string {
  return Math.random().toString(36).slice(2, 8).padEnd(6, '0');
}

/** Reads a figure the screen shows (`$2,252.63`, or the dash that stands for zero) as cents. */
function toCents(text: string): number {
  const digits = text.replace(/[^0-9.]/g, '');
  if (digits === '') return 0;
  const magnitude = Math.round(Number(digits) * 100);
  return /[−(-]/.test(text) ? -magnitude : magnitude;
}

/** Counts the requests a page sends to one route, so a refusal in the form can be told from one by the server. */
function countRequests(page: Page, method: string, path: RegExp): () => number {
  let sent = 0;
  page.on('request', (request) => {
    if (request.method() === method && path.test(new URL(request.url()).pathname)) sent += 1;
  });
  return () => sent;
}

// Settings sits at the foot of the sidebar, outside its navigation landmark, so it is named by the
// sidebar itself. A page's own buttons can share a sidebar item's name.
async function openSettings(page: Page): Promise<Locator> {
  await page
    .getByRole('complementary')
    .getByRole('button', { name: 'Settings', exact: true })
    .click();
  await expect(page).toHaveURL(/\/settings$/);
  const card = page.locator('.pf-card').filter({
    has: page.getByRole('heading', { name: 'Bank accounts' }),
  });
  await expect(card.locator('tbody tr').first()).toBeVisible();
  return card;
}

/** Opens Banking on one account's register, by its tab. The sidebar's navigation landmark names the item. */
async function openBankingOn(page: Page, account: string): Promise<void> {
  await page.getByRole('navigation').getByRole('button', { name: 'Banking', exact: true }).click();
  await expect(page).toHaveURL(/\/banking/);
  const tab = page.locator('.pf-acct-tab').filter({ hasText: account });
  await tab.click();
  await expect(tab).toHaveAttribute('aria-pressed', 'true');
  await expect(page).toHaveURL(/[?&]account=[0-9a-f-]+/);
}

/** The bank-account list's row for one account, by its name. */
function bankRow(card: Locator, name: string): Locator {
  return card.locator('tbody tr').filter({ hasText: name });
}

/**
 * From a dialog's last field, moves to its primary button and presses it. These dialogs have no
 * submit button inside the form, so Enter in a field submits nothing; the keyboard path is Tab past
 * Cancel to the primary button, and Enter there.
 */
async function submitByKeyboard(page: Page, primary: Locator): Promise<void> {
  await page.keyboard.press('Tab');
  await page.keyboard.press('Tab');
  await expect(primary).toBeFocused();
  await page.keyboard.press('Enter');
}

/** Creates a PM operating account from the open settings page and returns its row. */
async function createAccount(
  page: Page,
  card: Locator,
  account: { name: string; institution: string; mask: string },
): Promise<Locator> {
  await card.getByRole('button', { name: 'New account' }).click();
  const dialog = page.getByRole('dialog', { name: 'New bank account' });
  await dialog.getByLabel('Name').fill(account.name);
  await dialog.getByLabel('Institution').fill(account.institution);
  await dialog.getByLabel('Mask (last 4)').fill(account.mask);
  await dialog.getByLabel('Purpose').selectOption('operating');
  await dialog.getByRole('button', { name: 'Create account' }).click();
  await expect(dialog).toBeHidden();
  const row = bankRow(card, account.name);
  await expect(row).toHaveCount(1);
  return row;
}

/** Deactivates the account in `row`, so no posting picker offers it. */
async function deactivate(row: Locator): Promise<void> {
  await row.getByRole('button', { name: 'Deactivate' }).click();
  await expect(row.locator('td').nth(4)).toHaveText('Inactive');
  await expect(row.getByRole('button', { name: 'Reactivate' })).toBeVisible();
}

test('downloads the stored report of a finalized reconciliation, by click and by keyboard: each file holds the month and the statement balance the history shows @scenario:reconciliation-report-download', async ({
  page,
}) => {
  await signIn(page, SCENARIO_STAFF);
  await openBankingOn(page, OPERATING_TRUST);
  const bankAccountId = new URL(page.url()).searchParams.get('account');

  // The seed finalized March, April and May 2026 on this account, and each has a stored report.
  const history = page.locator('.pf-card').filter({
    has: page.getByRole('heading', { name: 'Reconciliation history' }),
  });
  const month = (label: string) => history.locator('.pf-recon-histrow').filter({ hasText: label });
  for (const label of ['Mar 2026', 'Apr 2026', 'May 2026']) {
    await expect(month(label)).toHaveCount(1);
    await expect(month(label).locator('.pf-badge')).toHaveText('Finalized');
    await expect(month(label)).toContainText(/Finalized \d{4}-\d{2}-\d{2}/);
  }

  // What a report must say: its own month, this account, the statement balance the row shows, a
  // cleared balance equal to it, and no difference. That is what finalizing required.
  const expectReport = (file: Buffer, period: string, shownCents: number, fileName?: string) => {
    const report = JSON.parse(file.toString('utf8')) as Record<string, unknown>;
    expect(report.period).toBe(period);
    expect(report.bankAccountId).toBe(bankAccountId);
    expect(Math.round(Number(report.statementEndingBalance) * 100)).toBe(shownCents);
    expect(Math.round(Number(report.clearedBalance) * 100)).toBe(shownCents);
    expect(Math.round(Number(report.difference) * 100)).toBe(0);
    expect(Number(report.reconciledItemCount)).toBeGreaterThan(0);
    expect(String(report.finalizedAt)).toMatch(/^\d{4}-\d{2}-\d{2}T/);
    if (fileName) expect(fileName).toBe(`reconciliation-${String(report.reconciliationId)}.json`);
  };

  // May, with the pointer.
  const may = month('May 2026');
  const mayCents = toCents(await may.locator('.pf-money').innerText());
  expect(mayCents).toBeGreaterThan(0);
  const mayFile = await captureDownload(page, may.getByRole('button', { name: 'Report' }), {
    urlPart: '/api/accounting/reconciliations/',
    contentType: 'application/json',
  });
  expectReport(mayFile, '2026-05', mayCents);

  // Keyboard path: April, which the seed reopened and finalized a second time, from its focused
  // Report button with Enter. The file is named for the reconciliation it holds.
  const april = month('Apr 2026');
  const aprilCents = toCents(await april.locator('.pf-money').innerText());
  expect(aprilCents).not.toBe(mayCents);
  const report = april.getByRole('button', { name: 'Report' });
  await report.focus();
  await expect(report).toBeFocused();
  const downloading = page.waitForEvent('download', { timeout: 20_000 });
  await page.keyboard.press('Enter');
  const download = await downloading;
  expectReport(
    await readFile(await download.path()),
    '2026-04',
    aprilCents,
    download.suggestedFilename(),
  );

  // Downloading changed nothing: the history still lists the same months.
  await expect(history).toContainText(/\d+ reconciliations/);
  await expect(month('May 2026').locator('.pf-badge')).toHaveText('Finalized');
});

test('creates a bank account by keyboard and renames it: an over-long name is refused, and the account shows in settings and on Banking with an empty register @scenario:bank-account-create-and-edit', async ({
  page,
}) => {
  const id = runId();
  const name = `Z e2e Operating ${id}`;
  const renamed = `Z e2e Renamed ${id}`;
  const mask = String(1000 + Math.floor(Math.random() * 9000));
  const newMask = String(1000 + Math.floor(Math.random() * 9000));
  const creates = countRequests(page, 'POST', /^\/api\/settings\/banks$/);
  const updates = countRequests(page, 'PUT', /^\/api\/settings\/banks\/[0-9a-f-]+$/);

  await signIn(page, SCENARIO_ADMIN);
  const card = await openSettings(page);
  await card.getByRole('button', { name: 'New account' }).click();
  const dialog = page.getByRole('dialog', { name: 'New bank account' });
  const create = dialog.getByRole('button', { name: 'Create account' });

  // The name is required: until it is filled the button is disabled.
  await expect(create).toBeDisabled();

  // The purpose decides which side of the trust equation the account is on, and the form says so
  // before the account exists, because it cannot be changed afterwards.
  await dialog.getByLabel('Purpose').selectOption('operating');
  await expect(dialog).toContainText(
    `${PURPOSE_BOUNDARY} Purpose cannot be changed after creation.`,
  );

  // A name over 120 characters is refused by the server, in place, and no account is created.
  await dialog.getByLabel('Name').fill('Z'.repeat(121));
  await create.click();
  await expect(dialog.getByRole('alert')).toContainText('120 characters or fewer');
  await expect(dialog).toBeVisible();
  expect(creates()).toBe(1);

  // Keyboard path: correct the name, fill the rest, and create from the keyboard. Purpose is the
  // last field, so the footer's buttons follow it.
  await dialog.getByLabel('Name').fill(name);
  await dialog.getByLabel('Institution').fill('e2e First Bank');
  await dialog.getByLabel('Mask (last 4)').fill(mask);
  await dialog.getByLabel('Purpose').focus();
  await submitByKeyboard(page, create);

  // The dialog closes and the list shows the account as entered, active.
  await expect(dialog).toBeHidden();
  expect(creates()).toBe(2);
  const row = bankRow(card, name);
  await expect(row).toHaveCount(1);
  await expect(row.locator('td').nth(1)).toHaveText('e2e First Bank');
  await expect(row.locator('td').nth(2)).toHaveText(`••${mask}`);
  await expect(row.locator('td').nth(3)).toContainText(PURPOSE_LABEL);
  await expect(row.locator('td').nth(3)).toContainText(PURPOSE_BOUNDARY);
  await expect(row.locator('td').nth(4)).toHaveText('Active');

  // Banking has a register for it: no balance, nothing to clear, no transactions and no checks.
  await openBankingOn(page, name);
  const tab = page.locator('.pf-acct-tab').filter({ hasText: name });
  await expect(tab).toContainText('Reconciled');
  expect(toCents(await tab.locator('.pf-money').innerText())).toBe(0);
  await expect(page.getByText('No transactions yet')).toBeVisible();
  await expect(page.getByText('No refund checks on this account')).toBeVisible();
  await expect(page.getByText('No reconciliations yet')).toBeVisible();

  // Rename it and correct its institution and mask. The form opens on what is stored, and offers
  // no purpose to change.
  await openSettings(page);
  await row.getByRole('button', { name: `Edit ${name}` }).click();
  const edit = page.getByRole('dialog', { name: 'Edit bank account' });
  const save = edit.getByRole('button', { name: 'Save' });
  await expect(edit.getByLabel('Name')).toHaveValue(name);
  await expect(edit.getByLabel('Institution')).toHaveValue('e2e First Bank');
  await expect(edit.getByLabel('Mask (last 4)')).toHaveValue(mask);
  await expect(edit.getByLabel('Purpose')).toHaveCount(0);

  // A blank name cannot be saved.
  await edit.getByLabel('Name').fill('');
  await expect(save).toBeDisabled();

  // Keyboard path: the mask is the last field, so the footer's buttons follow it.
  await edit.getByLabel('Name').fill(renamed);
  await edit.getByLabel('Institution').fill('e2e Second Bank');
  await edit.getByLabel('Mask (last 4)').fill(newMask);
  await submitByKeyboard(page, save);
  await expect(edit).toBeHidden();
  expect(updates()).toBe(1);

  // The list shows the new values under the same purpose, and still does after a reload.
  const renamedRow = bankRow(card, renamed);
  for (const reloaded of [false, true]) {
    if (reloaded) {
      await page.reload();
      await expect(card.locator('tbody tr').first()).toBeVisible();
    }
    await expect(renamedRow).toHaveCount(1);
    await expect(bankRow(card, name)).toHaveCount(0);
    await expect(renamedRow.locator('td').nth(1)).toHaveText('e2e Second Bank');
    await expect(renamedRow.locator('td').nth(2)).toHaveText(`••${newMask}`);
    await expect(renamedRow.locator('td').nth(3)).toContainText(PURPOSE_LABEL);
    await expect(renamedRow.locator('td').nth(4)).toHaveText('Active');
  }

  // Leave it inactive, out of the posting pickers.
  await deactivate(renamedRow);
});

test('saves a bank account’s check print offsets by keyboard and its MICR details, and prints the alignment page: out-of-range offsets and a bad routing number are refused, and only the last four digits are shown @scenario:check-print-settings-and-micr', async ({
  page,
}) => {
  const id = runId();
  const name = `Z e2e Checks ${id}`;
  const offsetSaves = countRequests(page, 'PUT', /^\/api\/refund-checks\/print-settings\//);
  const micrSaves = countRequests(page, 'PUT', /^\/api\/refund-checks\/micr\//);

  // The account is one this test creates, so no seeded account's print settings change.
  await signIn(page, SCENARIO_ADMIN);
  const card = await openSettings(page);
  await createAccount(page, card, { name, institution: 'e2e First Bank', mask: '4321' });

  await openBankingOn(page, name);
  const openDialog = async (): Promise<Locator> => {
    await page.getByRole('button', { name: 'Print settings' }).click();
    const opened = page.getByRole('dialog', { name: 'Check print settings' });
    // Exact: the MICR line's own offsets are labelled "MICR line horizontal offset (points)".
    await expect(opened.getByLabel('Horizontal offset (points)', { exact: true })).toBeEnabled();
    return opened;
  };
  let dialog = await openDialog();
  let horizontal = dialog.getByLabel('Horizontal offset (points)', { exact: true });
  let vertical = dialog.getByLabel('Vertical offset (points)', { exact: true });
  let micr = dialog.getByRole('group', { name: 'MICR details' });

  // A new account prints with no offset, on pre-printed stock, with no numbers saved.
  await expect(dialog).toContainText(name);
  await expect(horizontal).toHaveValue('0');
  await expect(vertical).toHaveValue('0');
  await expect(micr).toContainText('Pre-printed stock');
  await expect(micr).toContainText('No routing number saved');
  await expect(micr).toContainText('No On-Us field saved');

  // An offset over one inch (72 points) is refused in the form, and nothing is sent.
  await horizontal.fill('100');
  await dialog.getByRole('button', { name: 'Save', exact: true }).click();
  await expect(dialog.getByRole('alert')).toContainText('Enter points from −72 to 72');
  await expect(horizontal).toBeFocused();
  expect(offsetSaves()).toBe(0);

  // Keyboard path: type both offsets and save with Enter from the field.
  await horizontal.fill('4.5');
  await vertical.fill('-3.25');
  await vertical.press('Enter');
  await expect(dialog.getByRole('status')).toHaveText('Saved.');
  await expect(dialog.getByRole('alert')).toHaveCount(0);
  expect(offsetSaves()).toBe(1);

  // The alignment page prints with the saved offsets: one PDF, and no second save.
  const pdf = await captureDownload(
    page,
    dialog.getByRole('button', { name: 'Print alignment page' }),
    { urlPart: '/alignment', contentType: 'application/pdf' },
  );
  expect(pdf.subarray(0, 5).toString('latin1')).toBe('%PDF-');
  expect(pdf.length).toBeGreaterThan(1000);
  expect(offsetSaves()).toBe(1);

  // A routing number with a wrong check digit is refused by the server, and nothing is saved.
  await micr.getByLabel('Routing number').fill(ROUTING_BAD_CHECK_DIGIT);
  await micr.getByLabel('On-Us field').fill(ON_US);
  await micr.getByRole('button', { name: 'Save MICR details' }).click();
  await expect(micr.getByRole('alert')).toContainText(
    'The routing number must be nine digits with a valid check digit.',
  );
  await expect(micr).toContainText('No routing number saved');
  expect(micrSaves()).toBe(1);

  // A valid one is saved with the On-Us field. The form empties and shows only the last four
  // digits of each; the full numbers appear nowhere on the page.
  await micr.getByLabel('Routing number').fill(ROUTING);
  await micr.getByRole('button', { name: 'Save MICR details' }).click();
  await expect(micr.getByRole('status')).toHaveText('Saved.');
  expect(micrSaves()).toBe(2);
  await expect(micr).toContainText(`Routing number ending ${ROUTING.slice(-4)}`);
  await expect(micr).toContainText('On-Us field ending 6789');
  await expect(micr.getByLabel('Routing number')).toHaveValue('');
  await expect(micr.getByLabel('On-Us field')).toHaveValue('');
  await expect(micr.getByRole('alert')).toHaveCount(0);
  await expect(page.locator('body')).not.toContainText(ROUTING);
  await expect(page.locator('body')).not.toContainText(ON_US);

  // Reloaded, the dialog opens on what was stored: the offsets, and the masked numbers.
  await page.reload();
  await expect(page.locator('.pf-acct-tab').filter({ hasText: name })).toHaveAttribute(
    'aria-pressed',
    'true',
  );
  dialog = await openDialog();
  horizontal = dialog.getByLabel('Horizontal offset (points)', { exact: true });
  vertical = dialog.getByLabel('Vertical offset (points)', { exact: true });
  micr = dialog.getByRole('group', { name: 'MICR details' });
  await expect(horizontal).toHaveValue('4.5');
  await expect(vertical).toHaveValue('-3.25');
  await expect(micr).toContainText('Pre-printed stock');
  await expect(micr).toContainText(`Routing number ending ${ROUTING.slice(-4)}`);
  await expect(micr).toContainText('On-Us field ending 6789');
  await expect(page.locator('body')).not.toContainText(ROUTING);
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();

  // Leave the account inactive, out of the posting pickers.
  await deactivate(bankRow(await openSettings(page), name));
});
