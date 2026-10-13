import { readFile } from 'node:fs/promises';
import { expect } from '@playwright/test';
import { captureDownload, DEMO_ADMIN, SCENARIO_ADMIN, signIn } from './helpers';
import { test } from './test';

// The PMAdmin audit-review surface (#321), against the seeded Tarheel demo org. The seed itself is
// what makes this demoable: provisioning the org writes an `org-provisioned` row and every directory
// and journal write behind it writes its own, all attributed to a named system process (ADR-039).
//
// The two review tests are read-only: nothing in them mutates demo state, so they are safe beside
// the money specs that share the org. The seeded admin (Renée Calloway) is a PMAdmin with no MFA, so
// login is email + password.
//
// The export test (#544) runs in the scenario organization. An export is the one thing on this page
// that writes: it adds a row recording itself, and nothing else. It touches no money, and no other
// spec reads the scenario organization's trail.

test.describe('M8 audit-log review', () => {
  test('an admin reaches the trail from the sidebar and reads one event @scenario:audit-log-review', async ({
    page,
  }) => {
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

  test('filters narrow the trail, and the whole audited universe is reachable @scenario:audit-log-review', async ({
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

  test('an admin exports the filtered trail as a CSV holding the rows on screen, and each export is itself recorded @scenario:audit-log-export', async ({
    page,
  }) => {
    const HEADER = 'Occurred (UTC),Entity type,Entity id,Action,Actor,Actor email';
    // No cell in these exports holds a comma, a quote or a line break, so a plain split reads them.
    const lines = (file: Buffer) => file.toString('utf8').trimEnd().split(/\r?\n/);
    // The screen writes "2026-10-13 12:34:56 UTC" where the file writes "2026-10-13 12:34:56Z".
    const asInFile = (when: string) => when.replace(' UTC', 'Z');

    await signIn(page, SCENARIO_ADMIN);
    await page.goto('/audit');
    const filters = page.getByRole('group', { name: 'Audit filters' });
    const rows = page.locator('.pf-table tbody tr');
    const exportButton = page.getByRole('button', { name: 'Export CSV' });

    // One row the seed wrote once: the organization being provisioned.
    await filters.getByLabel('Record type').selectOption('org-provisioned');
    await expect(page.getByText('1 total', { exact: true })).toBeVisible({ timeout: 15_000 });
    await expect(rows).toHaveCount(1);
    const seeded = rows.first().locator('td');
    const when = await seeded.nth(0).innerText();
    const action = await seeded.nth(2).innerText();
    // The actor cell holds a name, and under it an email address when the actor is a person.
    const [actor, actorEmail = ''] = await seeded.nth(3).locator('span').allInnerTexts();
    expect(when).toMatch(/^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2} UTC$/);

    const first = lines(
      await captureDownload(page, exportButton, {
        urlPart: '/api/audit/events.csv',
        contentType: 'text/csv',
      }),
    );
    expect(first).toHaveLength(3);
    expect(first[0]).toBe('Audit log — 1 events');
    expect(first[1]).toBe(HEADER);
    const cells = (first[2] ?? '').split(',');
    expect(cells).toHaveLength(6);
    expect(cells[0]).toBe(asInFile(when));
    expect(cells[1]).toBe('org-provisioned');
    expect(cells[2]).toMatch(/^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$/);
    expect(cells[3]).toBe(action);
    expect(cells[4]).toBe(actor);
    expect(cells[5]).toBe(actorEmail);

    // The export recorded itself, under this administrator's name. Narrowed to those records, the
    // newest row is the export just made.
    await filters.getByLabel('Record type').selectOption('audit-export-generated');
    await filters.getByLabel('Actor').selectOption({ label: 'Scenario Admin' });
    await expect(rows.first().locator('td').nth(1)).toHaveText('audit-export-generated');
    await expect(rows.first().locator('td').nth(3)).toContainText('Scenario Admin');
    await expect(rows.first().locator('td').nth(3)).toContainText(SCENARIO_ADMIN.email);
    const total = page.locator('.pf-card-hd .sub').filter({ hasText: /^\d+ total$/ });
    const exportsSoFar = Number.parseInt(await total.innerText(), 10);
    // Earlier runs on the same database each left two.
    expect(exportsSoFar).toBeGreaterThanOrEqual(1);
    const newest = await rows.first().locator('td').nth(0).innerText();

    // Keyboard path: the same narrowing, exported from the focused button with Enter.
    await exportButton.focus();
    await expect(exportButton).toBeFocused();
    const downloading = page.waitForEvent('download', { timeout: 20_000 });
    await page.keyboard.press('Enter');
    const download = await downloading;
    expect(download.suggestedFilename()).toMatch(/^audit-log-\d{4}-\d{2}-\d{2}\.csv$/);
    const second = lines(await readFile(await download.path()));
    expect(second[0]).toBe(`Audit log — ${exportsSoFar} events`);
    expect(second[1]).toBe(HEADER);
    const exported = second.slice(2).map((line) => line.split(','));
    expect(exported).toHaveLength(exportsSoFar);
    for (const row of exported) {
      expect(row.slice(1, 2)).toEqual(['audit-export-generated']);
      expect(row.slice(3)).toEqual(['insert', 'Scenario Admin', SCENARIO_ADMIN.email]);
    }
    // Newest first, as on screen.
    expect(exported[0]?.[0]).toBe(asInFile(newest));

    // That export is in the trail too.
    await page.reload();
    await filters.getByLabel('Record type').selectOption('audit-export-generated');
    await filters.getByLabel('Actor').selectOption({ label: 'Scenario Admin' });
    await expect(total).toHaveText(`${exportsSoFar + 1} total`);
    await expect(page.getByRole('alert')).toHaveCount(0);
  });
});
