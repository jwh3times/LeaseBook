import { expect, test, type Page } from '@playwright/test';
import { DEMO_ADMIN, openPalette, signIn } from './helpers';

// Keyboard-only operability e2e (WP-4 step 4): the flagship budgeted flow driven entirely by keyboard
// (⌘K → tenant → composer → amount → Enter posts, budget ≤ 3), palette arrow navigation, focus-return
// on dialog close, the design-system :focus-visible ring (PR #54's focus-ring rekey made this fragile),
// reconcile Select-All by keyboard, and the Settings bank-deactivate guard. Run against the shared
// seeded demo org.
//
// Cross-spec ordering hazard (resolved): specs run serially / workers:1 / alphabetical file order, so
// `keyboard-only` sorts BEFORE `m4-banking`. `m4-banking` performs a real reconcile *Finalize* on the
// demo Operating Trust (locking that month). Operating Trust is the ONLY demo account with uncleared
// items (Security Deposit Trust is fully cleared, so its "Select all uncleared" button never appears),
// so the reconcile sub-test below drives reconcile mode + Select-All on Operating Trust and asserts the
// difference reaches $0.00, then STOPS before the mutating Finalize (finalizing here would lock the
// month and break m4-banking). No server mutation occurs — the ticked selection is client-side only —
// so this is collision-free and repeatable. Finalize-by-keyboard is one Enter on the (asserted-
// focusable) Finalize button; m4-banking owns that mutation.
//
// Mutation hygiene: the keyboard payment POSTs to the demo org with a UNIQUE amount (base 41.xx, away
// from m3-ledger's 12.xx and every seeded figure) and never asserts against golden totals — adding an
// entry does not move the seeded goldens (the trust equation stays balanced; check-invariants passes).
// The Settings sub-test exercises the guard-REJECTION path (uncleared items), so the account's active
// flag never changes.

// Base 41.xx keeps this run's amount clear of m3-ledger's 12.xx range and of every seeded rent/fee.
const UNIQUE_AMOUNT = (41 + Math.floor(Math.random() * 90) / 100).toFixed(2);

async function gotoCarterLedger(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Tenants' }).click();
  await expect(page).toHaveURL(/\/tenants$/);
  await page.getByText('Jasmine Carter').click();
  await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
  await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();
}

test.describe('keyboard-only operability', () => {
  test('records a payment end-to-end by keyboard within the ≤ 3 interaction budget', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);

    // ⌘K → type the tenant → the top result is selected → Enter opens the ledger. All by keyboard.
    const search = await openPalette(page);
    await search.pressSequentially('carter');
    const topOption = page.getByRole('option').first();
    await expect(topOption).toContainText('Jasmine Carter');
    await expect(topOption).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+$/);
    await expect(page.getByRole('heading', { name: 'Jasmine Carter' })).toBeVisible();

    // Open the composer by keyboard (focus the trigger, Enter). The amount field autofocuses.
    const recordButton = page.getByRole('button', { name: 'Record payment' });
    await recordButton.focus();
    await page.keyboard.press('Enter');
    const amount = page.getByLabel('Amount');
    await expect(amount).toBeFocused();

    // Type a UNIQUE amount and post with Enter. Composer open (1) + Enter (2) = 2 interactions ≤ 3;
    // `trackInteraction` posts the budget telemetry the flow is contracted against.
    const budget = page.waitForRequest(
      (request) =>
        request.url().includes('/api/telemetry/budget') &&
        request.method() === 'POST' &&
        (request.postData() ?? '').includes('"task":"record-payment"'),
    );
    await amount.pressSequentially(UNIQUE_AMOUNT);
    await amount.press('Enter');

    const event = JSON.parse((await budget).postData() ?? '{}');
    expect(event.task).toBe('record-payment');
    expect(event.met).toBe(true);
    expect(event.interactions).toBeLessThanOrEqual(3);

    // The posted row appears inline without navigation (`.first()` tolerates a prior local run's row).
    await expect(
      page
        .getByRole('row')
        .filter({ hasText: `$${UNIQUE_AMOUNT}` })
        .first(),
    ).toBeVisible();

    // This payment is intentionally not voided (unlike m3-ledger's void-to-baseline). It is
    // golden-safe as written: a unique amount dated today (July 2026) cannot enter the May-scoped
    // goldens, no current/all-time balance is asserted here, and CI reseeds a fresh org each run.
    // If a future spec ever asserts a July-inclusive or all-time balance, void this payment first.
  });

  test('palette arrow keys move the selection and Enter activates the highlighted result', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);

    // "trust" matches the Hargrove owner + both trust banks → at least two options to move between.
    const search = await openPalette(page);
    await search.pressSequentially('trust');
    const options = page.getByRole('option');
    await expect(options.nth(1)).toBeVisible(); // ≥ 2 results rendered
    await expect(options.nth(0)).toHaveAttribute('aria-selected', 'true');

    await page.keyboard.press('ArrowDown');
    await expect(options.nth(1)).toHaveAttribute('aria-selected', 'true');
    await expect(options.nth(0)).toHaveAttribute('aria-selected', 'false');

    await page.keyboard.press('ArrowUp');
    await expect(options.nth(0)).toHaveAttribute('aria-selected', 'true');
    await expect(options.nth(1)).toHaveAttribute('aria-selected', 'false');

    // Enter activates the highlighted result: the palette closes and we navigate off the dashboard.
    await page.keyboard.press('Enter');
    await expect(page.getByRole('combobox', { name: 'Search' })).toBeHidden();
    await expect(page).not.toHaveURL(/\/dashboard/);
  });

  test('closing a dialog with Escape returns focus to the trigger', async ({ page }) => {
    await signIn(page, DEMO_ADMIN);
    await gotoCarterLedger(page);

    // Open a ledger row's History dialog by keyboard from its trigger button.
    const historyButton = page.getByRole('button', { name: 'History' }).first();
    await historyButton.focus();
    await expect(historyButton).toBeFocused();
    await page.keyboard.press('Enter');

    const dialog = page.getByRole('dialog', { name: 'History' });
    await expect(dialog).toBeVisible();

    // Escape closes it and focus returns to the trigger (WCAG 2.4.3 / 2.1.2).
    await page.keyboard.press('Escape');
    await expect(dialog).toBeHidden();
    await expect(historyButton).toBeFocused();
  });

  test('keyboard focus is shown with a design-system focus-visible ring', async ({ page }) => {
    await signIn(page, DEMO_ADMIN);

    // Tab into the app shell by keyboard → the focused control reports :focus-visible, the state the
    // design-system focus ring keys off. PR #54's focus-ring rekey made this area fragile.
    await page.keyboard.press('Tab');
    const focused = page.locator('*:focus');
    await expect(focused).toBeVisible();
    await expect.poll(() => focused.evaluate((el) => el.matches(':focus-visible'))).toBe(true);
    const tag = await focused.evaluate((el) => el.tagName);
    expect(['A', 'BUTTON', 'INPUT', 'SELECT', 'TEXTAREA']).toContain(tag);

    // The design system's own keyboard focus ring (ledger.css `.pf-ledger:focus-visible`) renders a
    // real box-shadow when the ledger grid takes keyboard focus — a direct guard on the rekey.
    await gotoCarterLedger(page);
    const grid = page.getByRole('grid', { name: 'Tenant ledger' });
    await grid.focus();
    await page.keyboard.press('ArrowDown'); // a keyboard interaction on the focused grid
    await expect.poll(() => grid.evaluate((el) => el.matches(':focus-visible'))).toBe(true);
    const shadow = await grid.evaluate((el) => getComputedStyle(el).boxShadow);
    expect(shadow).not.toBe('none');
  });

  test('reconcile mode: Select All by keyboard drives the difference to $0.00 and surfaces Finalize', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);
    await page.getByRole('button', { name: 'Banking' }).click();
    await expect(page).toHaveURL(/\/banking$/);
    // Operating Trust is the default (first, alphabetical) account and carries the seed's uncleared items.
    await expect(page.getByRole('button', { name: /Operating Trust/ })).toBeVisible();
    // Wait for the register (and its totals) to load before entering reconcile: `enterReconcile` seeds
    // the statement-ending balance from the book balance at click time, so entering before the register
    // resolves would default it to 0. The Pryor deposit is a seeded uncleared line on this account.
    await expect(page.getByRole('row').filter({ hasText: 'Pryor' })).toBeVisible();

    // Enter reconcile by keyboard.
    const reconcileButton = page.getByRole('button', { name: 'Reconcile account' });
    await reconcileButton.focus();
    await page.keyboard.press('Enter');

    // Select all uncleared by keyboard → the difference readout reaches $0.00.
    const selectAll = page.getByRole('button', { name: 'Select all uncleared' });
    await expect(selectAll).toBeVisible();
    await selectAll.focus();
    await page.keyboard.press('Enter');
    await expect(page.getByLabel('Difference')).toHaveText('$0.00');

    // Finalize is now one Enter away on the focused button. We deliberately DO NOT press it: finalizing
    // Operating Trust's current month locks the period, which m4-banking.spec.ts (runs after this file,
    // alphabetical) performs itself — finalizing here would break that spec. Prove Finalize is reachable
    // by keyboard, then stop. No server mutation has occurred (the ticked selection is client-side only).
    const finalize = page.getByRole('button', { name: 'Finalize' });
    await expect(finalize).toBeVisible();
    await finalize.focus();
    await expect(finalize).toBeFocused();

    // Leave the account exactly as found.
    await page.getByRole('button', { name: 'Exit reconcile' }).click();
    await expect(page.getByRole('button', { name: 'Reconcile account' })).toBeVisible();
  });

  test('deactivating a bank account with uncleared items is blocked by the server guard', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);
    await page.goto('/settings');

    // Operating Trust carries the seed's uncleared items → the server (409) rejects deactivation, so the
    // account's state never changes. "Operating Trust" is a unique row label among the three accounts.
    const row = page.getByRole('row').filter({ hasText: 'Operating Trust' });
    await expect(row).toBeVisible();
    await expect(row.getByText('Active')).toBeVisible();

    const deactivate = row.getByRole('button', { name: 'Deactivate' });
    await deactivate.focus();
    await page.keyboard.press('Enter');

    // The guard alert surfaces; the account stays Active (no state change).
    await expect(page.getByRole('alert')).toHaveText(
      'Clear or reconcile outstanding items before deactivating this account.',
    );
    await expect(row.getByRole('button', { name: 'Deactivate' })).toBeVisible();
    await expect(row.getByText('Active')).toBeVisible();
  });
});
