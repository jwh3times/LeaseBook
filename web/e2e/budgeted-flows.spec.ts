import { expect, test } from '@playwright/test';

// The budgeted M2 flows (§D step 6), run against the seeded demo org. The seeded admin has no MFA
// enrolled (M0 seed), so login is email + password → dashboard.
const ADMIN = 'renee.calloway@tarheelpg.test';
const PASSWORD = 'Tarheel-Trust-2026!';

async function login(page: import('@playwright/test').Page) {
  await page.goto('/login');
  await page.getByLabel('Email').fill(ADMIN);
  await page.getByLabel('Password').fill(PASSWORD);
  await page.getByRole('button', { name: /sign in/i }).click();
  await expect(page).toHaveURL(/\/dashboard/);
}

test('login lands on the dashboard with owner balances visible at 0 clicks', async ({ page }) => {
  await login(page);
  await expect(page.getByText('Owner ending balances')).toBeVisible();
  await expect(page.getByText('Hargrove Family Trust')).toBeVisible();
});

test('⌘K jumps to any tenant in ≤ 2 interactions', async ({ page }) => {
  await login(page);
  await page.keyboard.press('Control+k'); // (1) open palette
  await page.getByRole('combobox', { name: 'Search' }).fill('carter');
  await expect(page.getByText('Jasmine Carter')).toBeVisible();
  await page.keyboard.press('Enter'); // (2) jump
  await expect(page).toHaveURL(/\/tenants\//);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
});

test('list row-click opens the detail in ≤ 2 interactions', async ({ page }) => {
  await login(page);
  await page.keyboard.press('g'); // (1) go to tenants
  await page.keyboard.press('t');
  await expect(page).toHaveURL(/\/tenants$/);
  await page.getByText('Jasmine Carter').click(); // (2) open
  await expect(page).toHaveURL(/\/tenants\//);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
});

test('changing the accounting basis in settings persists across a reload', async ({ page }) => {
  await login(page);
  await page.goto('/settings');
  await page.getByLabel('Accounting basis').selectOption('accrual');
  await page.getByRole('button', { name: /save changes/i }).click();
  await expect(page.getByText('Saved')).toBeVisible();

  await page.reload();
  await expect(page.getByLabel('Accounting basis')).toHaveValue('accrual');
});
