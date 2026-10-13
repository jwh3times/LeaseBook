import { createHmac, randomBytes } from 'node:crypto';
import { expect, type Browser, type Locator, type Page } from '@playwright/test';
import { runA11y, SCENARIO_ADMIN, signIn, type Credentials } from './helpers';
import { test } from './test';

// Required enrollment: the screen an administrator is held on until an authenticator is set up. A
// Development host does not require it (`Auth:EnforceAdminMfa` is off outside Production), so no
// seeded sign-in reaches that state, and this test answers the session and the enrollment routes
// itself to bring it up. It covers the redirect target, the keyboard path, the QR image and the
// one-time display. The two tests after it drive the same screen against the real host.
test('required enrollment is reachable by keyboard and shows recovery codes accessibly @scenario:mfa-enrollment-by-keyboard', async ({
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

// Account security against the real host (#544): enrolling an authenticator, signing in with its
// codes and with recovery codes, and changing a password.
//
// Each test makes its own account and touches no seeded one. Enrollment cannot be undone from any
// screen, and a changed password would follow a seeded user into every later spec and the next run on
// the same database. So an administrator invites a portal tenant at an address made for the run, the
// invitation is accepted through the enrollment page, and that new person is the one whose account
// changes. Their portal access is revoked at the end; the account itself stays behind, unused.
//
// The tenant is one no other spec opens. `portal-invitations.spec.ts` asserts on Silas Byrd's panel.
const TENANT = 'Lena Osei';

// What an authenticator app computes from the setup key the screen shows: RFC 6238, HMAC-SHA1 over
// the 30-second step, six digits. The key is Base32.
function totp(setupKey: string, step: number): string {
  const alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567';
  let bits = '';
  for (const character of setupKey.replace(/[\s=-]/g, '').toUpperCase()) {
    const value = alphabet.indexOf(character);
    if (value < 0) throw new Error('The setup key is not Base32.');
    bits += value.toString(2).padStart(5, '0');
  }
  const key = Buffer.from((bits.match(/.{8}/g) ?? []).map((byte) => Number.parseInt(byte, 2)));
  const counter = Buffer.alloc(8);
  counter.writeBigUInt64BE(BigInt(step));
  const mac = createHmac('sha1', key).update(counter).digest();
  const offset = mac.readUInt8(mac.length - 1) & 0x0f;
  return ((mac.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).toString().padStart(6, '0');
}

const currentStep = () => Math.floor(Date.now() / 30_000);
const authenticatorCode = (setupKey: string) => totp(setupKey, currentStep());

// Six digits that are not the code for this step or any step near it, so the host cannot accept
// them whatever tolerance it allows for a slow clock.
function wrongCode(setupKey: string): string {
  const near = new Set<string>();
  for (let offset = -5; offset <= 5; offset += 1) near.add(totp(setupKey, currentStep() + offset));
  let candidate = 0;
  while (near.has(candidate.toString().padStart(6, '0'))) candidate += 1;
  return candidate.toString().padStart(6, '0');
}

function accessPanel(page: Page): Locator {
  return page
    .locator('.pf-card')
    .filter({ has: page.getByRole('heading', { name: 'Portal access' }) });
}

type NewPerson = {
  credentials: Credentials;
  page: Page;
  // Ends the person's portal access through the administrator's screen and closes their browser.
  leave: () => Promise<void>;
};

// An administrator invites a tenant at a new address, and the person accepts in a browser of their
// own and signs in. `portal-enrollment.spec.ts` owns the assertions about that flow; here it is the
// only way a screen makes an account.
async function newPortalTenant(
  admin: Page,
  browser: Browser,
  password: string,
): Promise<NewPerson> {
  const email = `security-${randomBytes(6).toString('hex')}@example.test`;
  await signIn(admin, SCENARIO_ADMIN);
  await admin.goto('/tenants');
  await admin.getByRole('row').filter({ hasText: TENANT }).click();
  await expect(admin.getByRole('heading', { name: TENANT })).toBeVisible();
  const tenantUrl = admin.url();
  const panel = accessPanel(admin);
  await panel.getByLabel('Invitation email').fill(email);
  await panel.getByLabel('Invitation email').press('Enter');
  await expect(panel.getByText(email, { exact: true })).toBeVisible();

  await admin.goto('/portal/test-inbox');
  const invitation = admin.getByRole('link', { name: `Open invitation for ${email}` });
  // Delivery to the test inbox follows the invitation by up to a second.
  await expect(async () => {
    await admin.getByRole('button', { name: 'Refresh inbox' }).click();
    await expect(invitation).toHaveCount(1, { timeout: 1000 });
  }).toPass({ timeout: 15_000 });
  const href = (await invitation.getAttribute('href'))!;

  const context = await browser.newContext();
  const leave = async () => {
    await context.close();
    await admin.goto(tenantUrl);
    await expect(accessPanel(admin).getByRole('heading', { name: 'Invitations' })).toBeVisible();
    const revoke = admin.getByRole('button', { name: `Revoke access for ${email}` });
    // Absent when the run stopped before the invitation was accepted.
    if ((await revoke.count()) === 0) return;
    await revoke.click();
    await admin
      .getByRole('dialog')
      .getByRole('button', { name: 'Revoke access', exact: true })
      .click();
    await expect(revoke).toHaveCount(0);
  };
  try {
    const page = await context.newPage();
    await page.goto(href);
    await page.getByLabel('Your name').fill('Account security tester');
    await page.getByLabel('New password').fill(password);
    await page.getByRole('button', { name: 'Accept invitation' }).click();
    await expect(page.getByText('Your portal access is ready.')).toBeVisible();
    const credentials = { email, password };
    await signIn(page, credentials);
    await expect(page.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();
    return { credentials, page, leave };
  } catch (error) {
    await leave();
    throw error;
  }
}

async function openAccountSecurity(page: Page): Promise<void> {
  await page.getByRole('link', { name: 'Account security' }).click();
  await expect(page.getByRole('heading', { name: 'Account security', exact: true })).toBeVisible();
}

// "Sign out everywhere" is on the portal and on the account-security page, and lands on sign-in.
async function signOut(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Sign out everywhere' }).click();
  await expect(page.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();
}

// The password step, from the keyboard, without waiting for where it leads.
async function submitPassword(page: Page, credentials: Credentials): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email').fill(credentials.email);
  await page.getByLabel('Password').fill(credentials.password);
  await page.getByLabel('Password').press('Enter');
}

// Waits for the sign-in card to stop moving before it is scanned. The code step is drawn into the
// password step's elements, so the button that was "Sign in" becomes "Use a recovery code" and its
// colors take 0.14 seconds to change. A scan inside that time reads a contrast between two colors
// that are never shown together.
async function settled(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(
        () => document.getAnimations().filter((motion) => motion.playState === 'running').length,
      ),
    )
    .toBe(0);
}

// The password step for an account with an authenticator: it stops at the code.
async function reachCodeStep(page: Page, credentials: Credentials): Promise<void> {
  await submitPassword(page, credentials);
  await expect(page.getByText('Two-factor verification')).toBeVisible();
  await expect(page.getByLabel('Authentication code')).toBeVisible();
}

test('a new portal user sets up an authenticator, then signs in with its code and with recovery codes that each work once @scenario:mfa-enrollment-and-sign-in', async ({
  page,
  browser,
}) => {
  const person = await newPortalTenant(page, browser, 'Security-Trust-2026!');
  try {
    const own = person.page;
    await openAccountSecurity(own);
    await expect(own.getByText('Manage your sign-in security.')).toBeVisible();

    // Set up, from the keyboard. The code is computed from the setup key on the screen, as an
    // authenticator app would compute it.
    await own.getByRole('button', { name: 'Set up authenticator' }).focus();
    await own.keyboard.press('Enter');
    await expect(own.getByAltText('Authenticator setup QR code')).toBeVisible();
    const setupKey = await own.getByLabel('Setup key').inputValue();
    expect(setupKey).toMatch(/^[A-Z2-7]{16,}$/);

    // A code that is not the authenticator's is refused, and the setup stays open.
    await own.getByLabel('Authentication code').fill(wrongCode(setupKey));
    await own.getByLabel('Authentication code').press('Enter');
    await expect(own.getByRole('alert')).toHaveText('Invalid code.');
    await expect(own.getByLabel('Setup key')).toHaveValue(setupKey);

    await own.getByLabel('Authentication code').fill(authenticatorCode(setupKey));
    await own.getByLabel('Authentication code').press('Enter');
    await expect(own.getByText('Save your recovery codes', { exact: true })).toBeVisible();
    await expect(own.getByLabel('Setup key')).toHaveCount(0);
    const listed = own.getByRole('list', { name: 'Recovery codes' }).getByRole('listitem');
    await expect(listed).toHaveCount(10);
    const recoveryCodes = await listed.allTextContents();
    expect(new Set(recoveryCodes).size).toBe(10);
    const [firstCode, secondCode] = recoveryCodes;
    if (!firstCode || !secondCode) throw new Error('The screen listed no recovery codes.');

    await own.getByRole('button', { name: 'I have saved my recovery codes' }).focus();
    await own.keyboard.press('Enter');
    await expect(own.getByText(firstCode)).toHaveCount(0);
    await expect(own.getByText('Authenticator enabled.')).toBeVisible();
    // The host remembers it: a fresh load offers no second setup and never shows the codes again.
    await own.reload();
    await expect(own.getByText('Authenticator enabled.')).toBeVisible();
    await expect(own.getByRole('button', { name: 'Set up authenticator' })).toHaveCount(0);
    await expect(own.getByRole('list', { name: 'Recovery codes' })).toHaveCount(0);

    // Signing in now takes the password and then a code.
    await signOut(own);
    await reachCodeStep(own, person.credentials);
    await settled(own);
    await runA11y(own);
    await own.getByLabel('Authentication code').fill(wrongCode(setupKey));
    await own.getByLabel('Authentication code').press('Enter');
    await expect(own.getByRole('alert')).toHaveText('Invalid authentication code.');
    await own.getByLabel('Authentication code').fill(authenticatorCode(setupKey));
    await own.getByLabel('Authentication code').press('Enter');
    await expect(own.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();

    // Without the authenticator, a recovery code takes its place.
    await signOut(own);
    await reachCodeStep(own, person.credentials);
    await own.getByRole('button', { name: 'Use a recovery code' }).focus();
    await own.keyboard.press('Enter');
    await expect(own.getByLabel('Recovery code')).toBeVisible();
    await settled(own);
    await runA11y(own);
    await own.getByLabel('Recovery code').fill(firstCode);
    await own.getByLabel('Recovery code').press('Enter');
    await expect(own.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();

    // Each recovery code works once: the one just used is refused, and the next one is accepted.
    await signOut(own);
    await reachCodeStep(own, person.credentials);
    await own.getByRole('button', { name: 'Use a recovery code' }).click();
    await own.getByLabel('Recovery code').fill(firstCode);
    await own.getByLabel('Recovery code').press('Enter');
    await expect(own.getByRole('alert')).toHaveText('Invalid recovery code.');
    await own.getByLabel('Recovery code').fill(secondCode);
    await own.getByLabel('Recovery code').press('Enter');
    await expect(own.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();
  } finally {
    await person.leave();
  }
});

test('a portal user changes their password, which signs their other browser out and replaces the old password @scenario:password-change', async ({
  page,
  browser,
}) => {
  const original = 'Security-Trust-2026!';
  const replacement = `Changed-${randomBytes(6).toString('hex')}-9aZ!`;
  const person = await newPortalTenant(page, browser, original);
  const otherContext = await browser.newContext();
  try {
    const own = person.page;
    // The same person, signed in on a second browser.
    const other = await otherContext.newPage();
    await signIn(other, person.credentials);
    await expect(other.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();

    await openAccountSecurity(own);
    const current = own.getByLabel('Current password');
    const next = own.getByLabel('New password');

    // The current password has to be right.
    await current.fill('Not-The-Password-2026!');
    await next.fill(replacement);
    await next.press('Enter');
    await expect(own.getByRole('alert')).toHaveText('Invalid current password.');

    // The new one has to meet the rule printed under the field.
    await current.fill(original);
    await next.fill('onlylowercaseletters');
    await next.press('Enter');
    await expect(own.getByRole('alert')).toContainText(
      'Password must contain upper and lower case letters, a number and a symbol',
    );

    await next.fill(replacement);
    await next.press('Enter');
    await expect(own.getByRole('status')).toHaveText(
      'Password changed. Other sessions have been signed out.',
    );
    await expect(current).toHaveValue('');
    await expect(next).toHaveValue('');
    await runA11y(own);

    // This browser stays signed in. The other one is turned away on its next request.
    await own.reload();
    await expect(own.getByRole('heading', { name: 'Account security', exact: true })).toBeVisible();
    await other.reload();
    await expect(other.getByRole('button', { name: 'Sign in', exact: true })).toBeVisible();

    // The old password no longer signs in, and the new one does.
    await signOut(own);
    await submitPassword(own, person.credentials);
    await expect(own.getByRole('alert')).toHaveText('Invalid email or password.');
    await signIn(own, { email: person.credentials.email, password: replacement });
    await expect(own.getByRole('heading', { name: 'Tenant portal' })).toBeVisible();
  } finally {
    await otherContext.close();
    await person.leave();
  }
});
