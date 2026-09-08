import { readFile } from 'node:fs/promises';
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

// Opens the ⌘K palette robustly and returns its search combobox. The global keydown listener attaches
// in a useEffect after the app shell mounts (web/src/lib/useGlobalShortcuts.ts), so a single press fired
// right after navigation can be missed in a slow (CI) environment — re-press only while the palette is
// still closed (safe against toggle). The user-facing path is unchanged: one ⌘K press opens it.
export async function openPalette(page: Page): Promise<Locator> {
  const search = page.getByRole('combobox', { name: 'Search' });
  await expect(async () => {
    await page.keyboard.press('Control+k');
    await expect(search).toBeVisible({ timeout: 1000 });
  }).toPass({ timeout: 15_000 });
  return search;
}

// The detail text a forced 500 fulfills (WP-4 step 2). Exported so specs can assert the SPA's real
// error-mapping (e.g. `ledgerMutations.ts`'s `toError`, which prefers `body.detail`) actually surfaced
// this string, rather than asserting on a generic status message.
export const ROUTE_FAIL_DETAIL = 'Simulated failure (e2e).';

/**
 * Forces every request matching `urlPattern` to fail with a 500 + JSON problem body, so a spec can
 * assert the SPA's designed error branch renders (not blank content or a raw network error). A JSON
 * body (not an empty one) matters: the client parses the response by content-type, and list/mutation
 * error-handling reads `body.detail`/`body.title` off it.
 *
 * Register this inside the owning test only, with as narrow a `urlPattern` as the assertion needs —
 * each test gets its own `page` (so a route never literally leaks to another spec's run), but a narrow
 * pattern keeps a single test's interception from shadowing an unrelated request it also happens to
 * make (e.g. a GET on the same list endpoint fired by a different part of the page).
 */
export async function routeFail(page: Page, urlPattern: string | RegExp): Promise<void> {
  await page.route(urlPattern, (route) =>
    route.fulfill({
      status: 500,
      contentType: 'application/json',
      body: JSON.stringify({ title: 'Internal Server Error', detail: ROUTE_FAIL_DETAIL }),
    }),
  );
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

/**
 * Clicks `trigger`, asserts the export request came back 200 with `contentType`, and returns the
 * bytes the browser actually wrote to disk.
 *
 * Read the downloaded file, never `response.body()`. Every export on this app goes through
 * `download()` in `web/src/api/request.ts`: the client reads the response with `parseAs: 'blob'`,
 * then hands the Blob to an anchor. Chromium does not keep a body the page consumed as a Blob
 * available to CDP's `Network.getResponseBody`, so `response.body()` has no bytes to give.
 *
 * That did not surface before Playwright 1.63: when a body came back empty against a non-empty
 * `Content-Length`, Playwright silently re-fetched the URL via `Network.loadNetworkResource` and
 * returned *that* response's bytes. Two things were wrong with leaning on it — the assertion never
 * saw what the click produced, and each run re-rendered the export server-side (the API served the
 * statement PDF twice per test). 1.63 narrowed that fallback to GETs of static subresource types
 * (font/image/media/script/stylesheet/…) and prefetches precisely because re-fetching "may produce
 * side effects on the server"; an `application/pdf` XHR no longer qualifies and comes back empty.
 *
 * The download event has none of that: one request, and the assertion is on the file the user gets.
 */
export async function captureDownload(
  page: Page,
  trigger: Locator,
  options: { urlPart: string; contentType: string },
): Promise<Buffer> {
  // Both waiters must be registered before the click, or the events race the listeners.
  const responsePromise = page.waitForResponse(
    (response) => response.url().includes(options.urlPart) && response.status() === 200,
    { timeout: 20_000 },
  );
  const downloadPromise = page.waitForEvent('download', { timeout: 20_000 });

  await trigger.click();

  const contentType = (await responsePromise).headers()['content-type'] ?? '';
  expect(contentType.toLowerCase()).toContain(options.contentType.toLowerCase());

  const download = await downloadPromise;
  const path = await download.path();
  return readFile(path);
}
