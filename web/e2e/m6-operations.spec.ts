import { expect, test, type Page } from '@playwright/test';

// M6 WP-5 e2e smoke — dashboard CTA navigation (§M6 WP-5).
// Full run-cycle walkthrough is WP-7. This spec verifies:
//   1. The dashboard CTA "Run owner disbursements" navigates to /operations?tab=disbursement.
//   2. The Operations page renders the disbursement run screen with a period picker.

const ADMIN = 'renee.calloway@tarheelpg.test';
const PASSWORD = 'Tarheel-Trust-2026!';

async function login(page: Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/dashboard/);
}

test('dashboard CTA navigates to the disbursement run screen', async ({ page }) => {
  await login(page);

  // Dashboard should show the "Run owner disbursements" CTA button.
  const cta = page.getByRole('button', { name: /run owner disbursements/i });
  await expect(cta).toBeVisible();

  // Click the CTA — should navigate to /operations?tab=disbursement.
  await cta.click();
  await expect(page).toHaveURL(/\/operations\?tab=disbursement/);

  // The disbursement run screen heading should be visible.
  await expect(page.getByText('Owner disbursement run')).toBeVisible({ timeout: 10_000 });

  // The period picker selects (year + month) should be present.
  await expect(page.getByLabel('Select year')).toBeVisible();
  await expect(page.getByLabel('Select month')).toBeVisible();
});

test('operations page tab bar switches screens', async ({ page }) => {
  await login(page);
  await page.goto('/operations');

  // Default tab is disbursement.
  await expect(page.getByText('Owner disbursement run')).toBeVisible({ timeout: 10_000 });

  // Switch to rent run tab.
  await page.getByRole('button', { name: /rent charges/i }).click();
  await expect(page.getByText('Rent charge run')).toBeVisible();

  // Switch to late fee tab.
  await page.getByRole('button', { name: /late fees/i }).click();
  await expect(page.getByText('Late fee run')).toBeVisible();

  // Switch to history tab.
  await page.getByRole('button', { name: /run history/i }).click();
  // Use heading role to avoid strict-mode conflict with the tab button that also says "Run history".
  await expect(page.getByRole('heading', { name: 'Run history', exact: true })).toBeVisible();
});
