import { expect, type Locator, type Page } from '@playwright/test';
import { runA11y, SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// Transferring a property to another owner (#555, ADR-036), driven in the scenario organization
// (`seed --org scenario`, ADR-056) as a staff member. 14 Harborview Ln holds two tenants' security
// deposits, which move to the buyer with the property; no other spec touches the property, its
// tenants or the two owners it passes between.
//
// A transfer is append-only and each one must be dated after the one before it, so this test cannot
// undo itself. It hands the property to whichever of the two owners does not hold it, and CI's
// retries each date theirs a day later than the attempt before (today − 2, − 1, today), so a retry
// against the journal a failed attempt left behind still has a valid date. Like the M4 banking specs,
// a second plain run against the same database needs a fresh seed (`npm run e2e:fresh`).
const PROPERTY = '14 Harborview Ln';
const OWNERS = ['Harborview Holdings', 'Meridian Estates'] as const;
// Avery Whitfield's $1,200 and Rosa Delgado's $1,000, held since the cutover; nothing else moves them.
const HELD_FOR_PROPERTY_CENTS = 220_000;

/** The viewer's calendar date `daysAgo` days back, as the date field takes it. */
function localDaysAgo(daysAgo: number): string {
  const date = new Date();
  date.setDate(date.getDate() - daysAgo);
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** Reads a figure the screen shows (`$2,200.00`, or the dash that stands for zero) as cents. */
function toCents(text: string): number {
  const digits = text.replace(/[^0-9.]/g, '');
  return digits === '' ? 0 : Math.round(Number(digits) * 100);
}

async function openList(page: Page, name: 'Owners' | 'Properties'): Promise<void> {
  await page.getByRole('navigation').getByRole('button', { name, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/${name.toLowerCase()}$`));
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
}

/** Each owner's "Deposits held" on the owner list, in cents. */
async function depositsHeld(page: Page): Promise<Record<string, number>> {
  await openList(page, 'Owners');
  const held: Record<string, number> = {};
  for (const owner of OWNERS) {
    // Name, units, properties, operating, deposits held, total.
    const cell: Locator = page.locator('tbody tr').filter({ hasText: owner }).locator('td').nth(4);
    await expect(cell).toBeVisible();
    held[owner] = toCents(await cell.innerText());
  }
  return held;
}

async function openProperty(page: Page): Promise<void> {
  await openList(page, 'Properties');
  await page.getByRole('textbox', { name: 'Filter properties…' }).fill(PROPERTY);
  await page.locator('tbody tr').filter({ hasText: PROPERTY }).click();
  await expect(page.getByRole('heading', { name: PROPERTY })).toBeVisible();
}

test('a staff member transfers a property to another owner and its held deposits move with it @scenario:property-ownership-transfer', async ({
  page,
}, testInfo) => {
  await signIn(page, SCENARIO_STAFF);
  const before = await depositsHeld(page);

  await openProperty(page);
  const sub = page.locator('.pf-detailhd .sub');
  await expect(sub).toContainText('Owner: ');
  const subText = await sub.innerText();
  const from = OWNERS.find((owner) => subText.includes(`Owner: ${owner}`));
  if (!from)
    throw new Error(`${PROPERTY} is held by neither of ${OWNERS.join(' and ')}: ${subText}`);
  const to = OWNERS.find((owner) => owner !== from)!;

  await page.getByRole('button', { name: 'Transfer ownership' }).click();
  const dialog = page.getByRole('dialog', { name: `Transfer ${PROPERTY}` });
  await expect(dialog).toContainText('move to the new owner');
  await expect(dialog).toContainText("does not settle the current owner's equity");
  await expect(dialog.getByLabel('New owner')).toBeVisible();
  await runA11y(page);

  // A future date is refused in the dialog before anything posts.
  const date = dialog.getByLabel('Effective date');
  await dialog.getByLabel('New owner').selectOption({ label: to });
  await date.fill(localDaysAgo(-1));
  await date.press('Enter');
  await expect(dialog.getByRole('alert')).toContainText('can’t be in the future');

  // Confirmed from the keyboard: Enter in the date field.
  const retries = testInfo.project.retries;
  const effective = localDaysAgo(retries - Math.min(testInfo.retry, retries));
  await date.fill(effective);
  await date.press('Enter');
  await expect(dialog).toBeHidden();
  await expect(page.getByRole('status').filter({ hasText: 'Transferred to' })).toHaveText(
    `Transferred to ${to}, effective ${effective}. The held security deposits moved with it.`,
  );
  await expect(sub).toContainText(`Owner: ${to}`);

  // What the seller held for this property is now the buyer's, to the cent.
  const after = await depositsHeld(page);
  expect(before[from]! - after[from]!).toBe(HELD_FOR_PROPERTY_CENTS);
  expect(after[to]! - before[to]!).toBe(HELD_FOR_PROPERTY_CENTS);
});
