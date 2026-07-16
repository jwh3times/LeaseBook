# ADR-023: Visual regression (CI-only Linux baselines)

- **Status:** Accepted
- **Date:** 2026-07-01
- **Milestone:** M8.2 (Product hardening) — spec #2

## Context

Spec #1 (ADR-022) put the Playwright suite in CI and added an a11y gate. The M3–M7 specs already
capture `page.screenshot()` artifacts, but they assert nothing. We want unintended UI changes to
key money-critical states to fail CI, using Playwright's built-in `toHaveScreenshot` (no third-party
service). The obstacle: `toHaveScreenshot` baselines are OS-specific, but development is on Windows
and CI runs Ubuntu.

## Decision

1. **CI-only, Linux baselines.** Visual assertions run through a `visualSnapshot()` helper that no-ops
   unless `process.env.CI` is set, so they gate only in the Ubuntu `e2e` job against committed
   `*-chromium-linux.png` baselines; local Windows runs are unaffected.
2. **Hybrid scope.** Two full-page flagship baselines (dashboard, owner statement) + five element-scoped
   baselines (dashboard KPI strip, fiduciary panel, reconcile-$0 strip, ledger composer open,
   onboarding tied-report), placed inline where the existing specs already reach those states.
3. **Determinism.** `animations: 'disabled'` + `maxDiffPixelRatio: 0.02`; the wall-clock-relative
   "Collected this month" dashboard KPI is masked; the ledger composer is captured before its random
   test amount is typed.
4. **Baseline generation = a `workflow_dispatch` Action** ("Update visual baselines") that regenerates
   on the CI Ubuntu setup and commits baselines back. The Playwright Docker image lacks the .NET SDK
   our host needs, so local Docker generation is impractical.
5. **Missing baseline fails.** We leave Playwright's default `updateSnapshots: 'missing'` in place:
   on a missing baseline it writes the file to the ephemeral runner but still **fails the run** (and
   suppresses retries for that case), so a baseline must be generated and committed deliberately —
   the gate is never silently vacuous.

## Consequences

- Unintended visual drift on the gated states fails CI, with diff images in the `playwright-report`
  artifact. Intended changes are re-baselined via the workflow.
- Baselines are Linux-only; local Windows visual feedback is not available (functional/a11y e2e still
  runs locally).
- **Dark-theme coverage (amended — the deferral above is closed).** Three theme-sensitive states are
  now gated in dark as well as light: `dashboard-full`, `ledger-composer-open`, and
  `owner-statement-full`, named with a `-dark` suffix. Coverage is deliberately narrower than the
  light set — these are the states where a token regression actually shows; the remaining light
  shots (KPI strip, reconcile strip, fiduciary panel, onboarding report) are composed from the same
  tokens and would not fail independently. Each dark spec asserts `<html data-theme="dark">` before
  snapshotting: baselines are bootstrapped from CI-rendered actuals, so a silently-failed theme seed
  would otherwise commit a light image as the dark baseline and the gate would pass forever against
  the wrong picture. Dark shots reuse their light twin's masks.
- A GITHUB_TOKEN push from the workflow does not re-trigger CI; after re-baselining, re-run the `e2e`
  check (or push any commit) to confirm the gate is green.

## Revisit trigger

If built-in Playwright snapshots prove insufficient (excessive cross-run flake the 2% tolerance can't
absorb, or a need for cross-browser pixel diffing), evaluate Percy / Chromatic / Applitools.
