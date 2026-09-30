import { expect, test, type Page } from '@playwright/test';
import { DEMO_ADMIN, PORTAL_OWNER, PORTAL_RESIDENT, seedTheme, signIn } from './helpers';

// The read-only owner portal (#464). Signs in as Owner A of the `seed --org portal` fixture.

/** Records every request whose path matches, so a denial can assert nothing was even asked for. */
function recordRequests(page: Page, pattern: RegExp): string[] {
  const seen: string[] = [];
  page.on('request', (request) => {
    if (pattern.test(new URL(request.url()).pathname)) seen.push(request.url());
  });
  return seen;
}

const STAFF_DATA = /^\/api\/(directory|dashboard|accounting|reports|banking|statements|operations)/;
const TENANT_PORTAL_DATA = /^\/api\/portal\/tenant\//;
const OWNER_PORTAL_DATA = /^\/api\/portal\/owner\//;

for (const theme of ['light', 'dark'] as const) {
  test(`owner sign-in, balance, statement PDF, denials and sign-out (${theme})`, async ({
    page,
  }) => {
    await seedTheme(page, theme);
    await signIn(page, PORTAL_OWNER);
    await expect(page).toHaveURL(/\/portal\/owner$/);
    await expect(page.getByRole('heading', { name: 'Owner A' })).toBeVisible();
    await expect(page.getByText('Balance held in trust')).toBeVisible();
    await expect(page.getByText(/(Cash|Accrual) basis\./)).toBeVisible();

    const statements = page.getByRole('table', { name: /Issued statements/ });
    await expect(statements).toBeVisible();
    const open = statements.getByRole('button', { name: /^Open PDF:/ }).first();

    // The action fetches the document itself so a failure stays in the page; assert the real
    // response the click produced, then that no unavailable/failure notice appeared.
    const response = page.waitForResponse(
      (r) => /\/api\/portal\/owner\/statements\/[0-9a-f-]+\/pdf$/.test(new URL(r.url()).pathname),
      { timeout: 20_000 },
    );
    await open.focus();
    await page.keyboard.press('Enter');
    const pdf = await response;
    expect(pdf.status()).toBe(200);
    expect(pdf.headers()['content-type']).toContain('application/pdf');
    await expect(page.getByText(/Statement document unavailable|Couldn’t open the/)).toHaveCount(0);
    await expect(open).toHaveText('Open PDF');

    await expect(page.getByRole('table', { name: 'Money paid out to you' })).toBeVisible();
    await expect(page.getByRole('table', { name: 'Trust activity' })).toContainText(
      '1 Fixture Lane',
    );

    // Direct navigation to a staff page and to the tenant portal: a deliberate access result, and
    // neither persona's data is requested.
    const staffRequests = recordRequests(page, STAFF_DATA);
    const tenantRequests = recordRequests(page, TENANT_PORTAL_DATA);
    await page.goto('/dashboard');
    await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
    await page.goto('/portal/tenant');
    await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
    expect(staffRequests).toEqual([]);
    expect(tenantRequests).toEqual([]);

    await page.getByRole('link', { name: 'Return to your account' }).focus();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/portal\/owner$/);
    await page.getByRole('link', { name: 'Account security' }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { name: 'Account security' })).toBeVisible();
    await page.getByRole('link', { name: 'Continue to LeaseBook' }).click();
    await expect(page).toHaveURL(/\/portal\/owner$/);

    await page.getByRole('button', { name: 'Sign out everywhere' }).click();
    await expect(page).toHaveURL(/\/login$/);
    await page.goto('/portal/owner');
    await expect(page).toHaveURL(/\/login$/);
  });
}

test('a resident is denied the owner portal without requesting owner data', async ({ page }) => {
  await signIn(page, PORTAL_RESIDENT);
  await expect(page).toHaveURL(/\/portal\/tenant$/);
  const ownerRequests = recordRequests(page, OWNER_PORTAL_DATA);
  await page.goto('/portal/owner');
  await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
  expect(ownerRequests).toEqual([]);
});

test('staff are denied the owner portal and keep the dashboard destination', async ({ page }) => {
  await signIn(page, DEMO_ADMIN);
  await expect(page).toHaveURL(/\/dashboard$/);
  const ownerRequests = recordRequests(page, OWNER_PORTAL_DATA);
  await page.goto('/portal/owner');
  await expect(page.getByRole('heading', { name: 'Access denied' })).toBeVisible();
  expect(ownerRequests).toEqual([]);
});
