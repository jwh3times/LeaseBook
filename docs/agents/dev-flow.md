# Development Flow

- **Audience:** Coding agents and maintainers configuring engineering skills
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-10-31

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
- **Release prep** is a `chore(release): cut <version> — <headline>` commit touching only
  `CHANGELOG.md` and `VERSION`.

## Linking

A slice's link line may name its step, as in `Part of #547 (step 4: …)`. Private work is closed by hand
after the merge ([issue-tracker.md](issue-tracker.md#which-tracker)).
