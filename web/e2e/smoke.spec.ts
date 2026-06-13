import { expect, test } from '@playwright/test';

test('the SPA boots and shows the sign-in screen', async ({ page }) => {
  // Unauthenticated, `/` redirects to the login screen, which renders the brand.
  await page.goto('/');
  await expect(page).toHaveURL(/\/login/);
  await expect(page.getByText('LeaseBook').first()).toBeVisible();
});
