import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, openPalette, ROUTE_FAIL_DETAIL, routeFail, signIn } from './helpers';

// Error/empty-state e2e (WP-4 step 2, folded with step 3): asserts the SPA's designed error and empty
// branches actually render — not a blank page, a raw network error, or a client-fabricated figure.
// Read-only/non-mutating throughout: every failure is a `page.route` interception (nothing ever reaches
// the server), and the two no-match tests are pure client-side filters over already-loaded data, so the
// seeded demo org's golden figures cannot drift.
//
// Empty-states approach (flagging for reviewer per the brief): rather than a new `--org empty` seeder
// variant, empty renders are exercised two ways — (1) real no-match interactions (a nonsense filter/
// palette term) where the branch is reachable with the demo org's real data, and (2) a `page.route`
// fulfill of a zero-item payload (matching the real `PagedResponseOfOwnerListRow` shape) for the true
// zero-data render. The cutover org is banks-and-CoA-with-no-journal-data but redirects to /onboarding,
// so it doesn't cleanly reach a list's empty branch anyway. This keeps WP-4 test-only and deterministic
// with no new seeder / CI seed-step surface — a third option beyond the two the roadmap names.

test.describe('error states', () => {
  test.beforeEach(async ({ page }) => {
    await signIn(page, DEMO_ADMIN);
  });

  test('a failed list query renders the designed error state, not a blank page or a fabricated figure', async ({
    page,
  }) => {
    // Scoped to this test only, and to the exact list endpoint the Owners page reads.
    await routeFail(page, '**/api/directory/owners*');

    await page.getByRole('button', { name: 'Owners' }).click();
    await expect(page).toHaveURL(/\/owners$/);
    // IndexView renders its own <h2> regardless of load state (web/src/components/IndexView.tsx).
    await expect(page.getByRole('heading', { name: 'Owners', level: 2 })).toBeVisible();

    const errorState = page.locator('.pf-empty');
    await expect(errorState.getByText('Couldn’t load this list')).toBeVisible();
    // No client-side financial math fabricates a figure in place of the failed server response — the
    // SPA renders server figures only, so a failed list must show no money glyph at all here.
    await expect(errorState).not.toContainText('$');
  });

  test('a failed ledger payment post surfaces the composer error alert, with no phantom row posted', async ({
    page,
  }) => {
    await page.getByRole('button', { name: 'Tenants' }).click();
    await expect(page).toHaveURL(/\/tenants$/);
    await page.getByText('Jasmine Carter').click();
    await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();

    // Scoped to this test only, and to the exact post endpoint (`ledgerMutations.ts`'s `submitLedgerEntry`
    // for the 'Payment' category) — the GET that loaded the ledger above is a different path and unaffected.
    await routeFail(page, '**/api/accounting/tenants/*/payments');

    // A distinctive, unlikely-to-collide amount: proves the assertion below isn't vacuously passing
    // against some pre-existing row that happens to share a plainer figure.
    const amount = (700 + Math.random() * 99).toFixed(2);
    await page.getByRole('button', { name: 'Record payment' }).click();
    await page.getByLabel('Amount').fill(amount);
    await page.getByLabel('Amount').press('Enter');

    const alert = page.getByRole('alert');
    await expect(alert).toBeVisible();
    await expect(alert).toContainText(ROUTE_FAIL_DETAIL);
    // The composer stays open (not reset) so the user can retry — `LedgerComposer.tsx`'s `finishPost`
    // (which clears the mode) only runs `onSuccess`.
    await expect(page.locator('.pf-composer')).toBeVisible();

    // No phantom row: the failed post must not have added a ledger row for this amount.
    await expect(page.getByRole('row').filter({ hasText: `$${amount}` })).toHaveCount(0);
  });

  test('the owners filter renders the no-match empty state for a nonsense query, not a blank list', async ({
    page,
  }) => {
    await page.getByRole('button', { name: 'Owners' }).click();
    await expect(page).toHaveURL(/\/owners$/);
    // Real, already-loaded data — proves the no-match branch below is reached by filtering, not by a
    // load failure or a zero-item list.
    await expect(page.getByText('Hargrove Family Trust')).toBeVisible();

    const nonsense = 'zzznonexistentxyz123';
    await page.getByLabel('Filter owners…').fill(nonsense);

    const noMatch = page.locator('.pf-empty');
    await expect(noMatch.getByText('No matches')).toBeVisible();
    await expect(noMatch).toContainText(nonsense);
  });

  test('the ⌘K palette renders "no matches" for a nonsense query, not a blank list', async ({
    page,
  }) => {
    const search = await openPalette(page);
    const nonsense = 'zzznonexistentxyz123';
    await search.fill(nonsense);

    const noMatch = page.locator('.pf-palette-empty');
    await expect(noMatch).toContainText('No matches for');
    await expect(noMatch).toContainText(nonsense);
  });

  test('a zero-item owners list renders the true empty state (no fabricated rows)', async ({
    page,
  }) => {
    // Fulfills the real `PagedResponseOfOwnerListRow` shape with zero items, so `IndexView` reaches its
    // true "items.length === 0" branch (OwnersPage's own `emptyTitle`) rather than the error branch.
    await page.route('**/api/directory/owners*', (route) =>
      route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ items: [], page: 1, pageSize: 200, total: 0 }),
      }),
    );

    await page.getByRole('button', { name: 'Owners' }).click();
    await expect(page).toHaveURL(/\/owners$/);
    await expect(page.getByRole('heading', { name: 'Owners', level: 2 })).toBeVisible();

    const emptyState = page.locator('.pf-empty');
    await expect(emptyState.getByText('No owners yet')).toBeVisible();
    // The zero-item branch's own action button (only rendered when items.length === 0) — distinguishes
    // this from the no-match branch, which renders no action.
    await expect(emptyState.getByRole('button', { name: 'New owner' })).toBeVisible();
  });
});
