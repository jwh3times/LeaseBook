# Pull request

## What & why

<!-- A sentence or two. Link the work package / issue. -->

## Definition of Done (CONTRIBUTING.md)

- [ ] Tests at the right altitude (unit / integration / golden / e2e as appropriate)
- [ ] Money-touching paths emit audit + telemetry events
- [ ] Accounting-adjacent changes pass the invariant / property / golden-file suites
- [ ] UI: empty, loading, and error states covered; keyboard path works
- [ ] UI uses the design tokens/primitives; money uses `<Money>` (tabular numerals); status never color-alone
- [ ] New org-scoped tables go through the RLS helper (schema guard stays green)
- [ ] No new MediatR / AutoMapper / FluentAssertions (licensed); no float/double for money
- [ ] Demoable on the seed org
- [ ] User-visible `src/` / `web/src/` changes recorded in `CHANGELOG.md` `[Unreleased]` (or `skip-changelog` label if none) — the `CHANGELOG [Unreleased] updated` gate enforces this
- [ ] Scope changes flagged for the maintainer's build plan (`private/TODO.md`); ADR added for any `docs/blueprint.md` default deviation

## Verification

<!-- Commands run and their results (build, tests, lint, manual smoke). -->
