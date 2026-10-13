import { expect, type Locator, type Page } from '@playwright/test';
import { SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// Creating directory records (#544), driven in the scenario organization (`seed --org scenario`,
// ADR-056) as a staff member.
//
// Every record a test writes is its own, under a name no other run used, so a rerun or a CI retry
// adds records and collides with nothing. No seeded record is changed: the property is created for
// an owner the same test created, and nothing here has a unit, a lease or a journal entry. The
// organization's figures and the invariants are left as the seed wrote them.
//
// The lists load one page of 200 rows in name order and filter it in the browser. Each test types
// the record's name into the filter and asserts on the one row left, so nothing depends on where a
// row sits or on how many rows earlier runs left behind.

/** A per-run suffix, so a record is found by text that no other run wrote. */
function runId(): string {
  return Math.random().toString(36).slice(2, 8).padEnd(6, '0');
}

type ListName = 'Owners' | 'Properties' | 'Tenants';

// The sidebar item and a detail page's back button share a name, so the sidebar is named by its
// landmark. The list's own heading says the route has rendered.
async function openList(page: Page, name: ListName): Promise<void> {
  await page.getByRole('navigation').getByRole('button', { name, exact: true }).click();
  await expect(page).toHaveURL(new RegExp(`/${name.toLowerCase()}$`));
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
}

/** Filters the open list to `text` and returns the one row that is left. */
async function onlyRow(page: Page, list: ListName, text: string): Promise<Locator> {
  await page.getByRole('textbox', { name: `Filter ${list.toLowerCase()}…` }).fill(text);
  const rows = page.locator('tbody tr');
  await expect(rows).toHaveCount(1);
  await expect(rows.first()).toContainText(text);
  return rows.first();
}

/** Counts the create requests a page sends, so a refusal in the form can be told from one by the server. */
function countPosts(page: Page, path: string): () => number {
  let posts = 0;
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname === path) posts += 1;
  });
  return () => posts;
}

/**
 * Creates from the keyboard. These dialogs have no submit button inside the form, so Enter in a
 * field submits nothing; the keyboard path is Tab past Cancel to the create button, and Enter there.
 */
async function createByKeyboard(page: Page, create: Locator): Promise<void> {
  await page.keyboard.press('Tab');
  await page.keyboard.press('Tab');
  await expect(create).toBeFocused();
  await page.keyboard.press('Enter');
}

test('creates an owner by keyboard: a fee over 100% is refused, and the new owner shows on its page and in the list @scenario:directory-create-owner', async ({
  page,
}) => {
  const id = runId();
  const name = `e2e Owner ${id}`;
  const email = `e2e-owner-${id}@example.test`;
  const posts = countPosts(page, '/api/directory/owners');

  await signIn(page, SCENARIO_STAFF);
  await openList(page, 'Owners');
  await page.getByRole('button', { name: 'New owner' }).click();
  const dialog = page.getByRole('dialog', { name: 'New owner' });
  const create = dialog.getByRole('button', { name: 'Create owner' });

  // The name is required: the dialog opens on it, and until it is filled the button is disabled.
  await expect(dialog.getByLabel('Name')).toBeFocused();
  await expect(create).toBeDisabled();

  // A management fee over 100% is refused by the server, in place, and no owner is created.
  await dialog.getByLabel('Name').fill(name);
  await dialog.getByLabel('Mgmt fee %').fill('150');
  await create.click();
  await expect(dialog.getByRole('alert')).toContainText(
    'The management fee must be between 0 and 10000 basis points.',
  );
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(1);

  // Keyboard path: correct the fee, fill the rest, and create from the keyboard. Email is the last
  // field, so the footer's buttons follow it.
  await dialog.getByLabel('Mgmt fee %').fill('7.5');
  await dialog.getByLabel('Initials').fill('eo');
  await expect(dialog.getByLabel('Initials')).toHaveValue('EO');
  await dialog.getByLabel('Email').fill(email);
  await createByKeyboard(page, create);

  // Creating opens the owner's page, which shows what was entered.
  await expect(page).toHaveURL(/\/owners\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
  const content = page.locator('.pf-page');
  await expect(content).toContainText('Default management fee · 7.50%');
  await expect(content.locator('.pf-kv').filter({ hasText: 'Email' })).toContainText(email);
  await expect(content).toContainText('0 on file.');
  expect(posts()).toBe(2);

  // And it is in the list, with no units or properties yet.
  await content.getByRole('button', { name: 'Owners' }).click();
  await expect(page).toHaveURL(/\/owners$/);
  const row = await onlyRow(page, 'Owners', name);
  await expect(row.locator('td').nth(0)).toContainText('EO');
  await expect(row.locator('td').nth(1)).toHaveText('0');
  await expect(row.locator('td').nth(2)).toHaveText('0');
});

test('creates a property for a new owner by keyboard: a blank address is refused, and the property shows on its page, in the list and under its owner @scenario:directory-create-property', async ({
  page,
}) => {
  const id = runId();
  const owner = `e2e Owner ${id}`;
  const address = `${100 + Math.floor(Math.random() * 900)} e2e Lane ${id}`;
  const posts = countPosts(page, '/api/directory/properties');

  // The property's owner is one this test creates, so no seeded owner gains a property.
  await signIn(page, SCENARIO_STAFF);
  await openList(page, 'Owners');
  await page.getByRole('button', { name: 'New owner' }).click();
  const ownerDialog = page.getByRole('dialog', { name: 'New owner' });
  await ownerDialog.getByLabel('Name').fill(owner);
  await ownerDialog.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByRole('heading', { name: owner, level: 2 })).toBeVisible();

  await openList(page, 'Properties');
  await page.getByRole('button', { name: 'New property' }).click();
  const dialog = page.getByRole('dialog', { name: 'New property' });
  const create = dialog.getByRole('button', { name: 'Create property' });

  // An owner and an address are both required. With an address and no owner the button is disabled.
  await dialog.getByLabel('Address').fill(address);
  await expect(create).toBeDisabled();

  // The owner list offers the new owner. A blank address is refused by the server, in place.
  await dialog.getByLabel('Owner').selectOption({ label: owner });
  await dialog.getByLabel('Address').fill('   ');
  await create.click();
  await expect(dialog.getByRole('alert')).toContainText("'Address' must not be empty.");
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(1);

  // Keyboard path: fill the address and the city, keep the default state, and create from the
  // keyboard. State is the last field, so the footer's buttons follow it.
  await dialog.getByLabel('Address').fill(address);
  await dialog.getByLabel('City').fill('Durham');
  await expect(dialog.getByLabel('State')).toHaveValue('NC');
  await dialog.getByLabel('State').focus();
  await createByKeyboard(page, create);

  // Creating opens the property's page: the address, the city and state, the owner, and no units.
  await expect(page).toHaveURL(/\/properties\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name: address, level: 2 })).toBeVisible();
  const content = page.locator('.pf-page');
  await expect(content).toContainText(`Durham, NC · Owner: ${owner}`);
  await expect(content).toContainText('No units on this property yet.');
  await expect(content).toContainText('No current tenants.');
  expect(posts()).toBe(2);

  // It is in the list, found by its address and by its owner's name.
  await content.getByRole('button', { name: 'Properties' }).click();
  await expect(page).toHaveURL(/\/properties$/);
  let row = await onlyRow(page, 'Properties', address);
  await expect(row.locator('td').nth(1)).toHaveText('Durham');
  await expect(row.locator('td').nth(2)).toHaveText(owner);
  await expect(row.locator('td').nth(3)).toHaveText('0');
  await expect(row.locator('td').nth(4)).toHaveText('0/0 occupied');
  row = await onlyRow(page, 'Properties', owner);
  await expect(row.locator('td').nth(0)).toHaveText(address);

  // And the owner now has one property, on the list and on the owner's own page. Creating a
  // property does not refresh the owner list or the owner's page, which the browser keeps for 30
  // seconds, so the page is reloaded to read what is stored.
  await openList(page, 'Owners');
  await page.reload();
  await expect(page.getByRole('heading', { name: 'Owners', level: 2 })).toBeVisible();
  row = await onlyRow(page, 'Owners', owner);
  await expect(row.locator('td').nth(2)).toHaveText('1');
  await row.click();
  await expect(page.getByRole('heading', { name: owner, level: 2 })).toBeVisible();
  await expect(content).toContainText('1 on file.');
  await expect(content.locator('tbody tr').filter({ hasText: address })).toContainText('Durham');
});

test('creates a tenant by keyboard: a malformed email is refused, and the new tenant shows on its page and in the list @scenario:directory-create-tenant', async ({
  page,
}) => {
  const id = runId();
  const name = `e2e Tenant ${id}`;
  const phone = `919-555-${String(1000 + Math.floor(Math.random() * 9000))}`;
  const posts = countPosts(page, '/api/directory/tenants');

  await signIn(page, SCENARIO_STAFF);
  await openList(page, 'Tenants');
  await page.getByRole('button', { name: 'New tenant' }).click();
  const dialog = page.getByRole('dialog', { name: 'New tenant' });
  const create = dialog.getByRole('button', { name: 'Create tenant' });

  // The display name is required: the dialog opens on it, and until it is filled the button is
  // disabled.
  await expect(dialog.getByLabel('Display name')).toBeFocused();
  await expect(create).toBeDisabled();

  // A malformed email is refused by the server, in place, and no tenant is created.
  await dialog.getByLabel('Display name').fill(name);
  await dialog.getByLabel('Email').fill('not-an-email');
  await create.click();
  await expect(dialog.getByRole('alert')).toContainText('is not a valid email address.');
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(1);

  // Keyboard path: correct the email, fill the rest, and create from the keyboard. Lifecycle is the
  // last field, so the footer's buttons follow it. The tenant is created as a past one, with no
  // lease, so no rent run or late-fee run picks it up.
  await dialog.getByLabel('Email').fill(`e2e-tenant-${id}@example.test`);
  await dialog.getByLabel('Phone').fill(phone);
  await dialog.getByLabel('Lifecycle').selectOption('Past');
  await dialog.getByLabel('Lifecycle').focus();
  await createByKeyboard(page, create);

  // Creating opens the tenant's ledger, whose header shows what was entered and an empty position.
  await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
  const header = page.locator('.pf-tenant-hd');
  await expect(header.locator('.pf-badge').first()).toHaveText('Past');
  await expect(header).toContainText('No unit');
  await expect(header).toContainText(phone);
  await expect(header.locator('.pf-tstat').filter({ hasText: 'Current balance' })).toContainText(
    '—',
  );
  expect(posts()).toBe(2);

  // And it is in the list, with no unit, no rent and no balance.
  await openList(page, 'Tenants');
  const row = await onlyRow(page, 'Tenants', name);
  await expect(row.locator('td').nth(1)).toHaveText('—');
  await expect(row.locator('td').nth(2)).toHaveText('—');
  await expect(row.locator('td').nth(3)).toHaveText('—');
  await expect(row.locator('td').nth(4)).toHaveText('Past');
  await expect(row.locator('td').nth(5)).toHaveText('No alerts');
});
