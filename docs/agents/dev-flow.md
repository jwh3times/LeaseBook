# Development Flow

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-20

The flow itself is `.agents/skills/ship/settings/dev-flow.md`, and `AGENTS.md` summarizes it. This file holds what this repository
adds.

## Docs and agent config

- `docs/`
- `cspell.json`
- `.markdownlint-cli2.jsonc`

## Branch types

- `security`

## Exemption details

- **Dependency bumps** are Dependabot PRs and hand-made bumps titled `build(deps):`, this repository's
  Dependabot prefix (`chore(deps):` counts the same).
- **Release prep** is a branch touching only `VERSION` and its own `changelog.d/` fragment, to start
  a new major or minor line.

## Before implementing

- **Grill before implementing a candidate.** Of the first eight Wayfinder questions grilled, five
  were materially wrong or oversized as filed. A well-specified ready issue can be wrong about scope
  as readily: check its premise and its verification recipe ([verification](verification.md#claims)).
- **Budget for `code-reviewer` and `docs-updater` before a PR, and treat their output as work.** An
  adversarial review after the build has caught real defects every functional suite passed, and a
  fix correct inside its component can be a regression in aggregate.
- **Until production alerting is deployed, alert keys are still free.** Nothing in `infra/` or
  `.github/` keys on an invariant id yet, so fix a known naming defect in anything alerting will
  depend on now rather than filing it.
- **One step per pull request**, then wait for the maintainer's "merge" before the next.
  [Merging](merging.md) covers the rest.

## Linking

A slice's link line may name its step, as in `Part of #547 (step 4: …)`. Private work is closed by hand
after the merge ([issue-tracker.md](issue-tracker.md#which-tracker)).
