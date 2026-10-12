import { expect } from '@playwright/test';
import { test } from './test';

test('the SPA boots and shows the sign-in screen @scenario:spa-boot', async ({ page }) => {
  // Unauthenticated, `/` redirects to the login screen, which renders the brand.
  await page.goto('/');
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByText('LeaseBook').first()).toBeVisible();
});
