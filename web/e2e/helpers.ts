import { expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

export type Credentials = { email: string; password: string };

// The two seeded admins (no MFA — M0/M7 seed). Mirrors budgeted-flows / m7-onboarding specs.
export const DEMO_ADMIN: Credentials = {
  email: 'renee.calloway@tarheelpg.test',
  password: 'Tarheel-Trust-2026!',
};
export const CUTOVER_ADMIN: Credentials = {
  email: 'admin@cutover.test',
  password: 'Cutover-Trust-2026!',
};

// Canonical sign-in. Demo admin lands on /dashboard; the empty cutover org redirects to /onboarding.
export async function signIn(page: Page, creds: Credentials): Promise<void> {
  await page.goto('/login');
  await page.getByLabel('Email').fill(creds.email);
  await page.getByLabel('Password').fill(creds.password);
  await page.getByRole('button', { name: /sign in/i }).click();
  await page.waitForURL(/\/(dashboard|onboarding)/, { timeout: 15_000 });
}

const WCAG_AA_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa'];

// WCAG 2 A+AA axe scan asserting zero violations. `disableRules` are for documented,
// intentional exceptions only — each call site must comment why.
export async function runA11y(page: Page, opts: { disableRules?: string[] } = {}): Promise<void> {
  let builder = new AxeBuilder({ page }).withTags(WCAG_AA_TAGS);
  if (opts.disableRules?.length) builder = builder.disableRules(opts.disableRules);
  const { violations } = await builder.analyze();
  const report = violations
    .map(
      (v) =>
        `[${v.impact ?? 'n/a'}] ${v.id}: ${v.help}\n  ${v.nodes
          .map((n) => n.target.join(' '))
          .join('\n  ')}`,
    )
    .join('\n\n');
  expect(violations, `a11y violations on ${page.url()}:\n${report}`).toHaveLength(0);
}
