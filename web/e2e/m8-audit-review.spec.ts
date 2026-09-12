import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, signIn } from './helpers';

// The PMAdmin audit-review surface (#321), against the seeded Tarheel demo org. The seed itself is
// what makes this demoable: provisioning the org writes an `org-provisioned` row and every directory
// and journal write behind it writes its own, all attributed to a named system process (ADR-039).
//
// Read-only throughout. Nothing here mutates demo state, so it is safe beside the money specs that
// share the org.
//
// The seeded admin (Renée Calloway) is a PMAdmin with no MFA, so login is email + password.

test.describe('M8 audit-log review', () => {
  test('an admin reaches the trail from the sidebar and reads one event', async ({ page }) => {
    await signIn(page, DEMO_ADMIN);

    // The nav item is admin-only; reaching the page through it is the discoverability claim.
    await page.getByRole('button', { name: 'Audit log' }).click();
    await page.waitForURL(/\/audit$/);
    await expect(page.locator('h2').filter({ hasText: 'Audit log' })).toBeVisible();

    const rows = page.locator('.pf-table tbody tr');
    await expect(rows.first()).toBeVisible({ timeout: 15_000 });

    // Open the newest event and read its recorded values. Driven from the row's focusable control,
    // by keyboard: the row-click handler lives on the <tr>, which nothing can focus, so the button is
    // the keyboard path and this is what proves it exists.
    const open = page.getByRole('button', { name: /^View / }).first();
    await open.focus();
    await expect(open).toBeFocused();
    await open.press('Enter');
    const drawer = page.getByRole('dialog', { name: 'Audit event' });
    await expect(drawer).toBeVisible();
    // exact: a payload can carry its own ActorKind/ActorProcess columns — journal_entries does,
    // since ADR-039 — and those field names are rendered in the same drawer as this label.
    await expect(drawer.getByText('Actor', { exact: true })).toBeVisible();
    await expect(drawer.getByText('Record', { exact: true })).toBeVisible();
  });

  test('filters narrow the trail, and the whole audited universe is reachable', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);
    await page.goto('/audit');

    const filters = page.getByRole('group', { name: 'Audit filters' });
    await expect(filters).toBeVisible({ timeout: 15_000 });

    // `owners` is a directory table: present here, and deliberately absent from the compliance pack's
    // money-touching extract. That contrast is the reason this surface exists.
    await filters.getByLabel('Record type').selectOption('owners');

    const rows = page.locator('.pf-table tbody tr');
    await expect(rows.first()).toBeVisible({ timeout: 15_000 });
    for (const cell of await page.locator('.pf-table tbody tr td:nth-child(2)').all()) {
      await expect(cell).toHaveText('owners');
    }

    // The seeder acts as a named process, not a person — the distinction ADR-039 made durable.
    await filters.getByLabel('Actor').selectOption('system');
    await expect(rows.first()).toBeVisible();
    await expect(page.locator('.pf-table tbody tr td:nth-child(4)').first()).toContainText(
      'System',
    );
  });
});
