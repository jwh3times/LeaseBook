import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, signIn } from './helpers';

/**
 * M8 WP-6 — the late-fee policy settings surface.
 *
 * The run is exercised in **preview only**. The demo's seeded May charges predate canonical rent
 * source references, so ADR-033 requires the preview to refuse them rather than invent an assessment
 * target. Confirming would post real charges into the golden fixture, so this spec never clicks it.
 *
 * The org policy is restored in a `finally` — the demo org is shared by every other spec, and a
 * leftover 0-day grace period would silently change what they see.
 */
test('org late-fee policy round-trips and preview requires an open rent obligation', async ({
  page,
}) => {
  await signIn(page, DEMO_ADMIN);

  await page.goto('/settings');

  const graceInput = page.getByLabel('Grace days');
  const feeInput = page.getByLabel('Flat fee');
  await expect(graceInput).toBeVisible({ timeout: 15_000 });

  const originalGrace = await graceInput.inputValue();
  const originalFee = await feeInput.inputValue();

  try {
    // A distinctive fee so the preview assertion cannot pass on a coincidental value.
    await graceInput.fill('0');
    await feeInput.fill('37');
    await page.getByRole('button', { name: /save late-fee policy/i }).click();
    await expect(page.getByText('Saved')).toBeVisible({ timeout: 10_000 });

    // Reload to prove it persisted server-side rather than only in component state — the read side
    // returned none of these fields before this WP, so the round trip is the thing worth proving.
    await page.reload();
    await expect(page.getByLabel('Grace days')).toHaveValue('0', { timeout: 15_000 });
    await expect(page.getByLabel('Flat fee')).toHaveValue('37');

    // The policy is now in force for the run engine. Preview only — never confirm.
    await page.goto('/operations?tab=latefee');
    await expect(page.getByText('Late fee run')).toBeVisible({ timeout: 15_000 });

    // The demo fixture's canonical open rent obligations are in May 2026. Late-fee eligibility is
    // obligation-specific, so preview that contractual period rather than the wall-clock month.
    await page.getByLabel('Select year').selectOption('2026');
    await page.getByLabel('Select month').selectOption('5');

    // The seed's legacy May rent entries have no canonical source reference. A delinquent aggregate
    // is no longer enough: issue #182 requires a specific, open rent obligation to assess.
    const warnings = page.getByRole('alert');
    await expect(warnings).toContainText('no open rent obligation found for 2026-05', {
      timeout: 15_000,
    });
    await expect(page.getByText('$37.00')).toHaveCount(0);

    // The confirm button must stay disabled: nothing is selected, and this spec selects nothing.
    const confirm = page.getByRole('button', { name: /select leases to charge|confirm — charge/i });
    await expect(confirm).toBeDisabled();
  } finally {
    await page.goto('/settings');
    await expect(page.getByLabel('Grace days')).toBeVisible({ timeout: 15_000 });
    await page.getByLabel('Grace days').fill(originalGrace);
    await page.getByLabel('Flat fee').fill(originalFee);
    await page.getByRole('button', { name: /save late-fee policy/i }).click();
    await expect(page.getByText('Saved')).toBeVisible({ timeout: 10_000 });
  }
});

/**
 * The per-lease override editor, reached from the tenant's ledger page. Overrides are read back and
 * restored the same way — a stray override on a demo tenant would change that tenant's late fees for
 * every other spec and for the demo itself.
 */
test('a lease late-fee override can be set and cleared from the tenant ledger', async ({
  page,
}) => {
  await signIn(page, DEMO_ADMIN);

  // Any tenant with an active lease; the directory list is the supported way in.
  await page.goto('/tenants');
  await page.getByRole('row').nth(1).click();
  await expect(page).toHaveURL(/\/tenants\/[0-9a-f-]+/i, { timeout: 15_000 });

  const lateFees = page.getByRole('button', { name: 'Late fees' });
  await expect(lateFees).toBeVisible({ timeout: 15_000 });

  try {
    await lateFees.click();
    await expect(page.getByText('Late fee policy for this lease')).toBeVisible();

    // Every field starts inherited; overriding grace days reveals its value input, which is the
    // whole point of the two-part control — "inherit" and "0" must stay distinguishable.
    const graceMode = page.getByLabel('Grace days');
    await graceMode.selectOption('override');
    await page.getByLabel('Value').first().fill('0');

    await page.getByRole('button', { name: /save overrides/i }).click();

    // The header badge is how an operator spots a lease that deviates from the org policy.
    await expect(page.getByText('Custom late fees')).toBeVisible({ timeout: 10_000 });
  } finally {
    await page.getByRole('button', { name: 'Late fees' }).click();
    await page.getByLabel('Grace days').selectOption('inherit');
    await page.getByRole('button', { name: /save overrides/i }).click();
    await expect(page.getByText('Custom late fees')).toBeHidden({ timeout: 10_000 });
  }
});
