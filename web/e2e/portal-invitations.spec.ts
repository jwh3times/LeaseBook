import { randomBytes } from 'node:crypto';
import { expect, type Locator, type Page } from '@playwright/test';
import { runA11y, SCENARIO_ADMIN, SCENARIO_STAFF, signIn } from './helpers';
import { test } from './test';

// Portal invitations after they are sent (#544): replacing one, cancelling one, and the setting that
// decides whether staff may manage them at all. Driven in the scenario organization, on a tenant no
// other spec opens, with an invitation address made for the run. `portal-enrollment.spec.ts` sends
// and accepts invitations in the demo organization and is not affected by anything here.
//
// Nothing is emailed. In Development an invitation is written to the local test inbox, and the
// inbox page is where its link is read, as the enrollment spec reads it.
const TENANT = 'Silas Byrd';
const PERMISSION = 'Allow staff to manage portal invitations and access';
const DENIED = 'Portal access management is unavailable for this account.';

function accessPanel(page: Page): Locator {
  return page
    .locator('.pf-card')
    .filter({ has: page.getByRole('heading', { name: 'Portal access' }) });
}

async function openTenant(page: Page): Promise<Locator> {
  await page.goto('/tenants');
  await page.getByRole('row').filter({ hasText: TENANT }).click();
  await expect(page.getByRole('heading', { name: TENANT })).toBeVisible();
  const panel = accessPanel(page);
  await expect(panel.getByRole('heading', { name: 'Invitations' })).toBeVisible();
  return panel;
}

// The link the test inbox holds for an address, or none. The inbox lists pending invitations only.
async function inboxLink(page: Page, email: string): Promise<Locator> {
  await page.goto('/portal/test-inbox');
  await expect(page.getByRole('heading', { name: 'Portal test inbox' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Refresh inbox' })).toBeEnabled();
  return page.getByRole('link', { name: `Open invitation for ${email}` });
}

// Delivery to the test inbox follows the invitation by up to a second, so the inbox is refreshed
// until the link is there.
async function deliveredLink(page: Page, email: string): Promise<string> {
  const link = await inboxLink(page, email);
  await expect(async () => {
    await page.getByRole('button', { name: 'Refresh inbox' }).click();
    await expect(link).toHaveCount(1, { timeout: 1000 });
  }).toPass({ timeout: 15_000 });
  return (await link.getAttribute('href'))!;
}

// What the person holding an invitation link sees when they open it, in a browser of their own.
async function openAsRecipient(recipient: Page, href: string): Promise<void> {
  // The token rides in the fragment, which the page reads once and then removes. Leaving the page
  // first makes the next link a fresh load and not a fragment change on the same document.
  await recipient.goto('about:blank');
  await recipient.goto(href);
  await expect(
    recipient.getByRole('heading', { name: 'Join your LeaseBook portal' }),
  ).toBeVisible();
}

test('a staff member replaces a pending invitation, then cancels the replacement, and each old link stops working @scenario:portal-invitation-replace-and-cancel', async ({
  page,
  browser,
}) => {
  const email = `invitation-${randomBytes(6).toString('hex')}@example.test`;
  await signIn(page, SCENARIO_STAFF);
  let panel = await openTenant(page);
  const tenantUrl = page.url();
  // Newest first: the invitations sent to this run's address, and to no other.
  const invitations = (within: Locator) =>
    within.locator('div.col.gap6').filter({ hasText: email });

  await panel.getByLabel('Invitation email').fill(email);
  await panel.getByLabel('Invitation email').press('Enter');
  await expect(invitations(panel)).toHaveCount(1);
  await expect(invitations(panel).first()).toContainText('pending');
  await expect(invitations(panel).first()).toContainText('Available in test inbox', {
    timeout: 15_000,
  });
  const firstLink = await deliveredLink(page, email);

  const recipientContext = await browser.newContext();
  try {
    const recipient = await recipientContext.newPage();
    await openAsRecipient(recipient, firstLink);
    await expect(
      recipient.getByText(`You are accepting tenant portal access for ${email}.`),
    ).toBeVisible();

    // Replace, from the keyboard. The first invitation stays in the list as replaced and offers
    // nothing more; the new one is pending and is delivered in its turn.
    await page.goto(tenantUrl);
    panel = accessPanel(page);
    await invitations(panel).first().getByRole('button', { name: 'Replace invitation' }).focus();
    await page.keyboard.press('Enter');
    await expect(invitations(panel)).toHaveCount(2);
    await expect(invitations(panel).nth(0)).toContainText('pending');
    await expect(invitations(panel).nth(0)).toContainText('Available in test inbox', {
      timeout: 15_000,
    });
    await expect(invitations(panel).nth(1)).toContainText('replaced');
    await expect(invitations(panel).nth(1).getByRole('button')).toHaveCount(0);
    await runA11y(page);

    // The inbox now holds one link for the address, and it is not the first one.
    const secondLink = await deliveredLink(page, email);
    expect(secondLink).not.toBe(firstLink);
    await openAsRecipient(recipient, firstLink);
    await expect(
      recipient.getByText('This invitation is unavailable or has expired.'),
    ).toBeVisible();
    await expect(
      recipient.getByText('Ask your property manager for a new invitation', { exact: false }),
    ).toBeVisible();
    await expect(recipient.getByRole('button', { name: 'Accept invitation' })).toHaveCount(0);
    await openAsRecipient(recipient, secondLink);
    await expect(
      recipient.getByText(`You are accepting tenant portal access for ${email}.`),
    ).toBeVisible();

    // Cancel the replacement, from the keyboard. Nothing for the address is left to accept.
    await page.goto(tenantUrl);
    panel = accessPanel(page);
    await invitations(panel).first().getByRole('button', { name: 'Cancel invitation' }).focus();
    await page.keyboard.press('Enter');
    await expect(invitations(panel).nth(0)).toContainText('cancelled');
    await expect(invitations(panel).nth(1)).toContainText('replaced');
    await expect(invitations(panel).getByRole('button')).toHaveCount(0);
    await expect(panel.getByText('No active portal users.')).toBeVisible();

    const gone = await inboxLink(page, email);
    await page.getByRole('button', { name: 'Refresh inbox' }).click();
    await expect(page.getByRole('button', { name: 'Refresh inbox' })).toBeEnabled();
    await expect(gone).toHaveCount(0);
    await openAsRecipient(recipient, secondLink);
    await expect(
      recipient.getByText('This invitation is unavailable or has expired.'),
    ).toBeVisible();
    await expect(recipient.getByRole('button', { name: 'Accept invitation' })).toHaveCount(0);
  } finally {
    await recipientContext.close();
  }
});

test('an administrator stops staff managing portal access, and allows it again @scenario:portal-access-staff-permission', async ({
  page,
  browser,
}) => {
  await signIn(page, SCENARIO_ADMIN);
  await page.goto('/settings');
  const permissions = page
    .locator('.pf-card')
    .filter({ has: page.getByRole('heading', { name: 'Portal permissions' }) });
  const allow = permissions.getByLabel(PERMISSION);
  const save = async (allowed: boolean) => {
    await allow.setChecked(allowed);
    await allow.focus();
    // Enter in the checkbox submits the form it is in.
    await page.keyboard.press('Enter');
    await expect(permissions.getByText('Portal permissions saved.')).toBeVisible();
  };
  // The organization is seeded with staff allowed, and this test leaves it that way. A run that was
  // stopped half way may have left it off, so it is put right before anything is asserted.
  if (!(await allow.isChecked())) {
    await save(true);
    await page.reload();
  }
  await expect(allow).toBeChecked();

  const staffContext = await browser.newContext();
  try {
    const staff = await staffContext.newPage();
    await signIn(staff, SCENARIO_STAFF);
    let panel = await openTenant(staff);
    await expect(panel.getByRole('button', { name: 'Send invitation' })).toBeVisible();
    // The setting is the administrator's alone: staff read it and are offered no way to save it.
    await staff.goto('/settings');
    const staffPermissions = staff
      .locator('.pf-card')
      .filter({ has: staff.getByRole('heading', { name: 'Portal permissions' }) });
    await expect(staffPermissions.getByLabel(PERMISSION)).toBeDisabled();
    await expect(
      staffPermissions.getByRole('button', { name: 'Save portal permissions' }),
    ).toHaveCount(0);

    try {
      await save(false);
      await page.reload();
      await expect(allow).not.toBeChecked();
      await runA11y(page);

      panel = await openTenant(staff);
      await expect(panel.getByText(DENIED)).toBeVisible();
      await expect(panel.getByRole('button', { name: 'Send invitation' })).toHaveCount(0);
      await expect(panel.getByRole('link', { name: 'Open test inbox' })).toHaveCount(0);
      await expect(panel.getByRole('button', { name: 'Replace invitation' })).toHaveCount(0);
      await expect(panel.getByRole('button', { name: 'Cancel invitation' })).toHaveCount(0);
      // The administrator keeps access whatever the setting says.
      const own = await openTenant(page);
      await expect(own.getByRole('button', { name: 'Send invitation' })).toBeVisible();
    } finally {
      // Put back through the same screen, whatever happened above: the invitation scenario in this
      // file is driven by a staff member.
      await page.goto('/settings');
      if (!(await allow.isChecked())) await save(true);
    }
    await page.reload();
    await expect(allow).toBeChecked();
    panel = await openTenant(staff);
    await expect(panel.getByRole('button', { name: 'Send invitation' })).toBeVisible();
    await expect(panel.getByText(DENIED)).toHaveCount(0);
  } finally {
    await staffContext.close();
  }
});
