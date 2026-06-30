# M8.2 (Spec #1) — CI e2e Foundation + Automated Accessibility Gate: Design Spec

> **Status:** Approved design, pre-implementation. **Milestone:** M8.2 (`private/TODO.md` §M8.2,
> "Product hardening"). **Scope authority:** PRD P1 acceptance / Report §4.7–4.8 (hardening) +
> §0 UX contract (status never by color alone; keyboard operability). **Predecessor:** M7
> (Migration Toolkit) — see `private/planning/m7_retro.md`. **Date:** 2026-06-30.
>
> **Sequence note:** This is the **first of three** M8.2 quality-gate specs. The user chose to
> sequence the work (foundation + a11y first). The follow-on specs — **visual regression**
> (`toHaveScreenshot` baselines) and **extended e2e coverage** (Directory navigation, error states,
> keyboard-only sequences) — are explicitly out of scope here and get their own spec → plan → build
> cycles. a11y runs first because it produces the concrete defect worklist (label/contrast/focus
> fixes) that the later polish work consumes.

## 1. Summary

Today the Playwright e2e suite runs **locally only** — the controller runs `npm run e2e` per milestone
against a seeded demo org. CI (`.github/workflows/ci.yml`) runs `format:check → lint → typecheck →
vitest → build` for the web app but **never** runs Playwright. The existing `page.screenshot()` calls
across the M3–M7 specs write PNG artifacts that **assert nothing**. So the M8.2 TODO language
("failing violations fail CI") presumes a CI e2e job that does not exist.

This spec delivers two things:

1. **A real CI e2e job** — a GitHub Actions job that stands up Postgres, applies migrations, seeds the
   `demo` and `cutover` orgs, installs the Playwright browser, and runs the full e2e suite. This is the
   foundation every later UI gate (visual regression, extended coverage) builds on.
2. **An automated WCAG 2 AA accessibility gate** — `@axe-core/playwright` asserting **zero** violations
   on every routed page in a logged-in session, plus fixing the violations the gate surfaces.

**Exit criteria:** the e2e suite (including the new `a11y.spec.ts`) runs green in CI on every PR; an
a11y regression on any covered page fails CI as a test error, not a warning; the demo golden remains
untouched (`check-invariants --org demo` exit 0).

## 2. Decisions locked in brainstorming

| #   | Decision                       | Choice                                                                                                                                                  |
| --- | ------------------------------ | ------------------------------------------------------------------------------------------------------------------------------------------------------ |
| D1  | Enforcement location           | **Build a real CI e2e job.** a11y + (later) visual failures block PR merges in GitHub Actions — not a local/controller-run gate. Matches the "prefer automated enforcement" habit. |
| D2  | Scope shape                    | **Sequence.** This spec = CI e2e job + a11y gate + fix violations. Visual regression and extended e2e coverage are separate later specs.                |
| D3  | a11y standard                  | **WCAG 2 AA** (`wcag2a`, `wcag2aa`, `wcag21a`, `wcag21aa` axe tags), per TODO M8.2.                                                                     |
| D4  | Violation triage               | **Green at merge.** Fix every reasonable violation so the gate lands green; a large out-of-scope cluster becomes a **documented, tracked `exclude`** (the worklist for a follow-up), never a silent skip. |
| D5  | ADR                            | **ADR-022 proposed** for the testing-strategy change (e2e enters CI; a11y becomes a gate). `docs-updater` confirms whether it lands.                    |
| D6  | Onboarding a11y target         | a11y-test `/onboarding` against the **`cutover`** org (demo skips onboarding once it has journal data), reusing the m7 fixture.                          |

## 3. Architecture & placement

No new application architecture — this is test infrastructure + CI + frontend a11y fixes. Three
surfaces change:

- **`.github/workflows/ci.yml`** — a new `e2e` job, parallel to the existing `backend` / `web` /
  `schema-drift` / `migration-check` / `secret-scan` / `container` jobs.
- **`web/e2e/`** — a new `a11y.spec.ts`, a small extracted `helpers.ts` (shared `signIn()` +
  `runA11y()`), and `@axe-core/playwright` as a dev dependency.
- **`web/src/`** — targeted a11y fixes in design-system primitives and screens that axe flags.

### 3.1 CI e2e job

Modeled on the existing `migration-check` job (which already runs a Postgres 18 service + bootstrap +
`dotnet ef database update`). Steps:

1. Postgres 18 **service container** (same image/health-check as `migration-check`).
2. Setup .NET 10 + Node 26 (cache npm).
3. `psql … -f infra/db/bootstrap.sql` — create `leasebook` db + the three roles.
4. `dotnet ef database update --project src/LeaseBook.Web` (migrator role) — apply all migrations.
5. `dotnet run --project src/LeaseBook.Web -- seed --org demo` then `-- seed --org cutover`
   (Development env, app role). Both orgs are needed: demo for the authenticated app pages, cutover
   for the `/onboarding` wizard.
6. `npm ci` → `npx playwright install --with-deps chromium` (browser binary cached by version).
7. `npm run e2e`.
8. **On failure:** upload the Playwright HTML report + traces (`playwright-report/`, `test-results/`)
   as a build artifact. CI `retries: 2` is already configured in `playwright.config.ts`.

**Connection-string wiring (the one real integration detail).** Playwright's `webServer` block boots
the .NET host with only `ASPNETCORE_ENVIRONMENT=Development` set. In CI the host must point at the
service-container Postgres as the **app** role (not migrator). Resolve by passing
`ConnectionStrings__Default` (app-role connection string) into the `webServer[0].env` in
`playwright.config.ts`, sourced from a job env var, so the same config works locally (dev default)
and in CI (service container). The seed steps (5) run as the app role too, so the connection string is
shared. This must be verified against the host's actual Development connection-string resolution
before the job is declared green.

### 3.2 a11y spec

`web/e2e/helpers.ts` (new, shared):

- `signIn(page)` — the canonical login flow the specs currently inline; extracted so `a11y.spec.ts`
  and future specs reuse one source of truth.
- `runA11y(page, { exclude? })` — wraps `new AxeBuilder({ page }).withTags(['wcag2a','wcag2aa',
  'wcag21a','wcag21aa'])`, applies any documented `exclude` selectors, runs, and asserts
  `results.violations` is empty. On failure it prints the violation `id`, `impact`, `help`, and
  affected node targets so the CI log is actionable.

`web/e2e/a11y.spec.ts`:

- **Demo session** — for each routed page, navigate and `runA11y`: dashboard, tenants index, a tenant
  detail/ledger, owners index, an owner detail, properties index, a property detail, banking, reports,
  operations, settings.
- **Cutover session** — navigate to `/onboarding` and `runA11y` on the wizard's initial step.
- Reaching detail pages reuses the navigation the existing specs already prove works (e.g. ⌘K → tenant,
  owners list row-click), so the a11y spec asserts on real rendered state, not fabricated routes.

### 3.3 a11y fixes

Iterate with the **`react-frontend`** specialist: run axe locally, fix real violations in the ported
design-system components and screens. Likely candidates (to be confirmed by the actual run, not
assumed): form-control labels/`aria-label`, accessible names on icon-only buttons, focus order and
focus-return on inline composer / drawer / modal close, ARIA roles on clickable table rows, and token
contrast in light **and** dark themes. Status badges already carry icon+label by design-token contract
(§0) — axe's `color-contrast` rule plus a manual badge-variant check confirms it.

**Triage (D4):** fix everything reasonable → gate green. If axe surfaces a large cluster that is
genuinely out of this spec's scope (e.g. a third-party-rendered surface, or a deep refactor), convert
**those specific rules/selectors** to a documented `exclude` with an inline comment naming the reason
and the follow-up, so the gate lands green and the exclusion is the tracked worklist. No silent skips.

## 4. Error handling, flakiness & performance

- **Flake surface.** The e2e suite is serial, single-worker, and boots .NET + Vite + Postgres. It is
  the slowest and most flake-prone CI job by nature. Mitigations: it is an **isolated** job (a flake
  fails only e2e, not the fast `backend`/`web` gates), `retries: 2` in CI, and trace-on-first-retry +
  uploaded artifacts make failures diagnosable.
- **No snapshot-platform problem here.** Visual snapshots (and their Windows-local vs Ubuntu-CI
  baseline mismatch) are deferred to the visual-regression spec. This spec introduces **no**
  `toHaveScreenshot` baselines, so the Ubuntu CI runner needs no Windows-authored fixtures.
- **Determinism.** The a11y spec asserts only structural accessibility properties (labels, roles,
  contrast, focus) which are seed-stable — it does not assert on figures, so it is insulated from any
  future golden change.

## 5. Testing & verification (Definition of Done)

This spec **is** test infrastructure, so "tests at the right altitude" means the gate itself is proven:

1. `dotnet build` / `dotnet test` / `npm run lint` / `typecheck` / `test` / `build` / `format:check`
   all green (the existing gates, unbroken by the new dev dep and helper extraction).
2. `npm run e2e` green locally **including** `a11y.spec.ts` (zero violations or only documented
   exclusions), against a freshly seeded `demo` + `cutover`.
3. The new `e2e` CI job runs green on the PR — proving the job actually boots, seeds, and passes in
   GitHub Actions (not just locally).
4. **Negative control:** temporarily introduce a known violation (e.g. an unlabeled input) locally and
   confirm `a11y.spec.ts` fails — the gate must be non-vacuous (the e2e-catches-drift lesson:
   a green suite that never fails proves nothing).
5. `check-invariants --org demo` exit 0 — the demo golden is untouched (this spec adds no journal
   data; seeding is unchanged).
6. Docs updated: `docs/runbooks/local-dev.md` (run e2e + a11y locally, install the browser),
   README/CLAUDE.md command list, and ADR-022 if `docs-updater` confirms it.

## 6. Out of scope (this spec)

- **Visual regression** — `toHaveScreenshot()`, committed baselines, threshold/`snapshotDir` config,
  and the cross-platform baseline strategy. Next spec in the sequence.
- **Extended e2e coverage** — Directory index navigation paths, error/rejection-state assertions, and
  keyboard-only behavioral sequences (composer Tab/Enter/Escape, reconcile Select-All→Finalize, ⌘K
  arrow navigation). Third spec in the sequence. Note: keyboard *behavioral* tests are distinct from
  axe rules; focus fixes that axe flags are still fixed **here**.
- **Deployment-gated M8.2 items** — the click-budget telemetry CI release-gate and the nightly
  trust-equation sweep alerting both need a deployed App Insights / scheduler and are tracked
  separately (M5-prep Tier 2, operator-gated).
- **Security findings 3a/3b/3c, late-fee settings UI, perf-at-300-units** — separate M8 work items,
  not part of the quality-gate sequence.
