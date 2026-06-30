import { test } from '@playwright/test';
import { DEMO_ADMIN, runA11y, signIn } from './helpers';

// Automated WCAG 2 AA gate (M8.2). Asserts zero axe violations on every routed page in a logged-in
// session. 'a11y' sorts first in discovery order, so it scans the freshly-seeded demo + cutover orgs
// before any spec mutates them (m7-onboarding signs off cutover). Do not rename to sort after m7.

test.describe('a11y — demo org pages', () => {
  test('no WCAG AA violations on /dashboard', async ({ page }) => {
    await signIn(page, DEMO_ADMIN);
    await page.goto('/dashboard', { waitUntil: 'networkidle' });
    await runA11y(page);
  });
});
