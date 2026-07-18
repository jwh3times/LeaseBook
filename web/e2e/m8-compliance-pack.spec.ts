import { expect, test } from '@playwright/test';
import { DEMO_ADMIN, runA11y, signIn } from './helpers';

// WP-8 Trust Compliance Pack e2e (§M8), against the seeded Tarheel demo org. The pack is a
// PMAdmin-only ZIP for one trust account × a *closed* period.
//
// Closed-period wrinkle & approach: we assert the realistic OPEN-period path — the demo has no
// finalized reconciliation for a future period-end month (Dec 2026), so the backend rejects the
// download with 422 and the UI must surface a clear, non-color-only message. We deliberately do NOT
// finalize a reconciliation here: that lock is one-way (PMAdmin-unlock only) and would mutate shared
// demo state / risk the m4 spec's own finalize. The happy-path ZIP for a closed period is covered by
// the backend integration test CompliancePackEndpointTests
// (PMAdmin_downloads_a_zip_for_a_closed_period_and_the_generation_is_audited), the correct altitude
// for asserting real ZIP bytes without moving demo golden figures.
//
// The seeded admin (Renée Calloway) is a PMAdmin with no MFA, so login is email + password.

test.describe('M8 trust compliance pack', () => {
  test('PMAdmin sees the pack card and gets a clear "period not closed" message for an open period', async ({
    page,
  }) => {
    await signIn(page, DEMO_ADMIN);
    await page.goto('/reports');
    await expect(page.locator('h2').filter({ hasText: 'Reports' })).toBeVisible();

    // The PMAdmin-only compliance-pack card is present in the catalog.
    const packCard = page
      .locator('.pf-report-card')
      .filter({ hasText: 'Trust compliance pack' })
      .first();
    await expect(packCard).toBeVisible({ timeout: 15_000 });
    await packCard.click();

    // Its builder is the download panel (not the generic preview/CSV builder): the primary action is
    // "Download pack".
    const downloadBtn = page.getByRole('button', { name: 'Download pack' });
    await expect(downloadBtn).toBeVisible();
    // Disabled until a trust account is chosen.
    await expect(downloadBtn).toBeDisabled();

    // Pick the seeded Operating Trust account.
    const filters = page.getByRole('group', { name: 'Compliance pack filters' });
    await filters.getByText('Trust account').click();
    const dialog = page.getByRole('dialog', { name: 'Select Trust account' });
    await dialog.getByRole('button', { name: 'Operating Trust', exact: true }).click();

    // Choose a period whose END month (Dec 2026) is definitely not reconciliation-locked in the demo.
    await page.getByLabel('From date').fill('2026-01-01');
    await page.getByLabel('To date').fill('2026-12-31');
    await expect(downloadBtn).toBeEnabled();

    // Download → the backend rejects the open period with 422; assert we actually hit it.
    const packResponse = page.waitForResponse(
      (r) => r.url().includes('/api/reports/compliance-pack') && r.status() === 422,
      { timeout: 20_000 },
    );
    await downloadBtn.click();
    await packResponse;

    // The UI surfaces a clear, text-bearing error (never color alone — WCAG 1.4.1).
    const alert = page.getByRole('alert').filter({ hasText: /isn't closed yet/i });
    await expect(alert).toBeVisible();

    await page.screenshot({
      path: 'e2e-results/m8-compliance-pack-open-period.png',
      fullPage: true,
    });

    // WCAG 2 AA: the reports page with the pack panel + error alert is accessible.
    await runA11y(page);
  });
});
