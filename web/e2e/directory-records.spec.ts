import { expect, type Locator, type Page } from '@playwright/test';
import {
  openPalette,
  orgToday,
  paletteOptions,
  SCENARIO_ADMIN,
  SCENARIO_STAFF,
  signIn,
} from './helpers';
import { test } from './test';

// Creating directory records (#544), driven in the scenario organization (`seed --org scenario`,
// ADR-056) as a staff member.
//
// Every record a test writes is its own, under a name no other run used, so a rerun or a CI retry
// adds records and collides with nothing. No seeded record is changed: each property is created for
// an owner the same test created, a unit only on such a property, and a lease only between a tenant
// and a unit the same test created. Nothing here posts a journal entry, so the organization's
// figures and the invariants are left as the seed wrote them.
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
 * Creates an owner and a property of theirs, both named for this run, and leaves the property's page
 * open. A unit added there, and a lease on it, then touch no seeded record.
 */
async function newOwnerAndProperty(
  page: Page,
  id: string,
): Promise<{ owner: string; address: string }> {
  const owner = `e2e Owner ${id}`;
  const address = `${100 + Math.floor(Math.random() * 900)} e2e Court ${id}`;
  await openList(page, 'Owners');
  await page.getByRole('button', { name: 'New owner' }).click();
  const ownerDialog = page.getByRole('dialog', { name: 'New owner' });
  await ownerDialog.getByLabel('Name').fill(owner);
  await ownerDialog.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByRole('heading', { name: owner, level: 2 })).toBeVisible();
  await openList(page, 'Properties');
  await page.getByRole('button', { name: 'New property' }).click();
  const propertyDialog = page.getByRole('dialog', { name: 'New property' });
  await propertyDialog.getByLabel('Owner').selectOption({ label: owner });
  await propertyDialog.getByLabel('Address').fill(address);
  await propertyDialog.getByRole('button', { name: 'Create property' }).click();
  await expect(page.getByRole('heading', { name: address, level: 2 })).toBeVisible();
  return { owner, address };
}

/** The open property page's Units card. */
function unitsCard(page: Page): Locator {
  return page
    .locator('.pf-page .pf-card')
    .filter({ has: page.getByRole('heading', { name: 'Units' }) });
}

/**
 * A field the browser refused before anything was sent (#561): submitting runs the fields' own
 * constraints, reports the first that fails and moves focus to it. The message is the browser's
 * own, in its own words, so only the failed constraint is asserted.
 */
async function expectRefusedInPlace(
  field: Locator,
  constraint: 'rangeOverflow' | 'typeMismatch' | 'valueMissing',
): Promise<void> {
  await expect(field).toBeFocused();
  expect(await field.evaluate((el: HTMLInputElement, c) => el.validity[c], constraint)).toBe(true);
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

  // A management fee over 100% is refused in the browser, from Enter in a field or from the button,
  // and nothing is sent.
  await dialog.getByLabel('Name').fill(name);
  await dialog.getByLabel('Mgmt fee %').fill('150');
  await page.keyboard.press('Enter');
  await create.click();
  await expectRefusedInPlace(dialog.getByLabel('Mgmt fee %'), 'rangeOverflow');
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(0);

  // Keyboard path: correct the fee to one with two decimals, fill the rest, and press Enter in the
  // last field.
  await dialog.getByLabel('Mgmt fee %').fill('7.25');
  await dialog.getByLabel('Initials').fill('eo');
  await expect(dialog.getByLabel('Initials')).toHaveValue('EO');
  await dialog.getByLabel('Email').fill(email);
  await page.keyboard.press('Enter');

  // Creating opens the owner's page, which shows what was entered.
  await expect(page).toHaveURL(/\/owners\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
  const content = page.locator('.pf-page');
  await expect(content).toContainText('Default management fee · 7.25%');
  await expect(content.locator('.pf-kv').filter({ hasText: 'Email' })).toContainText(email);
  await expect(content).toContainText('0 on file.');
  expect(posts()).toBe(1);

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

  // The owner list offers the new owner. An address of only spaces satisfies the browser's
  // `required`, so it is the server that refuses it, in place.
  await dialog.getByLabel('Owner').selectOption({ label: owner });
  await dialog.getByLabel('Address').fill('   ');
  await create.click();
  await expect(dialog.getByRole('alert')).toContainText("'Address' must not be empty.");
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(1);

  // Keyboard path: fill the address and the city, keep the default state, and press Enter in the
  // city.
  await dialog.getByLabel('Address').fill(address);
  await dialog.getByLabel('City').fill('Durham');
  await expect(dialog.getByLabel('State')).toHaveValue('NC');
  await page.keyboard.press('Enter');

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

  // And the owner now has one property, on the list and on the owner's own page, with no reload:
  // creating the property refreshes both (#559). The owner's page was last read when the owner was
  // created, with no properties, well inside the 30 seconds the browser would otherwise keep it.
  await openList(page, 'Owners');
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

  // A malformed email is refused in the browser, from Enter in a field or from the button, and
  // nothing is sent.
  await dialog.getByLabel('Display name').fill(name);
  await dialog.getByLabel('Email').fill('not-an-email');
  await page.keyboard.press('Enter');
  await create.click();
  await expectRefusedInPlace(dialog.getByLabel('Email'), 'typeMismatch');
  await expect(dialog).toBeVisible();
  expect(posts()).toBe(0);

  // Keyboard path: correct the email, fill the rest, and press Enter in the phone field. The tenant
  // is created as a past one, with no lease, so no rent run or late-fee run picks it up.
  await dialog.getByLabel('Email').fill(`e2e-tenant-${id}@example.test`);
  await dialog.getByLabel('Lifecycle').selectOption('Past');
  await dialog.getByLabel('Phone').fill(phone);
  await page.keyboard.press('Enter');

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
  expect(posts()).toBe(1);

  // And it is in the list, with no unit, no rent and no balance.
  await openList(page, 'Tenants');
  const row = await onlyRow(page, 'Tenants', name);
  await expect(row.locator('td').nth(1)).toHaveText('—');
  await expect(row.locator('td').nth(2)).toHaveText('—');
  await expect(row.locator('td').nth(3)).toHaveText('—');
  await expect(row.locator('td').nth(4)).toHaveText('Past');
  await expect(row.locator('td').nth(5)).toHaveText('No alerts');
});

test('adds a unit to a property by keyboard and edits it: a blank label is refused, and the unit counts follow @scenario:directory-add-edit-unit', async ({
  page,
}) => {
  const id = runId();
  let unitWrites = 0;
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/api/directory/units') && request.method() !== 'GET') unitWrites += 1;
  });

  // The unit goes on a property this test creates, for an owner it creates, so no seeded property
  // gains a unit and no seeded owner's counts move.
  await signIn(page, SCENARIO_STAFF);
  const { owner, address } = await newOwnerAndProperty(page, id);
  const content = page.locator('.pf-page');
  await expect(content).toContainText('No units on this property yet.');

  // Add unit opens on the label, and a unit is available unless chosen otherwise. A label of only
  // spaces satisfies the browser's `required`, so it is the server that refuses it, in place.
  // Keyboard path throughout: each dialog opens from its focused button and saves on Enter.
  await page.getByRole('button', { name: 'Add unit' }).focus();
  await page.keyboard.press('Enter');
  const add = page.getByRole('dialog', { name: `Add a unit to ${address}` });
  await expect(add.getByLabel('Label')).toBeFocused();
  await expect(add.getByLabel('Availability')).toHaveValue('available');
  await add.getByLabel('Label').fill('   ');
  await add.getByLabel('Asking rent').fill('1295');
  await page.keyboard.press('Enter');
  await expect(add.getByRole('alert')).toContainText("'Label' must not be empty.");
  await expect(add).toBeVisible();
  expect(unitWrites).toBe(1);

  // Give it a label and press Enter in the rent. The Units card lists it, vacant.
  await add.getByLabel('Label').fill('#1A');
  await add.getByLabel('Asking rent').focus();
  await page.keyboard.press('Enter');
  await expect(add).toBeHidden();
  const units = unitsCard(page);
  const unitRow = units.locator('tbody tr').filter({ hasText: '#1A' });
  await expect(unitRow).toContainText('$1,295.00');
  await expect(unitRow).toContainText('Vacant');
  await expect(unitRow).toContainText('Available');
  await expect(units).toContainText('1 unit(s)');
  expect(unitWrites).toBe(2);

  // Edit opens on the unit as it is. Raise the asking rent and retire the unit.
  await unitRow.getByRole('button', { name: 'Edit unit #1A' }).focus();
  await page.keyboard.press('Enter');
  const edit = page.getByRole('dialog', { name: 'Edit unit #1A' });
  await expect(edit.getByLabel('Label')).toHaveValue('#1A');
  await expect(edit.getByLabel('Asking rent')).toHaveValue('1295');
  await edit.getByLabel('Availability').selectOption('Unavailable');
  await edit.getByLabel('Asking rent').fill('1325.50');
  await page.keyboard.press('Enter');
  await expect(edit).toBeHidden();
  await expect(unitRow).toContainText('$1,325.50');
  await expect(unitRow).toContainText('Unavailable');
  expect(unitWrites).toBe(3);

  // The property's and the owner's unit counts follow with no reload (#559's pattern): both lists
  // were last read before the unit existed, well inside the 30 seconds the browser would keep them.
  await content.getByRole('button', { name: 'Properties' }).click();
  let row = await onlyRow(page, 'Properties', address);
  await expect(row.locator('td').nth(3)).toHaveText('1');
  await expect(row.locator('td').nth(4)).toHaveText('0/1 occupied');
  await openList(page, 'Owners');
  row = await onlyRow(page, 'Owners', owner);
  await expect(row.locator('td').nth(1)).toHaveText('1');
});

test('sets up a tenancy with no import, by keyboard: property, unit, tenant, then a lease from the ledger, with an overlapping term refused and nothing posted @scenario:directory-add-lease', async ({
  page,
}) => {
  const id = runId();
  const tenant = `e2e Lease Tenant ${id}`;
  const today = orgToday();
  const leasePosts = countPosts(page, '/api/directory/leases');

  // A property and a unit of this test's own, so the lease touches no seeded unit or tenant.
  await signIn(page, SCENARIO_STAFF);
  const { address } = await newOwnerAndProperty(page, id);
  await page.getByRole('button', { name: 'Add unit' }).focus();
  await page.keyboard.press('Enter');
  const addUnit = page.getByRole('dialog', { name: `Add a unit to ${address}` });
  await addUnit.getByLabel('Label').fill('#1A');
  await addUnit.getByLabel('Asking rent').fill('1295');
  await page.keyboard.press('Enter');
  await expect(addUnit).toBeHidden();
  await expect(unitsCard(page).locator('tbody tr').filter({ hasText: '#1A' })).toContainText(
    'Vacant',
  );
  const propertyPage = page.url();

  // A current tenant, whose ledger opens with no unit and nothing on it.
  await openList(page, 'Tenants');
  await page.getByRole('button', { name: 'New tenant' }).click();
  const newTenant = page.getByRole('dialog', { name: 'New tenant' });
  await newTenant.getByLabel('Display name').fill(tenant);
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: tenant, level: 2 })).toBeVisible();
  const header = page.locator('.pf-tenant-hd');
  await expect(header).toContainText('No unit');

  // Add lease, from the ledger header by keyboard. The unit is offered as vacant, in words, and its
  // asking rent fills the lease rent, which is lowered before saving. The term starts today, with no
  // end: a month-to-month lease, in effect at once.
  await page.getByRole('button', { name: 'Add lease' }).focus();
  await page.keyboard.press('Enter');
  const addLease = page.getByRole('dialog', { name: `Add a lease for ${tenant}` });
  const unitPicker = addLease.getByLabel('Unit');
  await addLease.getByLabel('Property').selectOption({ label: address });
  await expect(unitPicker.getByRole('option', { name: '#1A · vacant' })).toBeAttached();
  await unitPicker.selectOption({ label: '#1A · vacant' });
  await expect(addLease.getByLabel('Lease rent')).toHaveValue('1295');
  await expect(addLease.getByLabel('Lifecycle')).toHaveValue('active');
  await addLease.getByLabel('Lease rent').fill('1250');
  await addLease.getByLabel('Start date').fill(today);
  await addLease.getByLabel('Deposit required').fill('1250');
  await page.keyboard.press('Enter');
  await expect(addLease).toBeHidden();
  expect(leasePosts()).toBe(1);

  // The header shows the lease and its unit with no reload. Creating it posted nothing: the ledger
  // is empty and nothing is owed or held.
  await expect(header).toContainText('#1A');
  await expect(header).toContainText(`Lease ${today} – —`);
  await expect(page.locator('.pf-ledger-card')).toContainText('0 entries');
  await expect(header.locator('.pf-tstat').filter({ hasText: 'Current balance' })).toContainText(
    '—',
  );
  await expect(header.locator('.pf-tstat').filter({ hasText: 'Deposit held' })).toContainText(
    '$0.00',
  );

  // A second lease on the same unit from the same day overlaps the first, and the server refuses it
  // in the dialog: nothing is created. The unit is now offered as occupied.
  await page.getByRole('button', { name: 'Add lease' }).focus();
  await page.keyboard.press('Enter');
  await addLease.getByLabel('Property').selectOption({ label: address });
  await expect(unitPicker.getByRole('option', { name: '#1A · occupied' })).toBeAttached();
  await unitPicker.selectOption({ label: '#1A · occupied' });
  await addLease.getByLabel('Start date').fill(today);
  await addLease.getByLabel('Deposit required').fill('0');
  await page.keyboard.press('Enter');
  await expect(addLease.getByRole('alert')).toContainText(
    'A tenant cannot have overlapping non-pending lease terms.',
  );
  await expect(addLease.getByRole('alert')).toContainText('Reference:');
  expect(leasePosts()).toBe(2);
  await addLease.getByRole('button', { name: 'Cancel' }).click();
  await expect(addLease).toBeHidden();
  await expect(header).toContainText(`Lease ${today} – —`);

  // The unit reads as occupied wherever occupancy shows, with no reload: the properties list was
  // last read before the lease existed.
  await openList(page, 'Properties');
  const row = await onlyRow(page, 'Properties', address);
  await expect(row.locator('td').nth(4)).toHaveText('1/1 occupied');
  await row.click();
  await expect(page).toHaveURL(propertyPage);
  const unitRow = unitsCard(page).locator('tbody tr').filter({ hasText: '#1A' });
  await expect(unitRow).toContainText('Occupied');
  await expect(unitRow).toContainText('$1,295.00');
});

test('edits an owner by keyboard: the fee rate as a percent, the reserve and the name follow on its page and list, and the audit log keeps the before and after @scenario:directory-edit-owner', async ({
  page,
}) => {
  const id = runId();
  const owner = `e2e Owner ${id}`;
  const renamed = `e2e Owner ${id} Trust`;
  let ownerWrites = 0;
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'PUT' && path.startsWith('/api/directory/owners/')) ownerWrites += 1;
  });

  // An owner of this test's own, created with initials and the default 8% rate. The audit log is an
  // administrator's, so the whole test runs as the scenario organization's administrator.
  await signIn(page, SCENARIO_ADMIN);
  await openList(page, 'Owners');
  await page.getByRole('button', { name: 'New owner' }).click();
  const create = page.getByRole('dialog', { name: 'New owner' });
  await create.getByLabel('Name').fill(owner);
  await create.getByLabel('Initials').fill('EO');
  await create.getByRole('button', { name: 'Create owner' }).click();
  await expect(page.getByRole('heading', { name: owner, level: 2 })).toBeVisible();
  await expect(page.getByText('Default management fee · 8.00%')).toBeVisible();
  const ownerPage = page.url();
  const ownerId = ownerPage.split('/').at(-1) ?? '';

  // Edit opens from its focused button on the owner as it is, initials included, the rate as a
  // percent. Rename, set 8.25% and a reserve floor, and save from the focused button: it sets money
  // policy, so like the other money dialogs it never saves from Enter in a field (#711).
  await page.getByRole('button', { name: 'Edit', exact: true }).focus();
  await page.keyboard.press('Enter');
  const edit = page.getByRole('dialog', { name: `Edit ${owner}` });
  await expect(edit.getByLabel('Name')).toHaveValue(owner);
  await expect(edit.getByLabel('Initials')).toHaveValue('EO');
  await expect(edit.getByLabel('Mgmt fee %')).toHaveValue('8');
  await expect(edit.getByLabel('Reserve floor')).toHaveValue('0');
  await expect(edit).toContainText('A fee-rate or reserve change applies from the next run.');
  await edit.getByLabel('Name').fill(renamed);
  await edit.getByLabel('Mgmt fee %').fill('8.25');
  await edit.getByLabel('Reserve floor').fill('500');
  await page.keyboard.press('Enter');
  await expect(edit).toBeVisible();
  expect(ownerWrites).toBe(0);
  await edit.getByRole('button', { name: 'Save owner' }).focus();
  await page.keyboard.press('Enter');
  await expect(edit).toBeHidden();
  expect(ownerWrites).toBe(1);

  // The page follows with no reload.
  await expect(page.getByRole('heading', { name: renamed, level: 2 })).toBeVisible();
  await expect(page.getByText('Default management fee · 8.25%')).toBeVisible();
  await expect(
    page.locator('.pf-statgrid .pf-card').filter({ hasText: 'Reserve floor' }),
  ).toContainText('$500.00');

  // Reopened, it shows what was saved: the rate round-trips through basis points, and the initials
  // that were never touched are still there.
  await page.getByRole('button', { name: 'Edit', exact: true }).focus();
  await page.keyboard.press('Enter');
  const again = page.getByRole('dialog', { name: `Edit ${renamed}` });
  await expect(again.getByLabel('Mgmt fee %')).toHaveValue('8.25');
  await expect(again.getByLabel('Initials')).toHaveValue('EO');
  await expect(again.getByLabel('Reserve floor')).toHaveValue('500');
  await page.keyboard.press('Escape');
  await expect(again).toBeHidden();

  // The list follows too: it was last read before the rename, well inside its 30-second stale time.
  await openList(page, 'Owners');
  await onlyRow(page, 'Owners', renamed);

  // The audit log keeps the change: this administrator's newest owner update is this one, and its
  // drawer lists the fee rate and reserve before and after.
  await page.getByRole('navigation').getByRole('button', { name: 'Audit log' }).click();
  await expect(page).toHaveURL(/\/audit$/);
  const filters = page.getByRole('group', { name: 'Audit filters' });
  await filters.getByLabel('Record type').selectOption('owners');
  await filters.getByLabel('Actor').selectOption({ label: 'Scenario Admin' });
  const view = page.getByRole('button', { name: /^View owners / }).first();
  await view.focus();
  await page.keyboard.press('Enter');
  const drawer = page.getByRole('dialog', { name: 'Audit event' });
  await expect(drawer).toContainText(ownerId);
  const field = (name: string) => drawer.locator('tbody tr').filter({ hasText: name });
  await expect(field('DefaultMgmtFeeBps').locator('td').nth(1)).toHaveText('800');
  await expect(field('DefaultMgmtFeeBps').locator('td').nth(2)).toHaveText('825');
  // A money column is recorded as the whole money value, so its amount is read out of it.
  await expect(field('ReserveAmount').locator('td').nth(1)).toHaveText(/"Amount": 0(\.00)?,/);
  await expect(field('ReserveAmount').locator('td').nth(2)).toHaveText(/"Amount": 500(\.00)?,/);
  await expect(field('Name').locator('td').nth(2)).toHaveText(renamed);
});

/** Opens the palette on `term` and returns its result rows, closing nothing. */
async function searchFor(page: Page, term: string): Promise<Locator> {
  const search = await openPalette(page);
  await search.fill(term);
  return paletteOptions(page).locator('.label');
}

/**
 * The result labels that read exactly `text`. A tenant's record row is followed by its contextual
 * actions ("Record payment → <name>"), whose labels contain the name too.
 */
function exactly(labels: Locator, text: string): Locator {
  const escaped = text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  return labels.filter({ hasText: new RegExp(`^${escaped}$`) });
}

test('corrects a property and a tenant by keyboard: a blank address is refused, and each change follows on its page, its list, its owner and in search @scenario:directory-edit-property-tenant', async ({
  page,
}) => {
  const id = runId();
  let propertyWrites = 0;
  let tenantWrites = 0;
  page.on('request', (request) => {
    if (request.method() !== 'PUT') return;
    const path = new URL(request.url()).pathname;
    if (path.startsWith('/api/directory/properties/')) propertyWrites += 1;
    if (path.startsWith('/api/directory/tenants/')) tenantWrites += 1;
  });

  await signIn(page, SCENARIO_STAFF);
  const { owner, address } = await newOwnerAndProperty(page, id);
  const moved = `${100 + Math.floor(Math.random() * 900)} e2e Lane ${id}`;

  // Search finds the property by this run's id under the address it was created with. The search is
  // read again below within its 30-second stale time, so seeing the new address there means the
  // save refreshed it.
  let results = await searchFor(page, id);
  await expect(exactly(results, address)).toHaveCount(1);
  await page.keyboard.press('Escape');

  // Edit opens from its focused button on the property as it is. A blank address is refused in the
  // browser and nothing is sent.
  await page.getByRole('button', { name: 'Edit', exact: true }).focus();
  await page.keyboard.press('Enter');
  const editProperty = page.getByRole('dialog', { name: `Edit ${address}` });
  await expect(editProperty.getByLabel('Address')).toHaveValue(address);
  await expect(editProperty.getByLabel('City')).toHaveValue('');
  await editProperty.getByLabel('Address').fill('');
  await page.keyboard.press('Enter');
  await expectRefusedInPlace(editProperty.getByLabel('Address'), 'valueMissing');
  await expect(editProperty).toBeVisible();
  expect(propertyWrites).toBe(0);

  // Keyboard path: the new address, a city, state and zip, and Enter in the last field.
  await editProperty.getByLabel('Address').fill(moved);
  await editProperty.getByLabel('City').fill('Raleigh');
  await editProperty.getByLabel('State').fill('NC');
  await editProperty.getByLabel('Zip').fill('27601');
  await page.keyboard.press('Enter');
  await expect(editProperty).toBeHidden();
  expect(propertyWrites).toBe(1);

  // The page follows with no reload, its owner unchanged.
  await expect(page.getByRole('heading', { name: moved, level: 2 })).toBeVisible();
  const content = page.locator('.pf-page');
  await expect(content).toContainText(`Raleigh, NC, 27601 · Owner: ${owner}`);

  // So do the list, the owner's page (read when the property was created) and search.
  await openList(page, 'Properties');
  const propertyRow = await onlyRow(page, 'Properties', id);
  await expect(propertyRow.locator('td').nth(0)).toHaveText(moved);
  await expect(propertyRow.locator('td').nth(1)).toHaveText('Raleigh');
  await openList(page, 'Owners');
  await (await onlyRow(page, 'Owners', owner)).click();
  await expect(page.getByRole('heading', { name: owner, level: 2 })).toBeVisible();
  await expect(content.locator('tbody tr').filter({ hasText: moved })).toContainText('Raleigh');
  await expect(content).not.toContainText(address);
  results = await searchFor(page, id);
  await expect(exactly(results, moved)).toHaveCount(1);
  await expect(exactly(results, address)).toHaveCount(0);
  await page.keyboard.press('Escape');

  // A tenant of this run's own, current, with no lease, so no run picks it up.
  const tenant = `e2e Tenant ${id}`;
  const renamed = `e2e Tenant ${id} Ode`;
  await openList(page, 'Tenants');
  await page.getByRole('button', { name: 'New tenant' }).click();
  const create = page.getByRole('dialog', { name: 'New tenant' });
  await create.getByLabel('Display name').fill(tenant);
  await create.getByLabel('Phone').fill('919-555-0142');
  await page.keyboard.press('Enter');
  await expect(page.getByRole('heading', { name: tenant, level: 2 })).toBeVisible();

  // Read the list and search under the name it was created with, then return to its ledger.
  await openList(page, 'Tenants');
  await expect((await onlyRow(page, 'Tenants', tenant)).locator('td').nth(4)).toHaveText('Current');
  results = await searchFor(page, tenant);
  await expect(exactly(results, tenant)).toHaveCount(1);
  await page.keyboard.press('Escape');
  await (await onlyRow(page, 'Tenants', tenant)).click();
  await expect(page.getByRole('heading', { name: tenant, level: 2 })).toBeVisible();

  // Edit opens from its focused button on the tenant as it is. Rename, add an email, mark the tenant
  // evicting, and press Enter in the phone field.
  await page.getByRole('button', { name: 'Edit', exact: true }).focus();
  await page.keyboard.press('Enter');
  const editTenant = page.getByRole('dialog', { name: `Edit ${tenant}` });
  await expect(editTenant.getByLabel('Display name')).toHaveValue(tenant);
  await expect(editTenant.getByLabel('Phone')).toHaveValue('919-555-0142');
  await expect(editTenant.getByLabel('Lifecycle')).toHaveValue('current');
  await editTenant.getByLabel('Display name').fill(renamed);
  await editTenant.getByLabel('Email').fill(`e2e-tenant-${id}@example.test`);
  await editTenant.getByLabel('Lifecycle').selectOption('Evicting');
  await editTenant.getByLabel('Phone').focus();
  await page.keyboard.press('Enter');
  await expect(editTenant).toBeHidden();
  expect(tenantWrites).toBe(1);

  // The ledger header follows with no reload.
  await expect(page.getByRole('heading', { name: renamed, level: 2 })).toBeVisible();
  const header = page.locator('.pf-tenant-hd');
  await expect(header.locator('.pf-badge').first()).toHaveText('Evicting');
  await expect(header).toContainText('919-555-0142');

  // So do the list and search, both read under the old name moments ago.
  await openList(page, 'Tenants');
  const tenantRow = await onlyRow(page, 'Tenants', renamed);
  await expect(tenantRow.locator('td').nth(4)).toHaveText('Evicting');
  results = await searchFor(page, tenant);
  await expect(exactly(results, renamed)).toHaveCount(1);
  await expect(exactly(results, tenant)).toHaveCount(0);
});
