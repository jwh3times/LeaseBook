# ADR-024: Enforce the changelog cut policy with a CI gate

- **Status:** Accepted
- **Date:** 2026-07-16
- **Deciders:** Engineering

## Context

`CHANGELOG.md` documents a specific cut policy: `[Unreleased]` is the accumulator, and the
per-merge build tags (`vX.Y.<build>`, minted by `version.yml`) get no section of their own —
they roll up into the next deliberate `VERSION` major/minor cut. For that policy to hold, every
merge that changes product behavior must land its entry in `[Unreleased]` before it merges;
otherwise the section silently loses changes and the next cut is incomplete.

The only thing enforcing this was a human remembering and a reviewer noticing — a CONTRIBUTING
checkbox, not a gate (the same gap ADR-012 closed for the generated API client). A feature could
merge with no changelog entry and CI stayed green.

Two forces shaped the fix:

- **Not every PR should be required to touch the changelog.** Docs-only, CI, and test changes have
  no user-visible effect, and Dependabot bumps roll up into the next cut by policy. A blanket "must
  edit `CHANGELOG.md`" rule would be noise and would train contributors to add empty entries.
- **"Did the file change" is too weak.** A PR could edit an older dated section, or fix a typo, and
  satisfy a naive file-touch check without recording its own change.

## Decision

**A dedicated CI job (`.github/workflows/changelog.yml` → "CHANGELOG [Unreleased] updated") fails a
pull request that changes product source without updating the `[Unreleased]` section.** Concretely:

- **Scope.** An entry is required only when the PR changes `src/**` or `web/src/**`. Docs-, CI-, and
  test-only PRs pass without one.
- **Exemptions.** PRs authored by `dependabot[bot]` (its bumps roll up into the next cut), and PRs
  carrying a `skip-changelog` label — a visible, deliberate escape hatch for product-source changes
  with no user-visible effect (e.g. a pure refactor).
- **Section-diff, not file-touch.** The gate extracts the `[Unreleased]` section body at `base.sha`
  and at `head.sha` and fails if they are identical. This asserts the section itself changed; it is
  not satisfied by editing an older dated section.
- **Always runs.** The job runs on every `pull_request` with no `paths:` filter, so branch
  protection always receives a conclusion — a path-filtered required check that is skipped can block
  merges. It enforces only once marked a required status check on `main`.

The companion `/ship` developer skill (`.claude/skills/ship`) prepares a branch to satisfy this gate
(docs refresh, `[Unreleased]` update, fast checks, PR open). The docs policy's mutable-command
allowlist (`scripts/check-docs.mjs`) is extended to `.claude/skills/` so skill files may name
canonical commands, as `.claude/agents/` files already do.

## Consequences

- **The cut policy is enforced, not assumed.** A behavior change cannot merge without recording
  itself in `[Unreleased]`, so a deliberate cut is complete by construction.
- **Non-shipping PRs stay frictionless.** Docs, CI, tests, and Dependabot are exempt by scope or
  author; the `skip-changelog` label handles the rare product-source-without-user-effect case.
- **Costs accepted.** The gate is advisory until marked a required status check on the `main` branch
  protection rule, and the `skip-changelog` label must exist. That label is an open-ended mute,
  bounded by being per-PR and visible in the PR's label set.

## Revisit trigger

Reopen if the scope heuristic misclassifies real changes often enough to matter — user-visible
behavior that lives outside `src/**` / `web/src/**`, or frequent `skip-changelog` use on PRs that
should carry an entry — or if the `[Unreleased]` heading or format changes such that the section
extraction no longer isolates it. If the project abandons the accumulator model for a dated section
per merge, retire the gate.
