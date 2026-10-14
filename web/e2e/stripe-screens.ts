import { type Browser, type Page, expect } from '@playwright/test';
import { runA11y, signIn } from './helpers';

// What the two Stripe sandbox specs share: the stand-in pass (`stripe-stand-in.spec.ts`) and a
// maintainer's run against a real sandbox (`stripe-sandbox.spec.ts`). Both drive a payment fixture
// in `StripeSandbox` mode, whose sign-ins and screens are the same whoever answers for Stripe.

/** The development-only password of every payment fixture sign-in. */
export const PASSWORD = 'Payment-Fixture-2026!';

export type Method = 'card' | 'ach';

// The fee Stripe keeps in the recorded runs, as a convenience-fee rule: 2.9% plus $0.30 of a card
// charge and 0.8% of an ACH debit, with no cap. The recorded ACH debit of $504.03 carries a balance
// line with a fee of $4.03, and every other ACH line in the recorded payout carries 0.8% too. An
// organization that charges exactly this is quoted the fee a payout's line then carries, so nothing
// is left over.
export const STRIPE_FEES: Record<Method, { group: string; rate: string; fixed: string }> = {
  card: { group: 'Card', rate: '2.9', fixed: '0.30' },
  ach: { group: 'Bank debit (ACH)', rate: '0.8', fixed: '0.00' },
};

// An axe scan reads colors as they are at that instant, so it waits for any transition to end.
export async function settled(page: Page): Promise<void> {
  await expect
    .poll(() =>
      page.evaluate(
        () => document.getAnimations().filter((motion) => motion.playState === 'running').length,
      ),
    )
    .toBe(0);
}

// The staff Operations page. `nested-interactive` is its documented deferral for the disbursement
// rows (see a11y.spec.ts).
export async function scanStaff(admin: Page): Promise<void> {
  await settled(admin);
  await runA11y(admin, { disableRules: ['nested-interactive'] });
}

export async function scanTenant(page: Page): Promise<void> {
  await settled(page);
  await runA11y(page);
}

// An administrator of a fixture organization, signed in a browser context of their own.
export async function administrator(browser: Browser, org: 'a' | 'b') {
  const staff = await browser.newContext();
  const admin = await staff.newPage();
  await signIn(admin, { email: `admin-${org}@payments.test`, password: PASSWORD });
  return { staff, admin };
}

// Sets one method's convenience-fee rule on the Settings page, with no cap. The other method's
// rule is left as it is.
export async function setFeeRule(
  admin: Page,
  group: string,
  rate: string,
  fixed: string,
): Promise<void> {
  await admin.goto('/settings');
  const rule = admin.getByRole('group', { name: group });
  await rule.getByLabel('Rate (%)').fill(rate);
  await rule.getByLabel('Fixed amount ($)').fill(fixed);
  await rule.getByLabel('Cap ($)').fill('');
  await rule.getByLabel('Cap ($)').press('Enter');
  await expect(admin.getByText('Saved', { exact: true })).toBeVisible();
}

// The organization charges for a method exactly what Stripe keeps for it.
export const chargeStripesFee = (admin: Page, method: Method) =>
  setFeeRule(admin, STRIPE_FEES[method].group, STRIPE_FEES[method].rate, STRIPE_FEES[method].fixed);

// A payout on the staff Operations page, by its own reference.
export const staffPayout = (admin: Page, id: string) =>
  admin.getByRole('listitem', { name: `Payout ${id}` });
