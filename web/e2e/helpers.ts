import { expect, type Locator, type Page } from '@playwright/test';
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

// Force a theme deterministically before the app boots. ThemeProvider reads localStorage
// ('leasebook.theme' → { theme, accent, density }) synchronously on first render and storage wins
// over prefers-color-scheme, so seeding via addInitScript (runs before page scripts) pins the theme
// regardless of the CI runner's OS color-scheme. Seeds theme only → default accent (teal) + density.
// Must be called before the first navigation (i.e. before signIn).
export async function seedTheme(page: Page, theme: 'light' | 'dark'): Promise<void> {
  await page.addInitScript((t) => {
    localStorage.setItem('leasebook.theme', JSON.stringify({ theme: t }));
  }, theme);
}

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

// CI-only visual gate. Baselines are Linux (*-chromium-linux.png) and gate exclusively in the Ubuntu
// e2e job; on a local (Windows) run this no-ops so `npm run e2e` stays green despite OS render diffs.
// `target` is a Page (full-page shot) or a Locator (element-scoped). Callers pass a stable name.
export async function visualSnapshot(
  target: Page | Locator,
  name: string,
  opts: { mask?: Locator[]; fullPage?: boolean } = {},
): Promise<void> {
  if (!process.env.CI) return;
  await expect(target).toHaveScreenshot(name, {
    animations: 'disabled',
    ...(opts.mask ? { mask: opts.mask } : {}),
    ...(opts.fullPage ? { fullPage: true } : {}),
  });
}
