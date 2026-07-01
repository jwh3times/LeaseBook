# M8.2 (Spec #2) — Visual Regression: Design Spec

> **Status:** Approved design, pre-implementation. **Milestone:** M8.2 (`private/TODO.md` §M8.2,
> "Product hardening" → "Visual regression"). **Scope authority:** Report §4.7–4.8 (hardening) +
> the money-critical UI (balance strips, statement, reconcile-to-$0). **Predecessor:** M8.2 spec #1
> (CI e2e foundation + a11y gate — `docs/superpowers/specs/2026-06-30-m8-ci-e2e-a11y-gate-design.md`,
> merged PR #54/#55). **Date:** 2026-06-30.
>
> **Sequence note:** Second of three M8.2 quality-gate specs. Spec #1 delivered the CI `e2e` job +
> the a11y gate. This spec adds visual regression on top of that foundation. Spec #3 (extended e2e
> coverage — Directory nav, error states, keyboard-only behavioral tests) remains separate.

## 1. Summary

The M3–M7 specs already call `page.screenshot({ path: 'e2e-results/…' })` at ~35 points, but those
files **assert nothing** — they are write-only artifacts for human review. This spec turns the
highest-value, regression-prone UI states into **committed visual baselines that fail CI on
unintended pixel drift**, using Playwright's built-in `toHaveScreenshot()` (no third-party service).

The defining constraint: `toHaveScreenshot` baselines are **OS-specific** (font/anti-alias rendering
differs between Windows and Linux), but development is on Windows and CI runs Ubuntu. The design
resolves this by making visual assertions **Linux-baseline, CI-gated only** — they never run (and so
never fail) on a local Windows `npm run e2e`; they gate exclusively in the Ubuntu `e2e` CI job against
committed `*-chromium-linux.png` baselines.

**Exit criteria:** the `e2e` CI job fails on an unintended visual change to any gated state; committed
Linux baselines exist for the target set; local `npm run e2e` (Windows) is unaffected; a documented,
one-click workflow regenerates baselines when a UI change is intentional.

## 2. Decisions locked in brainstorming

| #   | Decision                     | Choice                                                                                                                                                             |
| --- | ---------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| D1  | Baseline platform + gating   | **CI-only gate, Linux baselines.** Visual assertions run/gate only in the Ubuntu `e2e` job against committed `*-chromium-linux.png`; local runs skip them.        |
| D2  | Snapshot scope               | **Hybrid.** 2 full-page flagship baselines (dashboard, owner statement) + 5 targeted element-scoped baselines (balance strip, fiduciary panel, reconcile-$0 strip, composer open, onboarding tied-report). |
| D3  | Baseline generator           | **`workflow_dispatch` GitHub Action**, not local Docker. The Playwright Docker image has no .NET SDK and our `webServer` boots the .NET host + Vite + seeded Postgres; regenerating on the native Ubuntu CI setup (`--update-snapshots`, commit back) is the practical path. |
| D4  | Gating mechanism             | One `visualSnapshot()` helper: `if (!process.env.CI) return;` then `toHaveScreenshot`. Uniform CI gate; inline at states the existing specs already reach.        |
| D5  | Missing-baseline policy      | **Missing baseline → fail** (do NOT set `updateSnapshots: 'missing'`). A baseline must be generated + committed deliberately, else the gate is vacuous. Corrects the TODO's `'missing'` suggestion. |
| D6  | ADR                          | **ADR-023** proposed, extending the ADR-022 testing-strategy record with the visual-regression decisions.                                                          |
| D7  | Determinism                  | `animations: 'disabled'` on every snapshot; **mask** any wall-clock-relative regions on full-page shots (implementer confirms which from the first diff).          |

## 3. Architecture & placement

No application code changes — this is test infrastructure + CI. Four surfaces:

- **`web/e2e/helpers.ts`** — add `visualSnapshot()` (the CI-gated snapshot primitive). Reuses the
  file's existing role as the shared e2e helper home (from spec #1).
- **`web/playwright.config.ts`** — add the `expect.toHaveScreenshot` defaults + a centralized
  `snapshotPathTemplate`.
- **Existing specs** (`budgeted-flows`, `m3-ledger`, `m4-banking`, `m5-reports`, `m7-onboarding`) —
  add `visualSnapshot()` calls **inline at the points those flows already reach the target state**
  (no re-navigation, no duplicated deep flows).
- **`.github/workflows/update-visual-baselines.yml`** — the dispatch generator (D3).
- **`web/e2e-snapshots/`** — committed Linux baselines.

### 3.1 The `visualSnapshot` helper (`web/e2e/helpers.ts`)

```ts
import { expect, type Locator, type Page } from '@playwright/test';

// CI-only visual gate. Baselines are Linux (*-chromium-linux.png) and gate exclusively in the Ubuntu
// e2e job; on a local (Windows) run this no-ops so `npm run e2e` stays green despite OS render diffs.
// `target` is a Page (full-page) or a Locator (element-scoped). Callers pass a stable, unique name.
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
```

`process.env.CI` is set automatically by GitHub Actions and unset locally, so the same specs run
everywhere; only in CI do the visual assertions execute.

### 3.2 Config (`web/playwright.config.ts`)

Add:

```ts
  expect: {
    toHaveScreenshot: { maxDiffPixelRatio: 0.02, animations: 'disabled' },
  },
  // Centralize baselines under web/e2e-snapshots/ instead of scattered <spec>-snapshots/ dirs.
  snapshotPathTemplate: 'e2e-snapshots/{testFileName}/{arg}-{projectName}-{platform}{ext}',
```

`maxDiffPixelRatio: 0.02` is the 2% anti-alias tolerance from the TODO. Missing baselines are left at
Playwright's default behavior (write + **fail** the run) — we deliberately do not enable
`updateSnapshots: 'missing'`.

### 3.3 Snapshot targets (D2)

Each is a `visualSnapshot()` call added at the existing state in the named spec:

| # | Target | Kind | Spec / state | Notes |
|---|--------|------|--------------|-------|
| 1 | Dashboard | full-page | `budgeted-flows` after login lands on `/dashboard` | mask any wall-clock-relative KPI (e.g. "collected this month") |
| 2 | Dashboard balance/KPI strip | element | same point, scoped to the KPI-strip locator | stable |
| 3 | Owner statement | full-page | `m5-reports` statement-loaded state | mask any "generated on" date if present |
| 4 | Statement fiduciary-integrity panel | element | same state, scoped to the fiduciary panel | the compliance-as-feature panel |
| 5 | Reconcile difference `$0.00` strip | element | `m4-banking` at the reconciled-to-$0 state | money-critical |
| 6 | Inline composer (open) | element | `m3-ledger` with the composer open | the signature interaction |
| 7 | Onboarding tied-report view | element | `m7-onboarding` at the tied verification report | cutover org |

Full-page shots (#1, #3) use `animations: 'disabled'` + masks. Element shots (#2, #4–#7) are scoped to
one stable locator, which is inherently low-flake.

### 3.4 Baseline generation workflow (D3)

`.github/workflows/update-visual-baselines.yml` — `workflow_dispatch` (manual "Run workflow", runs on
the selected branch). It mirrors the `e2e` job's environment (Postgres 18 service → bootstrap → migrate
→ seed demo+cutover → `npx playwright install --with-deps chromium`), then runs
`npm run e2e -- --update-snapshots`, and **commits the regenerated `web/e2e-snapshots/` back to the
branch** (git identity = a bot; `permissions: contents: write`). This produces correct Linux baselines
on the same setup that asserts them, with no local Docker.

Bootstrap flow when a snapshot is first added: the `e2e` job fails (missing baseline) → the author runs
this workflow on the PR branch → baselines are committed → `e2e` re-runs green.

## 4. Determinism & flakiness

- **Animations off** on every snapshot (`animations: 'disabled'`) — kills the ledger new-row flash,
  transitions, and caret blink.
- **Masking** dynamic regions on the two full-page shots. The demo seed is deterministic (golden
  figures), so most content is stable; the residual risk is **wall-clock-relative** UI (a
  "collected this month" figure that depends on the current month vs the fixed seed dates, or a
  "generated on <today>" stamp). The implementer identifies these from the first CI diff and adds a
  `mask` locator; element-scoped strips avoid them by construction.
- **2% pixel tolerance** (`maxDiffPixelRatio: 0.02`) absorbs sub-pixel anti-alias noise without
  hiding real regressions.
- The `e2e` job is already serial / single-worker / `retries: 2` (spec #1), which the visual
  assertions inherit.

## 5. Testing & verification (Definition of Done)

This spec is a CI gate, so "done" means the gate is real and the baselines exist:

1. `npm run e2e` **locally (Windows) stays green** — `visualSnapshot` no-ops off-CI; no local baselines
   needed, no Windows/Linux mismatch.
2. Baselines committed under `web/e2e-snapshots/` for all 7 targets (generated via the workflow), and
   the `e2e` CI job passes against them.
3. **Non-vacuous proof:** make a deliberate visual change to one gated state (e.g. a token/color
   tweak) on a throwaway commit and confirm the `e2e` job **fails** with a diff artifact; revert.
   (The visual analogue of spec #1's injected-violation proof.)
4. The `update-visual-baselines` workflow runs green on the branch and commits baselines.
5. Existing web gate unbroken (`format:check`/`lint`/`typecheck`/`test`/`build`); `check-invariants
   --org demo` clean (no journal data touched).
6. Docs: `docs/runbooks/local-dev.md` documents adding a snapshot, regenerating baselines via the
   workflow, and reviewing a diff from CI artifacts. ADR-023 recorded (`docs-updater` confirms).

## 6. Out of scope

- **Percy / Chromatic / Applitools** — the TODO's "evaluate if built-in insufficient" line. Built-in
  Playwright snapshots first; revisit only if they prove inadequate.
- **Dark-theme visual coverage** — tracked with the dark-theme a11y follow-up; this spec snapshots the
  default (light) theme.
- **New pages/flows** — only assertions on states the existing specs already produce; no new
  navigation or fixtures.
- **Extended e2e coverage** (Directory nav, error states, keyboard behavioral) — M8.2 spec #3.
