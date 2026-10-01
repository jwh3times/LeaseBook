import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, runA11y, seedTheme, signIn } from './helpers';

for (const persona of ['tenant', 'owner'] as const) {
  test(`${persona} invitation, enrollment and revocation with local test delivery`, async ({
    page,
    browser,
  }) => {
    await seedTheme(page, persona === 'owner' ? 'dark' : 'light');
    await signIn(page, DEMO_ADMIN);
    await page.goto(persona === 'tenant' ? '/tenants' : '/owners');
    await page
      .getByRole('row')
      .filter({ hasText: persona === 'tenant' ? 'Jasmine Carter' : 'Hargrove Family Trust' })
      .click();
    const detailUrl = page.url();
    const email = `enrollment-${persona}-${Date.now()}@example.test`;
    await page.getByLabel('Invitation email').fill(email);
    await page.getByRole('button', { name: 'Send invitation' }).click();
    await expect(page.getByText(email, { exact: true })).toBeVisible();
    await runA11y(page);
    await page.getByRole('link', { name: 'Open test inbox' }).click();
    const invitation = page.getByRole('link', { name: `Open invitation for ${email}` });
    await expect(async () => {
      await page.getByRole('button', { name: 'Refresh inbox' }).click();
      await expect(invitation).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 15000 });
    const href = await invitation.getAttribute('href');
    await runA11y(page);
    const recipientContext = await browser.newContext();
    try {
      const recipient = await recipientContext.newPage();
      await seedTheme(recipient, persona === 'owner' ? 'dark' : 'light');
      await recipient.goto(href!);
      await expect(recipient).toHaveURL(/\/portal\/enroll$/);
      await recipient.getByLabel('Your name').fill('Invited portal user');
      await recipient.getByLabel('New password').fill('Portal-Trust-2026!');
      await runA11y(recipient);
      await recipient.getByRole('button', { name: 'Accept invitation' }).focus();
      await recipient.keyboard.press('Enter');
      await expect(recipient.getByText('Your portal access is ready.')).toBeVisible();
      await runA11y(recipient);
      await signIn(recipient, { email, password: 'Portal-Trust-2026!' });
      await expect(recipient).toHaveURL(new RegExp(`/portal/${persona}$`));
      await page.goto(detailUrl);
      await page.getByRole('button', { name: `Revoke access for ${email}` }).click();
      await page
        .getByRole('dialog')
        .getByRole('button', { name: 'Revoke access', exact: true })
        .click();
      await expect(page.getByRole('button', { name: `Revoke access for ${email}` })).toHaveCount(0);
      await recipient.reload();
      await expect(
        recipient.getByRole('heading', {
          name: persona === 'tenant' ? 'Resident access unavailable' : 'Owner access unavailable',
        }),
      ).toBeVisible();
    } finally {
      await recipientContext.close();
    }
  });
}
