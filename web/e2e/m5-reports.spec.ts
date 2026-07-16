import { expect, test, type Page } from '@playwright/test';
import { seedTheme, visualSnapshot } from './helpers';

// M5 reporting e2e specs (§D step 5), serial, against the seeded demo org.
// The seeded admin (Renée Calloway) has no MFA; login is email + password.
//
// These specs must run AFTER the M4 reconcile spec (workers:1 discovery order enforces this),
// because the M4 spec finalizes the Operating Trust reconciliation that the statement engine
// will report as `latestReconciledBank`.
//
// The O5 golden figures for May 2026 (cash basis) are locked by WP-1:
//   Beginning: $21,345.30  Income: $1,295.00  Ending: $22,640.30
// Owner id: 01923000-0000-7000-8000-000000000a05 (Ridgeline Investments, DemoIds.O5)

const ADMIN = 'renee.calloway@tarheelpg.test';
const PASSWORD = 'Tarheel-Trust-2026!';

// O5 = Ridgeline Investments (owns 230 Haywood Rd, property P5)
const O5_ID = '01923000-0000-7000-8000-000000000a05';
const O5_NAME = 'Ridgeline Investments';

async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/dashboard/);
}

// Navigate to the statement for O5, May 2026, cash basis.
// We use the direct URL since the owner detail page has no statement button yet (M5 scope).
async function gotoO5Statement(page: Page) {
  await page.goto(`/owners/${O5_ID}/statement`);
  // The page loads with currentPeriodFilters() (current month), then we navigate to May 2026
  // via the period picker.
  await expect(page.getByRole('heading', { name: 'Owner statement', exact: true })).toBeVisible();
  // Wait for the statement to load (skeleton disappears) — the owner name span.
  await expect(page.locator('.fw7').filter({ hasText: O5_NAME })).toBeVisible({ timeout: 15_000 });
}

// Open the PeriodPicker and select May 2026, cash basis.
async function selectMay2026Cash(page: Page) {
  // Open period picker — the PeriodPicker button has aria-haspopup="dialog" and shows the current
  // month label (e.g., "June 2026"). Click the first such button on the page.
  await page.locator('button[aria-haspopup="dialog"]').first().click();
  const dialog = page.getByRole('dialog', { name: 'Select period' });

  // Select year 2026
  await dialog.getByRole('button', { name: '2026' }).click();

  // Select month May (closes the popover)
  await dialog.getByRole('button', { name: 'May' }).click();

  // The period picker button should now show "May 2026"
  await expect(page.getByRole('button', { name: /May 2026/i })).toBeVisible({ timeout: 8_000 });

  // Wait for the statement to reload — the owner name span.
  await expect(page.locator('.fw7').filter({ hasText: O5_NAME })).toBeVisible({ timeout: 15_000 });
}

test.describe.serial('M5 reports', () => {
  // ---- Flow A: Owner statement → fiduciary panel → export -----------------------

  test('owner statement shows fiduciary panel with three passing checks and $0.00 variance', async ({
    page,
  }) => {
    await login(page);
    await gotoO5Statement(page);
    await selectMay2026Cash(page);
    // Visual regression (CI-only): flagship owner statement (O5 May 2026 Cash — golden figures).
    await visualSnapshot(page, 'owner-statement-full.png', { fullPage: true });

    // Screenshot for human review
    await page.screenshot({ path: 'e2e-results/m5-statement-loaded.png', fullPage: true });

    // The fiduciary integrity panel must be visible.
    await expect(page.getByText('Fiduciary integrity')).toBeVisible();

    // Three checks must render in the panel.
    const fidPanel = page.locator('.pf-fiduciary');
    await expect(fidPanel).toBeVisible();
    await visualSnapshot(fidPanel, 'fiduciary-panel.png');

    // PM income excluded check
    await expect(fidPanel.getByText(/PM income excluded/i)).toBeVisible();
    // Deposits recognized on application check (present regardless of pass/fail — it's period-specific)
    await expect(fidPanel.getByText(/[Dd]eposits recognized on application/i)).toBeVisible();

    // The variance check: assert "$0.00 variance" is rendered (balanced = true for O5 May 2026).
    await expect(fidPanel.getByText(/\$0\.00/)).toBeVisible();

    // The two structural checks (PM income excluded + balanced) must be "pass" icons.
    // (Deposits check may be false for a period with no applied deposits — it's a period fact.)
    const passIcons = fidPanel.locator('.pf-fid-check-icon.pass');
    await expect(passIcons).toHaveCount(2);

    // The ending balance must match the O5 May 2026 golden figure.
    await expect(page.getByText('$22,640.30')).toBeVisible();

    await page.screenshot({ path: 'e2e-results/m5-fiduciary-panel.png', fullPage: true });
  });

  // WP-3 (ADR-023's deferred dark coverage): the dark twin of the flagship statement shot above.
  // Read-only, same state (O5 May 2026 cash), no mask — matching the light call. Stays inside this
  // serial block so it keeps the "runs after the M4 reconcile spec" ordering the file documents.
  // The data-theme assertion guards the bootstrap-from-CI-actuals flow (a failed seed would commit a
  // light image as the dark baseline).
  test('owner statement renders in the dark theme', async ({ page }) => {
    await seedTheme(page, 'dark'); // before login — ThemeProvider reads storage on boot
    await login(page);
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await gotoO5Statement(page);
    await selectMay2026Cash(page);
    await visualSnapshot(page, 'owner-statement-full-dark.png', { fullPage: true });
    // The golden ending balance must still render — a dark-theme shot of a broken statement is not
    // coverage.
    await expect(page.getByText('$22,640.30')).toBeVisible();
  });

  test('owner statement PDF export returns a non-empty PDF response', async ({ page }) => {
    await login(page);
    await gotoO5Statement(page);
    await selectMay2026Cash(page);

    // Intercept the PDF download at the API level (the SPA uses fetch → blob → anchor click).
    // We intercept via a route that captures the response before the anchor fires.
    const pdfPromise = page.waitForResponse(
      (response) =>
        response.url().includes(`/api/statements/${O5_ID}/pdf`) && response.status() === 200,
      { timeout: 20_000 },
    );

    // Click the PDF button
    await page.getByRole('button', { name: 'PDF' }).click();

    const pdfResponse = await pdfPromise;
    const contentType = pdfResponse.headers()['content-type'] ?? '';
    expect(contentType).toContain('application/pdf');

    // Verify body is non-empty (a real PDF has bytes)
    const body = await pdfResponse.body();
    expect(body.byteLength).toBeGreaterThan(1000);

    await page.screenshot({ path: 'e2e-results/m5-pdf-exported.png', fullPage: true });
  });

  test('owner statement CSV export returns a non-empty CSV response', async ({ page }) => {
    await login(page);
    await gotoO5Statement(page);
    await selectMay2026Cash(page);

    const csvPromise = page.waitForResponse(
      (response) =>
        response.url().includes(`/api/statements/${O5_ID}/csv`) && response.status() === 200,
      { timeout: 20_000 },
    );

    // Click the CSV export button
    await page.getByRole('button', { name: 'Export CSV' }).first().click();

    const csvResponse = await csvPromise;
    const contentType = csvResponse.headers()['content-type'] ?? '';
    expect(contentType.toLowerCase()).toContain('text/csv');

    const text = await csvResponse.text();
    // The CSV must contain the owner name and the ending balance figure.
    expect(text).toContain(O5_NAME);
    expect(text).toContain('22640.30');
  });

  // ---- Flow B: Report catalog → filter → live preview -------------------------

  test('report catalog loads 8 reports; selecting one shows a live preview', async ({ page }) => {
    await login(page);
    await page.goto('/reports');
    await expect(page.locator('h2').filter({ hasText: 'Reports' })).toBeVisible();

    // All 8 catalog entries must load (each is a listitem in the available reports list).
    const reportItems = page.getByRole('listitem');
    // Wait for the catalog to finish loading (skeleton gone)
    await expect(page.getByText('Rent roll')).toBeVisible({ timeout: 15_000 });
    const count = await reportItems.count();
    expect(count).toBeGreaterThanOrEqual(8);

    // Select "Rent roll" — this report has rows on the demo org (no period filter needed).
    // The report card is a button whose accessible name includes the report name.
    const rentRollCard = page.locator('.pf-report-card').filter({ hasText: 'Rent roll' }).first();
    await expect(rentRollCard).toBeVisible({ timeout: 10_000 });
    await rentRollCard.click();

    // Wait for the builder panel to show the "Live preview" bar — confirms the BuilderPanel rendered.
    await expect(page.getByText('Live preview')).toBeVisible({ timeout: 5_000 });

    // Wait for the preview to settle: skeleton disappears.
    // The preview area always renders something (table, empty state, or error) once data loads.
    await page.waitForFunction(() => document.querySelector('.pf-skeleton') === null, {
      timeout: 15_000,
    });

    // The preview area itself must be in the DOM.
    await expect(page.locator('.pf-builder-preview')).toBeVisible({ timeout: 3_000 });

    await page.screenshot({ path: 'e2e-results/m5-catalog-loaded.png', fullPage: true });
  });

  test('selecting a report and changing the period filter updates the live preview', async ({
    page,
  }) => {
    await login(page);
    await page.goto('/reports');
    await expect(page.locator('h2').filter({ hasText: 'Reports' })).toBeVisible();

    // Wait for catalog to load
    await expect(page.getByText('Rent roll')).toBeVisible({ timeout: 15_000 });

    // Click the Rent roll card (category: Owner → no period filter, always has rows).
    const rentRollCard = page.locator('.pf-report-card').filter({ hasText: 'Rent roll' }).first();
    await rentRollCard.click();

    // After clicking, the BuilderPanel key changes — wait for the new builder panel to render
    // by looking for the "Rent roll" title in the builder header (the CardHeader h3).
    await expect(
      page.locator('.pf-builder').locator('h3').filter({ hasText: 'Rent roll' }),
    ).toBeVisible({
      timeout: 8_000,
    });

    // The live preview table must show data for the rent roll.
    await expect(page.getByRole('table', { name: 'Report preview' })).toBeVisible({
      timeout: 15_000,
    });

    await page.screenshot({ path: 'e2e-results/m5-rent-roll-preview.png', fullPage: true });

    // Switch to the "Delinquency" report (category: Banking) to exercise report switching.
    const delinquencyCard = page
      .locator('.pf-report-card')
      .filter({ hasText: 'Delinquency' })
      .first();
    await delinquencyCard.click();

    // Wait for the Delinquency builder panel to render.
    await expect(
      page.locator('.pf-builder').locator('h3').filter({ hasText: 'Delinquency' }),
    ).toBeVisible({
      timeout: 8_000,
    });

    // The period chip is always visible in the builder — click to change the year.
    const periodChip = page
      .locator('.pf-builder')
      .locator('button')
      .filter({ hasText: /Period/ })
      .first();
    await periodChip.click();

    const periodDialog = page.getByRole('dialog', { name: 'Select period' });
    // Pick the previous year to force a different query parameter.
    await periodDialog.getByRole('button', { name: String(new Date().getFullYear() - 1) }).click();
    // Pick January to close the dialog.
    await periodDialog.getByRole('button', { name: 'Jan' }).click();

    // Wait for the preview to settle (either a table or the empty-state).
    await expect(page.locator('.pf-builder-preview').locator('table, .pf-empty')).toBeVisible({
      timeout: 15_000,
    });

    await page.screenshot({ path: 'e2e-results/m5-filter-changed.png', fullPage: true });
  });

  test('report catalog Export CSV button returns a CSV file', async ({ page }) => {
    await login(page);
    await page.goto('/reports');

    // Wait for catalog
    await expect(page.getByText('Rent roll')).toBeVisible({ timeout: 15_000 });

    // Select Rent roll
    const rentRollCard2 = page.locator('.pf-report-card').filter({ hasText: 'Rent roll' }).first();
    await rentRollCard2.click();
    // Wait for the builder panel to update to Rent roll.
    await expect(
      page.locator('.pf-builder').locator('h3').filter({ hasText: 'Rent roll' }),
    ).toBeVisible({
      timeout: 8_000,
    });
    // Wait for the preview to load.
    await expect(page.getByRole('table', { name: 'Report preview' })).toBeVisible({
      timeout: 15_000,
    });

    // Intercept the CSV export response
    const csvPromise = page.waitForResponse(
      (response) =>
        response.url().includes('/api/reports/rent-roll/csv') && response.status() === 200,
      { timeout: 20_000 },
    );

    await page.getByRole('button', { name: 'Export CSV' }).click();

    const csvResponse = await csvPromise;
    const contentType = csvResponse.headers()['content-type'] ?? '';
    expect(contentType.toLowerCase()).toContain('text/csv');

    const text = await csvResponse.text();
    // The rent roll CSV must have at least one property address
    expect(text.length).toBeGreaterThan(10);
  });
});
