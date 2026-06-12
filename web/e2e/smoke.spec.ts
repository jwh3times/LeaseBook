import { expect, test } from '@playwright/test';

test('app shell renders the application name', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'LeaseBook' })).toBeVisible();
});
