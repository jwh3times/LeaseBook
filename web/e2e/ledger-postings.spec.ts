import { expect, type Locator, type Page } from '@playwright/test';
import { captureDownload, SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// Ledger postings other than a payment and a plain charge (#544), driven in the scenario
// organization (`seed --org scenario`, ADR-056) as a staff member.
//
// Other scenarios post to the same organization, and CI retries a failed test against the journal
// its first attempt left behind. So no figure here is a seeded one: each test reads the tenant's
// balance and held deposit from the screen before it posts, and asserts the change to the cent. The
// held-funds test collects what it applies and charges what it clears, so the tenant ends each run
// where it began and the test can run any number of times.
//
// Every posting is dated today, the composer's default. The scenario organization's reconciled
// months are March to May 2026, so today is always in an open month.
const TENANT = 'Dean Ashford';
const DEPOSIT_TRUST = 'Scenario Deposit Trust';
const OPERATING_TRUST = 'Scenario Operating Trust';
// The deposit trust is reconciled and locked for this month (`ScenarioSeeder`, April 2026).
const LOCKED_DATE = '2026-04-15';

/** A small amount with random cents, in cents, so a run's rows are its own. */
function randomCents(wholeDollars: number): number {
  return wholeDollars * 100 + 1 + Math.floor(Math.random() * 98);
}

const dollars = (cents: number) => (cents / 100).toFixed(2);

/** What a ledger cell shows for an amount: `$12.34`. */
const shown = (cents: number) =>
  `$${(cents / 100).toLocaleString('en-US', { minimumFractionDigits: 2 })}`;

/** A per-run description, so a row is found by text that no other run wrote. */
function describe(what: string): string {
  return `e2e ${what} ${Math.random().toString(36).slice(2, 8)}`;
}

/** Reads a figure the screen shows (`$1,310.00`, `−$4.20`, or the dash that stands for zero) as cents. */
function toCents(text: string): number {
  const digits = text.replace(/[^0-9.]/g, '');
  if (digits === '') return 0;
  const magnitude = Math.round(Number(digits) * 100);
  return /[−(-]/.test(text) ? -magnitude : magnitude;
}

function headerFigure(page: Page, label: 'Current balance' | 'Deposit held'): Locator {
  return page.locator('.pf-tstat').filter({ hasText: label }).locator('.pf-money');
}

async function readCents(figure: Locator): Promise<number> {
  return toCents(await figure.innerText());
}

/** Waits for a header figure to show `cents`; the header refetches after each post. */
async function expectFigure(figure: Locator, cents: number): Promise<void> {
  await expect.poll(() => readCents(figure), { timeout: 10_000 }).toBe(cents);
}

async function openLedger(page: Page): Promise<void> {
  await signIn(page, SCENARIO_STAFF);
  await page.getByRole('button', { name: 'Tenants' }).click();
  await expect(page).toHaveURL(/\/tenants$/);
  await page.getByText(TENANT).click();
  await expect(page.getByRole('heading', { name: TENANT })).toBeVisible();
  await expect(page.getByRole('grid', { name: 'Tenant ledger' })).toBeVisible();
  await expect(headerFigure(page, 'Current balance')).toBeVisible();
}

/** Opens the charge composer on a charge type and returns it. */
async function openComposer(page: Page, chargeType: string): Promise<Locator> {
  await page.getByRole('button', { name: 'Add charge' }).click();
  const composer = page.locator('.pf-composer');
  await composer.getByLabel('Charge type').selectOption(chargeType);
  return composer;
}

/** The ledger row a run wrote, found by its description. */
function ledgerRow(page: Page, description: string): Locator {
  return page.getByRole('row').filter({ hasText: description });
}

// Columns of the ledger grid, by position: Date, Type, Description, Charge, Payment, Balance, Status.
const CHARGE = 3;
const PAYMENT = 4;
const BALANCE = 5;

test('issues a credit from the composer by keyboard: the ledger shows the line and the balance falls by the amount @scenario:ledger-issue-credit', async ({
  page,
}) => {
  await openLedger(page);
  const balance = headerFigure(page, 'Current balance');
  const before = await readCents(balance);

  const amount = randomCents(3);
  const reason = describe('goodwill credit');
  const composer = await openComposer(page, 'Credit');
  // A credit's free text is its reason, and it prints on the owner statement (ADR-047).
  await expect(composer.getByLabel('Statement reason')).toBeVisible();

  // Submitting with no amount is refused in place, and nothing is posted.
  await composer.getByLabel('Amount').press('Enter');
  await expect(composer.getByRole('alert')).toHaveText('Enter an amount greater than zero.');

  // Keyboard path: type the amount and the reason, and post with Enter.
  await composer.getByLabel('Amount').fill(dollars(amount));
  await composer.getByLabel('Statement reason').fill(reason);
  await composer.getByLabel('Statement reason').press('Enter');

  const row = ledgerRow(page, reason);
  await expect(row).toBeVisible();
  await expect(row.getByRole('gridcell').nth(1)).toHaveText('Credit');
  await expect(row.getByRole('gridcell').nth(CHARGE)).toHaveText('—');
  await expect(row.getByRole('gridcell').nth(PAYMENT)).toHaveText(shown(amount));
  await expect(row).toContainText('Posted');
  await expect(composer).toBeHidden();

  // The credit lowers what the tenant owes by exactly its amount, in the header and on the row.
  await expectFigure(balance, before - amount);
  expect(await readCents(row.getByRole('gridcell').nth(BALANCE))).toBe(before - amount);
});

test('collects a deposit and a prepayment, then applies both against a charge: each figure moves by the amount and the tenant ends where it began @scenario:ledger-collect-and-apply-held-funds', async ({
  page,
}) => {
  await openLedger(page);
  const balance = headerFigure(page, 'Current balance');
  const depositHeld = headerFigure(page, 'Deposit held');
  const balanceBefore = await readCents(balance);
  const depositBefore = await readCents(depositHeld);

  const deposit = randomCents(4);
  const prepayment = randomCents(6);
  const charge = deposit + prepayment;

  // Collect a security deposit. It defaults to the deposit trust, is a liability and not a ledger
  // line, and leaves what the tenant owes alone.
  let composer = await openComposer(page, 'Security Deposit');
  await expect(composer.getByLabel('Bank account').locator('option:checked')).toHaveText(
    DEPOSIT_TRUST,
  );
  await composer.getByLabel('Amount').fill(dollars(deposit));
  await composer.getByLabel('Statement description').fill(describe('deposit'));
  await composer.getByRole('button', { name: 'Post charge' }).click();
  await expect(composer).toBeHidden();
  await expectFigure(depositHeld, depositBefore + deposit);
  await expectFigure(balance, balanceBefore);

  // Receive a prepayment. It defaults to the operating trust and shows as credit on the ledger.
  const prepaymentText = describe('prepayment');
  composer = await openComposer(page, 'Prepayment');
  await expect(composer.getByLabel('Bank account').locator('option:checked')).toHaveText(
    OPERATING_TRUST,
  );
  await composer.getByLabel('Amount').fill(dollars(prepayment));
  await composer.getByLabel('Statement description').fill(prepaymentText);
  await composer.getByRole('button', { name: 'Post charge' }).click();
  const prepaymentRow = ledgerRow(page, prepaymentText);
  await expect(prepaymentRow.getByRole('gridcell').nth(1)).toHaveText('Prepayment');
  await expect(prepaymentRow.getByRole('gridcell').nth(PAYMENT)).toHaveText(shown(prepayment));
  await expectFigure(balance, balanceBefore - prepayment);
  await expectFigure(depositHeld, depositBefore + deposit);

  // Charge what the held funds will clear, so the applications below never run out of charges.
  const chargeText = describe('move-out cleaning');
  composer = await openComposer(page, 'Other');
  await composer.getByLabel('Amount').fill(dollars(charge));
  await composer.getByLabel('Statement description').fill(chargeText);
  await composer.getByRole('button', { name: 'Post charge' }).click();
  await expect(ledgerRow(page, chargeText).getByRole('gridcell').nth(CHARGE)).toHaveText(
    shown(charge),
  );
  await expectFigure(balance, balanceBefore - prepayment + charge);

  // Apply the deposit against the tenant's open charges: the held deposit and the balance both fall.
  const depositApplied = describe('deposit applied');
  await page.getByRole('button', { name: 'Apply…' }).click();
  let apply = page.getByRole('dialog', { name: 'Apply held funds' });
  await expect(apply.getByLabel('Source')).toHaveValue('deposit');
  await expect(apply).toContainText(`From ${DEPOSIT_TRUST} → ${OPERATING_TRUST}`);
  await apply.getByLabel('Amount').fill(dollars(deposit));
  await apply.getByLabel('Statement description').fill(depositApplied);
  await apply.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(apply).toBeHidden();
  const depositRow = ledgerRow(page, depositApplied);
  await expect(depositRow.getByRole('gridcell').nth(PAYMENT)).toHaveText(shown(deposit));
  await expectFigure(depositHeld, depositBefore);
  await expectFigure(balance, balanceBefore - prepayment + charge - deposit);

  // Apply the prepayment. More than is held is refused in place and the dialog stays open.
  const prepaymentApplied = describe('prepayment applied');
  await page.getByRole('button', { name: 'Apply…' }).click();
  apply = page.getByRole('dialog', { name: 'Apply held funds' });
  await apply.getByLabel('Source').selectOption('prepayment');
  await apply.getByLabel('Amount').fill('999999');
  await apply.getByLabel('Amount').press('Enter');
  await expect(apply.getByRole('alert')).toContainText('exceeds');
  await expect(apply).toBeVisible();

  // Keyboard path: correct the amount and apply with Enter. The prepaid credit becomes a payment
  // against the charge, so the row carries the amount on both sides and the balance does not move.
  await apply.getByLabel('Amount').fill(dollars(prepayment));
  await apply.getByLabel('Statement description').fill(prepaymentApplied);
  await apply.getByLabel('Statement description').press('Enter');
  await expect(apply).toBeHidden();
  const appliedRow = ledgerRow(page, prepaymentApplied);
  await expect(appliedRow.getByRole('gridcell').nth(CHARGE)).toHaveText(shown(prepayment));
  await expect(appliedRow.getByRole('gridcell').nth(PAYMENT)).toHaveText(shown(prepayment));

  // Deposit in and out, prepayment in and out, and a charge both cleared: back where it began.
  await expectFigure(balance, balanceBefore);
  await expectFigure(depositHeld, depositBefore);
  expect(await readCents(appliedRow.getByRole('gridcell').nth(BALANCE))).toBe(balanceBefore);
});

test('a deposit dated into a reconciled month is refused with the lock message and nothing is collected @scenario:ledger-deposit-into-locked-month', async ({
  page,
}) => {
  await openLedger(page);
  const depositHeld = headerFigure(page, 'Deposit held');
  const before = await readCents(depositHeld);

  const composer = await openComposer(page, 'Security Deposit');
  await composer.getByLabel('Date', { exact: true }).fill(LOCKED_DATE);
  await composer.getByLabel('Amount').fill(dollars(randomCents(5)));
  await composer.getByLabel('Statement description').fill(describe('late deposit'));
  const refused = page.waitForResponse(
    (response) => response.url().includes('/deposits') && response.request().method() === 'POST',
  );
  await composer.getByRole('button', { name: 'Post charge' }).click();
  expect((await refused).status()).toBe(409);

  await expect(composer.getByRole('alert')).toContainText(
    "This account's month is reconciled and locked",
  );
  await expect(composer).toBeVisible(); // stays open so the date can be moved
  await composer.getByRole('button', { name: 'Cancel' }).click();
  expect(await readCents(depositHeld)).toBe(before);
});

test('exports the ledger as CSV: the file has the rows on screen and ends on the balance shown @scenario:ledger-export-csv', async ({
  page,
}) => {
  await openLedger(page);
  const balance = await readCents(headerFigure(page, 'Current balance'));
  const entries = Number(
    /(\d+) entries/.exec(await page.locator('.pf-ledger-card .sub').innerText())?.[1],
  );
  // The table is newest first, so its first data row is the file's last line.
  const newest = page.locator('.pf-ledger-row').first();
  const newestDate = await newest.getByRole('gridcell').nth(0).innerText();
  const newestDescription = await newest.locator('.desc-text').innerText();

  const file = await captureDownload(page, page.getByRole('button', { name: 'Export' }), {
    urlPart: '/ledger.csv',
    contentType: 'text/csv',
  });
  const lines = file.toString('utf8').trim().split(/\r?\n/);

  expect(lines[0]).toBe('Date,Category,Description,Internal note,Charge,Payment,Balance,Status');
  expect(lines.length - 1).toBe(entries);
  const last = lines.at(-1) ?? '';
  expect(last.startsWith(`${newestDate},`)).toBe(true);
  expect(last).toContain(newestDescription);
  // Balance is the column before Status, and neither holds a comma.
  expect(toCents(last.split(',').at(-2) ?? '')).toBe(balance);
});
