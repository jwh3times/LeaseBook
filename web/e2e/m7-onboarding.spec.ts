import path from 'path';
import { expect, test, type Page } from '@playwright/test';

// M7 onboarding e2e (WP-7 Task 7.2 — the M7 milestone gate).
// Serial, against the freshly-seeded cutover org (seed --org cutover).
//
// The cutover org is an EMPTY operational org: org row + PMAdmin login + trust bank accounts +
// chart of accounts — but NO journal data, so the dashboard redirects to the wizard.
//
// Test order (serial — each builds on the previous):
//   1. Empty dashboard redirects to /onboarding.
//   2. Entity import: owners → properties → units → tenants_leases.
//   3. Balance import: owner_balances → deposit_liabilities → bank_balances.
//   4. Non-tying verification: wrong figures → NOT TIED → sign-off button disabled / returns 409.
//   5. Tied verification: matching figures → TIED / $0.00 variance → sign off succeeds.
//   6. Post-signoff: dashboard no longer redirects to /onboarding.
//
// Fixture tie-out (seed/cutover-fixture/ — verified by check-invariants --org cutover):
//   Owner equity total (cash + accrual, both same): O-C1 $5,000.00 + O-C2 $3,500.00 = $8,500.00
//   Deposit liabilities total: T-C1 $1,500.00 + T-C2 $1,250.00 + T-C3 $1,750.00 = $4,500.00
//   Operating Trust book balance = $8,500.00 = Σ owner equity ✓
//   Deposit Trust book balance = $4,500.00 = Σ deposit liabilities ✓
//   MigrationClearing residual: $0.00 cash, $0.00 accrual (cash == accrual, no accrual-delta line) ✓
//
// Cutover org bank account IDs (stable — set in CutoverSeeder.cs):
//   Operating Trust:  01923000-0000-7000-8000-0000c7ba0001
//   Deposit Trust:    01923000-0000-7000-8000-0000c7ba0002
//
// NOTE: This spec runs AFTER the demo-org specs (alphabetical discovery: m7 > m6).
// The cutover org coexists with the demo org in the same DB; there is no state conflict.
// A second run of this spec against the same DB would fail (org is no longer empty after sign-off).
// Reset the DB (./scripts/dev.ps1 reset-db) and re-seed both orgs before re-running.

const CUTOVER_ADMIN = 'admin@cutover.test';
const CUTOVER_PASSWORD = 'Cutover-Trust-2026!';
const CUTOVER_DATE = '2026-03-31';

// Fixture CSV files (relative to project root → seed/cutover-fixture/).
const FIXTURE_DIR = path.join(__dirname, '..', '..', 'seed', 'cutover-fixture');

// Stable bank account IDs from CutoverSeeder.cs — used in verification bank balance rows.
const OPER_TRUST_ID = '01923000-0000-7000-8000-0000c7ba0001';
const DEPOSIT_TRUST_ID = '01923000-0000-7000-8000-0000c7ba0002';

// Tied fixture figures (must match the imported totals for verification to pass).
const OWNER_EQUITY_TOTAL = '8500';
const DEPOSIT_LIABILITY_TOTAL = '4500';
const OPER_TRUST_BOOK = '8500';
const DEPOSIT_TRUST_BOOK = '4500';

async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(CUTOVER_ADMIN);
  await page.getByLabel('Password').fill(CUTOVER_PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  // Empty org dashboard redirects to /onboarding; signed-off org stays on /dashboard.
  await page.waitForURL(/\/(onboarding|dashboard)/, { timeout: 10_000 });
}

/**
 * Upload a fixture CSV to the onboarding import dropzone.
 * The hidden file input has aria-label="CSV file".
 */
async function uploadFixtureCsv(page: Page, filename: string) {
  const filePath = path.join(FIXTURE_DIR, filename);
  const fileInput = page.locator('input[type="file"][aria-label="CSV file"]');
  await fileInput.setInputFiles(filePath);
}

/**
 * Prime the XSRF cookie (GET /api/auth/csrf) and return the token from document.cookie.
 * Used in the server-side gate assertion for the non-tying path.
 */
async function getCsrfToken(page: Page): Promise<string> {
  await page.evaluate(async () => {
    await fetch('/api/auth/csrf', { credentials: 'include' });
  });
  const token = await page.evaluate(() => {
    const match = document.cookie.match(/(?:^|; )XSRF-TOKEN=([^;]*)/);
    return match?.[1] ? decodeURIComponent(match[1]) : '';
  });
  return token;
}

test.describe.serial('M7 onboarding wizard', () => {
  // ── Step 1: empty dashboard redirects ─────────────────────────────────────

  test('empty org dashboard redirects to /onboarding', async ({ page }) => {
    await login(page);
    // Dashboard detects no journal data + not signed off → navigate('/onboarding').
    await expect(page).toHaveURL(/\/onboarding/, { timeout: 15_000 });
    await expect(page.getByRole('heading', { name: /migration setup/i })).toBeVisible();

    await page.screenshot({ path: 'e2e-results/m7-01-redirect.png', fullPage: true });
  });

  // ── Step 2: entity import ─────────────────────────────────────────────────

  test('banks configured: wizard auto-advances to entity import step', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    // banksConfigured=true (seeder provisioned them) → activeStep=1 (entities).
    await expect(page.getByRole('heading', { name: /import entities/i })).toBeVisible({
      timeout: 10_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-02-entity-step.png', fullPage: true });
  });

  test('import owners CSV: 2 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });
    await expect(page.getByRole('heading', { name: /import entities/i })).toBeVisible({
      timeout: 10_000,
    });

    const ownersRadio = page.locator('input[type="radio"][value="owners"]');
    await expect(ownersRadio).toBeVisible();
    await ownersRadio.check();

    await uploadFixtureCsv(page, 'owners.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-03-owners.png', fullPage: true });
  });

  test('import properties CSV: 2 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    const propertiesRadio = page.locator('input[type="radio"][value="properties"]');
    await propertiesRadio.check();
    await uploadFixtureCsv(page, 'properties.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-04-properties.png', fullPage: true });
  });

  test('import units CSV: 3 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    const unitsRadio = page.locator('input[type="radio"][value="units"]');
    await unitsRadio.check();
    await uploadFixtureCsv(page, 'units.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-05-units.png', fullPage: true });
  });

  test('import tenants_leases CSV: 3 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    const tlRadio = page.locator('input[type="radio"][value="tenants_leases"]');
    await tlRadio.check();
    await uploadFixtureCsv(page, 'tenants_leases.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-06-tenants.png', fullPage: true });
  });

  // ── Step 3: balance import ────────────────────────────────────────────────

  test('balance step is now active after entity import', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    await expect(page.getByRole('heading', { name: /import opening balances/i })).toBeVisible({
      timeout: 10_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-07-balance-step.png', fullPage: true });
  });

  test('import owner_balances CSV: 2 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });
    await expect(page.getByRole('heading', { name: /import opening balances/i })).toBeVisible({
      timeout: 10_000,
    });

    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    const obRadio = page.locator('input[type="radio"][value="owner_balances"]');
    await obRadio.check();
    await uploadFixtureCsv(page, 'owner_balances.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-08-owner-balances.png', fullPage: true });
  });

  test('import deposit_liabilities CSV: 3 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    const dlRadio = page.locator('input[type="radio"][value="deposit_liabilities"]');
    await dlRadio.check();
    await uploadFixtureCsv(page, 'deposit_liabilities.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-09-deposit-liabilities.png', fullPage: true });
  });

  test('import bank_balances CSV: 2 rows imported', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    const bbRadio = page.locator('input[type="radio"][value="bank_balances"]');
    await bbRadio.check();
    await uploadFixtureCsv(page, 'bank_balances.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-10-bank-balances.png', fullPage: true });
  });

  // ── Step 4: non-tying path (before sign-off) ──────────────────────────────

  test('non-tying verification: wrong figures → NOT TIED → sign-off button disabled and server returns 409', async ({
    page,
  }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    // After all balances imported, activeStep=3 (verify & sign off).
    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });

    // Enter WRONG figures: owner equity = $1.00 (should be $8,500.00) → variance ≠ 0 → NOT TIED.
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.getByLabel('Owner equity total from AppFolio').fill('1.00');
    await page.getByLabel('Deposit liability total from AppFolio').fill('1.00');
    // Do not provide bank rows → imported banks are flagged as "unexpected" (non-zero variance).

    await page.getByRole('button', { name: /run verification/i }).click();

    // Verification report appears and is NOT TIED.
    await expect(page.getByRole('table', { name: /verification variance report/i })).toBeVisible({
      timeout: 20_000,
    });
    await expect(page.getByLabel('Import is not tied')).toBeVisible({ timeout: 10_000 });

    // "Not tied" badge in report header.
    await expect(page.getByText(/not tied/i).first()).toBeVisible({ timeout: 5_000 });

    // The sign-off button must be disabled when the report is not tied.
    const signoffBtn = page.getByRole('button', { name: /sign off migration/i });
    await expect(signoffBtn).toBeDisabled({ timeout: 5_000 });

    // Explanatory text.
    await expect(page.getByText(/sign-off is disabled until the import ties/i)).toBeVisible({
      timeout: 5_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-11-not-tied.png', fullPage: true });

    // Server-side gate: assert POST /api/onboarding/verification/{id}/signoff → 409 not_tied.
    // The VerificationReport displayed in the UI contains the verificationId. Extract it via
    // the aria-label on the variance table (we need to get the id another way — use the API directly).
    const csrfToken = await getCsrfToken(page);
    expect(csrfToken).not.toBe('');

    const nonTiedVerifyRes = await page.evaluate(
      async ({ csrfToken: token, cutoverDate }: { csrfToken: string; cutoverDate: string }) => {
        const verifyRes = await fetch('/api/onboarding/verification', {
          method: 'POST',
          credentials: 'include',
          headers: { 'Content-Type': 'application/json', 'X-XSRF-TOKEN': token },
          body: JSON.stringify({
            cutoverDate,
            ownerEquityTotal: 1.0,
            depositLiabilityTotal: 1.0,
            bankBookBalances: [],
          }),
        });
        const body = (await verifyRes.json()) as {
          isTied: boolean;
          verificationId: string;
        };
        return {
          status: verifyRes.status,
          isTied: body.isTied,
          verificationId: body.verificationId,
        };
      },
      { csrfToken, cutoverDate: CUTOVER_DATE },
    );

    expect(nonTiedVerifyRes.status).toBe(200);
    expect(nonTiedVerifyRes.isTied).toBe(false);

    // Attempt sign-off on the non-tied verification → 409.
    const signoffRes = await page.evaluate(
      async ({
        csrfToken: token,
        verificationId,
      }: {
        csrfToken: string;
        verificationId: string;
      }) => {
        const res = await fetch(`/api/onboarding/verification/${verificationId}/signoff`, {
          method: 'POST',
          credentials: 'include',
          headers: { 'X-XSRF-TOKEN': token },
        });
        return { status: res.status };
      },
      { csrfToken, verificationId: nonTiedVerifyRes.verificationId },
    );

    expect(signoffRes.status).toBe(409);
  });

  // ── Step 5: happy-path tied verification + sign-off ───────────────────────

  test('tied verification: matching figures → TIED / $0.00 clearing residuals → sign off succeeds', async ({
    page,
  }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });

    // Enter matching AppFolio closing figures.
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.getByLabel('Owner equity total from AppFolio').fill(OWNER_EQUITY_TOTAL);
    await page.getByLabel('Deposit liability total from AppFolio').fill(DEPOSIT_LIABILITY_TOTAL);

    // Bank balance rows: row 1 is pre-rendered; add row 2 via the button.
    const bankIdInputs = page.getByLabel(/bank account id/i);
    const accountCodeInputs = page.getByLabel(/account code/i);
    const bookBalanceInputs = page.getByLabel(/expected book balance/i);

    // Row 1: Operating Trust.
    await bankIdInputs.nth(0).fill(OPER_TRUST_ID);
    await accountCodeInputs.nth(0).fill('Cutover Operating Trust');
    await bookBalanceInputs.nth(0).fill(OPER_TRUST_BOOK);

    // Row 2: Deposit Trust.
    await page.getByRole('button', { name: /add bank account/i }).click();
    await bankIdInputs.nth(1).fill(DEPOSIT_TRUST_ID);
    await accountCodeInputs.nth(1).fill('Cutover Deposit Trust');
    await bookBalanceInputs.nth(1).fill(DEPOSIT_TRUST_BOOK);

    // Run verification.
    await page.getByRole('button', { name: /run verification/i }).click();

    // Report appears.
    await expect(page.getByRole('table', { name: /verification variance report/i })).toBeVisible({
      timeout: 20_000,
    });

    // "Tied" badge (aria-label="Import is tied" on the span in VerificationStep.tsx).
    await expect(page.getByLabel('Import is tied')).toBeVisible({ timeout: 10_000 });

    // Clearing residuals section: both $0.00 (MigrationClearing nets to zero in both bases).
    const clearingSection = page.locator('.ob-clearing-residual');
    await expect(clearingSection).toBeVisible({ timeout: 5_000 });
    // Two $0.00 values appear (cash + accrual).
    const zeroAmounts = clearingSection.getByText('$0.00');
    await expect(zeroAmounts.first()).toBeVisible();

    await page.screenshot({ path: 'e2e-results/m7-12-tied-report.png', fullPage: true });

    // Sign-off button is ENABLED when tied.
    const signoffBtn = page.getByRole('button', { name: /sign off migration/i });
    await expect(signoffBtn).toBeEnabled({ timeout: 5_000 });

    // Sign off.
    await signoffBtn.click();

    // Success: "Migration verified and signed off."
    await expect(
      page.getByRole('status').filter({ hasText: /migration verified and signed off/i }),
    ).toBeVisible({ timeout: 15_000 });

    await page.screenshot({ path: 'e2e-results/m7-13-signed-off.png', fullPage: true });
  });

  // ── Step 6: dashboard after sign-off ──────────────────────────────────────

  test('after sign-off: dashboard no longer redirects to /onboarding', async ({ page }) => {
    await login(page);

    // Signed-off org: hasJournalData=true (balances posted) + signedOff=true → normal dashboard.
    await expect(page).toHaveURL(/\/dashboard/, { timeout: 15_000 });
    await expect(page).not.toHaveURL(/\/onboarding/);

    // Dashboard renders without error.
    await expect(page.getByRole('heading', { name: /dashboard/i }).first()).toBeVisible({
      timeout: 15_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-14-dashboard-live.png', fullPage: true });
  });
});
