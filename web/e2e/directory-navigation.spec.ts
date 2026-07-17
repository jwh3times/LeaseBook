import { expect, test, type Page } from '@playwright/test';
import { DEMO_ADMIN, openPalette, signIn } from './helpers';

// Directory navigation e2e (WP-4 step 1, ADR-022's deferred coverage): owners/properties/tenants
// list → detail, units surfaced via property detail (there is no /units/:id route), a ⌘K jump to one
// entity of each type, the record quick-switcher, and back-button integrity. Read-only against the
// seeded demo org — pure navigation, so the demo org's golden figures cannot drift.
//
// Note on "back-button integrity": there are no breadcrumbs in the app. The `DetailPage` scaffold
// renders a ghost back button whose accessible name equals the list label ("Owners"/"Properties"),
// which collides with the sidebar nav button of the same name (both are `role="button"` with that
// exact accessible text) — so back-button assertions are scoped to `.pf-page` (the routed content,
// which excludes the sidebar and the `<h1>` topbar title) to target the back button unambiguously.
// The tenant ledger has no back button at all — only the `RecordQuickSwitch`.

/**
 * Clicks a sidebar nav item and waits for the destination list's own heading (IndexView renders an
 * unconditional `<h2>{title}</h2>` regardless of load state) before returning. The nav click updates
 * the URL via `pushState` synchronously, but React can commit the route swap a tick later — a bare
 * `toHaveURL` assertion followed immediately by a generic query (`tbody tr`, `getByText(name)`) can
 * silently match content still on-screen from the *previous* page (e.g. the dashboard's own owner
 * table renders `t-row-click` rows and the tenant "Jasmine Carter"/owner "Hargrove Family Trust" text
 * that also appear there). Waiting for the list's own heading closes that race.
 */
async function openList(page: Page, name: 'Owners' | 'Properties' | 'Tenants'): Promise<void> {
  await page.getByRole('button', { name }).click();
  await expect(page).toHaveURL(new RegExp(`/${name.toLowerCase()}$`));
  await expect(page.getByRole('heading', { name, level: 2 })).toBeVisible();
}

test.describe('directory navigation', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, DEMO_ADMIN);
  });

  test('owners: list row-click opens the detail, and the back button returns to the list', async ({
    page,
  }) => {
    await openList(page, 'Owners');
    await page.getByText('Hargrove Family Trust').click();
    await expect(page).toHaveURL(/\/owners\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: 'Hargrove Family Trust' })).toBeVisible();

    // Back-button integrity (the `DetailPage` ghost button, not a breadcrumb — there are none).
    const backButton = page.locator('.pf-page').getByRole('button', { name: 'Owners' });
    await expect(backButton).toBeVisible();
    await backButton.click();
    await expect(page).toHaveURL(/\/owners$/);
  });

  test('properties: list row-click opens the detail aggregating units, tenants, and owner, and the back button returns to the list', async ({
    page,
  }) => {
    await openList(page, 'Properties');
    const firstRow = page.locator('tbody tr').first();
    const address = (await firstRow.locator('td').first().innerText()).trim();
    await firstRow.click();
    await expect(page).toHaveURL(/\/properties\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: address })).toBeVisible();

    // `PropertyDetailPage` aggregates units, tenants, and owner (there is no /units/:id route — units
    // are surfaced only through this page). Assert real, data-driven text (not just a static header)
    // so the assertion actually exercises the aggregation.
    await expect(page.getByRole('heading', { name: 'Units', level: 3 })).toBeVisible();
    await expect(page.getByText(/\d+ unit\(s\)/)).toBeVisible();
    await expect(page.getByRole('heading', { name: 'Tenants', level: 3 })).toBeVisible();
    await expect(page.getByText(/\d+ current tenant\(s\)/)).toBeVisible();
    await expect(page.getByText(/Owner: /)).toBeVisible();

    // Back-button integrity.
    const backButton = page.locator('.pf-page').getByRole('button', { name: 'Properties' });
    await expect(backButton).toBeVisible();
    await backButton.click();
    await expect(page).toHaveURL(/\/properties$/);
  });

  test('tenants: list row-click opens the ledger; there is no back button, only the record quick-switcher', async ({
    page,
  }) => {
    await openList(page, 'Tenants');
    await page.getByText('Jasmine Carter').click();
    await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();

    await expect(page.getByRole('group', { name: 'Record navigation' })).toBeVisible();
    // No `DetailPage` back button is rendered on the ledger page (scoped to the routed content, which
    // excludes the sidebar's own "Tenants" nav button).
    await expect(page.locator('.pf-page').getByRole('button', { name: 'Tenants' })).toHaveCount(0);
  });

  test('the record quick-switcher pages through the owners list order, then back', async ({
    page,
  }) => {
    await openList(page, 'Owners');
    await page.locator('tbody tr').first().click();
    await expect(page).toHaveURL(/\/owners\/[0-9a-f-]+$/);

    // Wait for the switcher (unique to the detail route) before reading the heading — the same
    // list→detail race `openList` guards against also applies to a bare list-row click.
    const switcher = page.locator('.pf-page').getByRole('group', { name: 'Record navigation' });
    await expect(switcher).toBeVisible();
    const heading = page.getByRole('heading', { level: 2 });
    await expect(heading).toBeVisible();
    const firstUrl = page.url();
    const firstName = (await heading.textContent())?.trim();
    expect(firstName).toBeTruthy();

    const prevButton = switcher.getByRole('button', { name: 'Previous record ([)' });
    const nextButton = switcher.getByRole('button', { name: 'Next record (])' });
    // The first row in list order has no predecessor.
    await expect(prevButton).toBeDisabled();
    await expect(nextButton).toBeEnabled();

    await nextButton.click();
    await expect(page).not.toHaveURL(firstUrl);
    // The detail query re-fetches for the new id without ever unmounting the heading (no interstitial
    // skeleton), so a single-shot read right after the click can still observe the *previous* owner's
    // text. Retry the whole read+compare until it settles on a real, different, non-empty name — this
    // avoids both a stale read and a vacuous pass against a momentarily absent element.
    await expect(async () => {
      await expect(heading).toBeVisible();
      const secondName = (await heading.textContent())?.trim();
      expect(secondName).toBeTruthy();
      expect(secondName).not.toBe(firstName);
    }).toPass({ timeout: 10_000 });

    await prevButton.click();
    await expect(page).toHaveURL(firstUrl);
    await expect(heading).toHaveText(firstName!);
  });

  test('⌘K jumps to an owner, a property, and a tenant', async ({ page }) => {
    // Owner.
    let search = await openPalette(page);
    await search.fill('hargrove');
    const ownerOption = page.getByRole('option').filter({ hasText: 'Hargrove Family Trust' });
    await expect(ownerOption).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/owners\//);
    await expect(page.getByRole('heading', { name: 'Hargrove Family Trust' })).toBeVisible();

    // Property — discover a real address from the list rather than hardcoding unconfirmed seed data,
    // and search on a distinctive word (skip the house number) so the fuzzy match is unambiguous.
    await openList(page, 'Properties');
    const address = (
      await page.locator('tbody tr').first().locator('td').first().innerText()
    ).trim();
    const propertyTerm = address.split(' ')[1] ?? address;

    search = await openPalette(page);
    await search.fill(propertyTerm);
    const propertyOption = page.getByRole('option').filter({ hasText: address });
    await expect(propertyOption).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/properties\//);
    await expect(page.getByRole('heading', { name: address })).toBeVisible();

    // Tenant. (A "unit" palette result routes to /properties, not a unit detail — not covered here.)
    search = await openPalette(page);
    await search.fill('carter');
    const tenantOption = page.getByRole('option').filter({ hasText: 'Jasmine Carter' });
    await expect(tenantOption).toBeVisible();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/tenants\//);
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
  });
});
