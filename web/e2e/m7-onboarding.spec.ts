import path from 'path';
import { fileURLToPath } from 'node:url';
import { expect, test, type Page } from '@playwright/test';
import { visualSnapshot } from './helpers';

// __dirname is not defined in ESM scope; derive it from import.meta.url.
const __dirname = path.dirname(fileURLToPath(import.meta.url));

// M7 onboarding e2e (WP-7 Task 7.2 — the M7 milestone gate).
// Serial, against the freshly-seeded cutover org (seed --org cutover).
//
// The cutover org is an EMPTY operational org: org row + PMAdmin login + trust bank accounts +
// chart of accounts — but NO journal data, so the dashboard redirects to the wizard.
//
// Test order (serial — each builds on the previous):
//   1. Empty dashboard redirects to /onboarding.
//   2. Entity import: owners → properties → units → tenants_leases.
//   3. Balance import: owner_balances (O-C1 deliberately understated by $50.00) → deposit_liabilities
//      → bank_balances (Operating Trust raised by $200.00) → held_pm_fees ($200.00, Operating Trust).
//   4. Non-tying verification: wrong figures → NOT TIED → sign-off button disabled / returns 409.
//   5. Corrected re-import (supersede — WP-7 Task 14): the owner_balances CSV is re-uploaded with
//      O-C1 fixed; banner reads "1 corrected, 1 unchanged" (O-C2's row is untouched).
//   6. Tied verification: matching figures (owner equity now $8,500.00 again, held fees attested at
//      $200.00) → TIED / $0.00 variance → sign off succeeds.
//   7. Post-signoff: dashboard no longer redirects to /onboarding.
//
// Fixture tie-out (seed/cutover-fixture/ — verified by check-invariants --org cutover, AFTER the
// correction + held-fees legs below; the deliberately-wrong first owner_balances import is an
// intermediate state, not the final tie):
//   Owner equity total (cash + accrual, both same, POST-CORRECTION): O-C1 $5,000.00 + O-C2 $3,500.00
//     = $8,500.00. (The first owner_balances.csv import understates O-C1 at $4,950.00 — a $50.00
//     gap — on purpose; owner_balances_corrected.csv supersedes it back to $5,000.00 before the tied
//     verification step. O-C2 is identical in both files, so its row supersedes as "unchanged".)
//   Deposit liabilities total: T-C1 $1,500.00 + T-C2 $1,250.00 + T-C3 $1,750.00 = $4,500.00
//   Held PM fees (held_pm_fees.csv, Operating Trust only): $200.00
//   Operating Trust book balance = $8,700.00 = Σ owner equity ($8,500.00) + held PM fees ($200.00) ✓
//   Deposit Trust book balance = $4,500.00 = Σ deposit liabilities ✓
//   MigrationClearing residual: $0.00 cash, $0.00 accrual (cash == accrual, no accrual-delta line;
//   held fees post Basis=Both, so no basis ever carries the raw $200.00/$50.00 alone) ✓
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

// Tied fixture figures (must match the imported totals for verification to pass). OWNER_EQUITY_TOTAL
// is the POST-CORRECTION figure (the corrected re-import restores O-C1 to its original $5,000.00).
// OPER_TRUST_BOOK is raised by HELD_PM_FEES_TOTAL over the original $8,500.00 (Task 14, WP-7 §5) so
// the Operating Trust book still equals owner equity + held fees sitting inside that same account.
const OWNER_EQUITY_TOTAL = '8500';
const DEPOSIT_LIABILITY_TOTAL = '4500';
const HELD_PM_FEES_TOTAL = '200';
const OPER_TRUST_BOOK = '8700';
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

  // ── Step 2: entity import (all four kinds in one session, then Continue) ───
  //
  // The wizard does NOT auto-advance — step advancement is explicit. So all four entity
  // kinds are imported within ONE page session (no reload between them, which would resume
  // the wizard at the balance step because entitiesImported flips true after the first import),
  // each asserting its own success banner, then "Continue →" advances to the balance step.

  test('banks configured: wizard lands on the entity import step', async ({ page }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });

    // banksConfigured=true (seeder provisioned them) → firstIncompleteStep=1 (entities).
    await expect(page.getByRole('heading', { name: /import entities/i })).toBeVisible({
      timeout: 10_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-02-entity-step.png', fullPage: true });
  });

  test('entity import: owners → properties → units → tenants_leases (each banner), then Continue', async ({
    page,
  }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });
    await expect(page.getByRole('heading', { name: /import entities/i })).toBeVisible({
      timeout: 10_000,
    });

    // The "Continue" button is disabled until at least one kind is imported.
    const continueBtn = page.getByRole('button', { name: /continue/i });
    await expect(continueBtn).toBeDisabled();

    // Owners (2 rows).
    await page.locator('input[type="radio"][value="owners"]').check();
    await uploadFixtureCsv(page, 'owners.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-03-owners.png', fullPage: true });

    // Properties (2 rows). Selecting a new radio resets the prior success banner.
    await page.locator('input[type="radio"][value="properties"]').check();
    await uploadFixtureCsv(page, 'properties.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-04-properties.png', fullPage: true });

    // Units (3 rows).
    await page.locator('input[type="radio"][value="units"]').check();
    await uploadFixtureCsv(page, 'units.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-05-units.png', fullPage: true });

    // Tenants & leases (3 rows).
    await page.locator('input[type="radio"][value="tenants_leases"]').check();
    await uploadFixtureCsv(page, 'tenants_leases.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-06-tenants.png', fullPage: true });

    // Continue → balance step.
    await expect(continueBtn).toBeEnabled();
    await continueBtn.click();
    await expect(page.getByRole('heading', { name: /import opening balances/i })).toBeVisible({
      timeout: 10_000,
    });

    await page.screenshot({ path: 'e2e-results/m7-07-balance-step.png', fullPage: true });
  });

  // ── Step 3: balance import (all four kinds in one session, then Continue) ───
  //
  // On a fresh login here, entitiesImported=true + balancesImported=false → the wizard
  // resumes at the balance step. All four balance kinds are imported within ONE session,
  // then "Continue →" advances to the verify step.

  test('balance import: owner_balances → deposit_liabilities → bank_balances → held_pm_fees (each banner), then Continue', async ({
    page,
  }) => {
    await login(page);
    await page.waitForURL(/\/onboarding/, { timeout: 10_000 });
    await expect(page.getByRole('heading', { name: /import opening balances/i })).toBeVisible({
      timeout: 10_000,
    });

    const continueBtn = page.getByRole('button', { name: /continue/i });
    await expect(continueBtn).toBeDisabled();

    // Owner balances (2 rows).
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.locator('input[type="radio"][value="owner_balances"]').check();
    await uploadFixtureCsv(page, 'owner_balances.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-08-owner-balances.png', fullPage: true });

    // Deposit liabilities (3 rows).
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.locator('input[type="radio"][value="deposit_liabilities"]').check();
    await uploadFixtureCsv(page, 'deposit_liabilities.csv');
    await expect(page.getByRole('status')).toContainText(/imported 3 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-09-deposit-liabilities.png', fullPage: true });

    // Bank balances (2 rows) — matched by name against the seeded cutover bank accounts. The
    // Operating Trust figure (8700.00) is raised $200.00 over owner equity (8500.00) — the held-fees
    // upload below accounts for exactly that $200.00 still sitting inside the account (WP-7 Task 14).
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.locator('input[type="radio"][value="bank_balances"]').check();
    await uploadFixtureCsv(page, 'bank_balances.csv');
    await expect(page.getByRole('status')).toContainText(/imported 2 rows/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-10-bank-balances.png', fullPage: true });

    // Held PM fees (1 row) — WP-7 Task 14: unremitted PM fees still sitting inside the Operating
    // Trust bank. Names the same trust bank as bank_balances.csv above; the $200.00 here is exactly
    // the amount that account's book balance was raised by, so the run still ties.
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.locator('input[type="radio"][value="held_pm_fees"]').check();
    await uploadFixtureCsv(page, 'held_pm_fees.csv');
    await expect(page.getByRole('status')).toContainText(/imported 1 row\b/i, { timeout: 15_000 });
    await page.screenshot({ path: 'e2e-results/m7-10b-held-fees.png', fullPage: true });

    // Continue → verify step.
    await expect(continueBtn).toBeEnabled();
    await continueBtn.click();
    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });
  });

  // ── Step 4: non-tying path (before sign-off) ──────────────────────────────

  test('non-tying verification: wrong figures → NOT TIED → sign-off button disabled and server returns 409', async ({
    page,
  }) => {
    await login(page);
    // Balances are now posted → hasJournalData=true → the dashboard no longer redirects.
    // Navigate to the wizard explicitly; it resumes at the verify step (firstIncompleteStep=3).
    await page.goto('/onboarding');
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

  // ── Step 5: corrected re-import (supersede) ───────────────────────────────
  //
  // The balance-import step (above) deliberately understated O-C1's owner balance by $50.00. Before
  // verification can tie, the operator corrects it via the pre-sign-off supersede path (WP-7 Task 5 /
  // Task 14) rather than a plain re-import. balancesImported is already true (owner_balances posted,
  // even with the error), so a fresh page load resumes at the verify step (firstIncompleteStep=3);
  // navigate back to the balance step via the checklist — it is already "reached".

  test('corrected re-import (supersede): fixes the understated owner balance, other row unchanged, then Continue', async ({
    page,
  }) => {
    await login(page);
    await page.goto('/onboarding');
    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });

    // Back to the balance step via the checklist (reached, since balancesImported is already true).
    await page
      .getByRole('button', { name: /go to step \d+: import opening balances/i })
      .click();
    await expect(page.getByRole('heading', { name: /import opening balances/i })).toBeVisible({
      timeout: 10_000,
    });

    // Corrected owner balances (2 rows): O-C1 back to $5,000.00 (was $4,950.00), O-C2 unchanged at
    // $3,500.00. The cutover date must match the original import exactly, or supersede 409s
    // (cutover_date_mismatch) — the field defaults to today on this fresh mount, so it must be re-set.
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.locator('input[type="radio"][value="owner_balances"]').check();
    await page.getByLabel('This is a corrected re-import (supersede)').check();
    await uploadFixtureCsv(page, 'owner_balances_corrected.csv');
    await expect(page.getByRole('status')).toContainText(/1 corrected, 1 unchanged/i, {
      timeout: 15_000,
    });
    await page.screenshot({ path: 'e2e-results/m7-11b-correction.png', fullPage: true });

    // Continue → verify step (owner equity now ties at $8,500.00 again).
    const continueBtn = page.getByRole('button', { name: /continue/i });
    await expect(continueBtn).toBeEnabled();
    await continueBtn.click();
    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });
  });

  // ── Step 6: happy-path tied verification + sign-off ───────────────────────

  test('tied verification: matching figures → TIED / $0.00 clearing residuals → sign off succeeds', async ({
    page,
  }) => {
    await login(page);
    // Navigate to the wizard explicitly (no redirect once journal data exists); resumes at verify.
    await page.goto('/onboarding');
    await expect(page.getByRole('heading', { name: /verify & sign off/i })).toBeVisible({
      timeout: 10_000,
    });

    // Enter matching AppFolio closing figures. Owner equity is the POST-CORRECTION total (the prior
    // step superseded O-C1 back to $5,000.00). Held PM fees ($200.00) is the amount imported against
    // the Operating Trust bank — attesting it (rather than leaving it blank) is what makes the "Held
    // PM Fees (Cash)" variance line appear and tie, per D5/§3b in VerificationService.
    await page.getByLabel('Cutover date').fill(CUTOVER_DATE);
    await page.getByLabel('Owner equity total from AppFolio').fill(OWNER_EQUITY_TOTAL);
    await page.getByLabel('Deposit liability total from AppFolio').fill(DEPOSIT_LIABILITY_TOTAL);
    await page.getByLabel('Held PM fees total from AppFolio').fill(HELD_PM_FEES_TOTAL);

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

    // Held PM Fees (Cash) line (WP-7 Task 14): appears because heldPmFeesTotal was attested above,
    // and ties because the $200.00 attested matches the $200.00 imported against Operating Trust.
    // Each VarianceRow carries its own per-line aria-label ("tied"/"not tied" — VerificationStep.tsx),
    // scoped to this row so it doesn't match every other tied line in the table. exact: true matters
    // here — "not tied" contains "tied" as a substring, so a loose match would pass even if untied.
    const heldFeesRow = page.getByRole('row').filter({ hasText: 'Held PM Fees (Cash)' });
    await expect(heldFeesRow).toBeVisible({ timeout: 5_000 });
    await expect(heldFeesRow.getByLabel('tied', { exact: true })).toBeVisible();

    // Clearing residuals section: both bases net to zero (MigrationClearing == 0).
    // The <Money colorize> component renders an exact zero as an em-dash with class
    // "pf-money zero" (formatMoney's dash for 0), NOT "$0.00". Assert two zero-class Money
    // spans (clearing cash + clearing accrual), and assert NO non-zero dollar residual.
    const clearingSection = page.locator('.ob-clearing-residual');
    await expect(clearingSection).toBeVisible({ timeout: 5_000 });
    const zeroMoney = clearingSection.locator('.pf-money.zero');
    // Cash + accrual clearing residuals are both zero (plus the total-variance Money is zero too).
    await expect(zeroMoney).toHaveCount(3, { timeout: 10_000 });
    // Visual regression (CI-only): the tied verification variance report.
    await visualSnapshot(
      page.getByRole('table', { name: 'Verification variance report' }),
      'onboarding-tied-report.png',
    );

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

  // ── Step 7: dashboard after sign-off ──────────────────────────────────────

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
