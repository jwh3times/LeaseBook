import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, PORTAL_RESIDENT, seedTheme, signIn } from './helpers';

test('staff sign-in retains its dashboard destination and denies the tenant portal', async ({
  page,
}) => {
  await signIn(page, DEMO_ADMIN);
  await expect(page).toHaveURL(/\/dashboard$/);
  await page.goto('/portal/tenant');
  await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
});

for (const theme of ['light', 'dark'] as const) {
  test(`resident sign-in, staff denial, account security and sign-out (${theme})`, async ({
    page,
  }) => {
    await seedTheme(page, theme);
    const staffRequests: string[] = [];
    page.on('request', (request) => {
      if (/\/api\/(directory|dashboard|accounting|reports|banking)/.test(request.url()))
        staffRequests.push(request.url());
    });
    await signIn(page, PORTAL_RESIDENT);
    await expect(page.getByRole('heading', { name: 'Resident A' })).toBeVisible();
    await expect(page.getByText('Rent ledger balance')).toBeVisible();
    await expect(page.getByText('Voided', { exact: true })).toBeVisible();
    await page.goto('/dashboard');
    await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
    expect(staffRequests).toEqual([]);
    await page.getByRole('link', { name: 'Return to your account' }).focus();
    await page.keyboard.press('Enter');
    await page.getByRole('link', { name: 'Account security' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Account security' })).toBeVisible();
    await page.getByRole('link', { name: 'Continue to LeaseBook' }).click();
    await expect(page).toHaveURL(/\/portal\/tenant$/);
    await page.getByRole('button', { name: 'Sign out everywhere' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await page.goto('/portal/tenant');
    await expect(page).toHaveURL(/\/login$/);
  });
}
