import { expect, test } from '@playwright/test';
import { runA11y } from './helpers';

// The HTTP state transitions are covered against real Identity/Postgres by AccountSecurityTests.
// This browser test isolates the required-enrollment route, keyboard path, QR and one-time display.
test('required enrollment is reachable by keyboard and shows recovery codes accessibly', async ({
  page,
}) => {
  let enabled = false;
  await page.route('**/api/auth/me', (route) =>
    route.fulfill({
      json: {
        userId: 'test-user',
        name: 'Administrator',
        email: 'admin@example.com',
        role: 'PMAdmin',
        orgId: 'test-org',
        orgName: 'Test organization',
        mfaEnabled: enabled,
        mfaEnrollmentRequired: !enabled,
      },
    }),
  );
  await page.route('**/api/auth/csrf', (route) =>
    route.fulfill({ status: 204, headers: { 'Set-Cookie': 'XSRF-TOKEN=test; Path=/' } }),
  );
  await page.route('**/api/auth/mfa/enroll', (route) =>
    route.fulfill({
      json: {
        secret: 'JBSWY3DPEHPK3PXP',
        otpauthUri: 'otpauth://totp/LeaseBook:test?secret=JBSWY3DPEHPK3PXP&issuer=LeaseBook',
      },
    }),
  );
  await page.route('**/api/auth/mfa/enroll/confirm', (route) => {
    enabled = true;
    return route.fulfill({ json: { codes: ['sample-code-one', 'sample-code-two'] } });
  });
  await page.goto('/account/security');
  await expect(page.getByRole('heading', { name: 'Account security', exact: true })).toBeVisible();
  await page.keyboard.press('Tab');
  await expect(page.getByRole('button', { name: 'Set up authenticator' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(page.getByAltText('Authenticator setup QR code')).toBeVisible();
  await runA11y(page);
  await page.getByLabel('Authentication code').focus();
  await page.keyboard.type('123456');
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  await expect(page.getByText('Save your recovery codes', { exact: true })).toBeVisible();
  await expect(page.getByLabel('Setup key')).toHaveCount(0);
  await runA11y(page);
  await page.getByRole('button', { name: 'I have saved my recovery codes' }).click();
  await expect(page.getByText('sample-code-one')).toHaveCount(0);
  await expect(page.getByText('Authenticator enabled.')).toBeVisible();
});
