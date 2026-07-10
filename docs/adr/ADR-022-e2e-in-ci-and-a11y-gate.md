# ADR-022: e2e in CI + automated accessibility gate

- **Status:** Accepted
- **Date:** 2026-06-30
- **Milestone:** M8.2 (Product hardening)

## Context

Through M7 the Playwright e2e suite ran **locally only** — the controller ran `npm run e2e` per
milestone against a seeded demo org. CI ran the web unit gate (`format:check`/`lint`/`typecheck`/
`vitest`/`build`) but never Playwright, and the suite's `page.screenshot()` calls asserted nothing.
The M8.2 accessibility requirement ("failing violations fail CI") therefore had no CI surface to
attach to, and accessibility was a manual checklist.

## Decision

1. Add a GitHub Actions **`e2e` job** that stands up Postgres (service container + `bootstrap.sql`),
   applies migrations as the migrator role, seeds the `demo` and `cutover` orgs as the app role,
   installs the Playwright Chromium browser, and runs the full suite. The host is booted by
   Playwright's `webServer`; CI points it at the service container via a `ConnectionStrings__Default`
   override forwarded through `playwright.config.ts`.
2. Add an **`@axe-core/playwright` WCAG 2 AA gate** (`a11y.spec.ts`) asserting zero violations on
   every routed page in a logged-in session (demo for the app, cutover for `/onboarding`). Violations
   fail CI as test errors.
3. **Triage:** fix every reasonable violation so the gate lands green; a genuinely out-of-scope
   cluster becomes a documented `exclude` selector with an inline reason + follow-up — never a silent
   skip.

## Consequences

- e2e becomes a real merge gate; accessibility regressions are caught automatically (replaces manual
  discipline — the "prefer automated enforcement" habit).
- The `e2e` job is the slowest, most flake-prone CI job (serial, single-worker, boots .NET + Vite +
  Postgres). Mitigated by job isolation (a flake fails only e2e), `retries: 2`, and uploaded
  trace/report artifacts.
- **Deferred (follow-on specs):** visual-regression baselines (`toHaveScreenshot`) landed separately
  (ADR-023); the remaining deferrals are dark-theme visual coverage and extended e2e coverage
  (Directory navigation, error states, keyboard-only sequences). This ADR covers only the CI e2e
  foundation + the a11y gate.
- The axe scan now covers both the light and dark themes (WP-2) on the default accent, guarding
  dark-theme accessibility as a merge gate; the full accent×density matrix remains an out-of-scope
  future follow-up.

## Revisit trigger

Reopen the single serial `e2e` job if suite runtime or flake rate makes the gate a merge
bottleneck (then shard/parallelize workers, or split the a11y scan into its own job). Reopen the
axe scan's scope when a new theme, accent, or density variant ships to users — the gate must
cover what users actually see, and today that is light+dark on the default accent only.
