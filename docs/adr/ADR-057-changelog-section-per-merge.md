# ADR-057: Give every merge its own changelog section

- **Status:** Accepted
- **Date:** 2026-11-03
- **Deciders:** Jerry Holland
- **Supersedes:** [ADR-024](ADR-024-changelog-cut-policy-gate.md)

## Context

`version.yml` tags every merge to `main` with a build number (`v0.27.21`), but under ADR-024 the
changelog recorded a version only at a deliberate major or minor `VERSION` cut. Between cuts,
`[Unreleased]` accumulated, so a tag a reader can install or deploy named no section, and what a
given build contained had to be worked out from the commits. ADR-024's revisit trigger named this
case: "If the project abandons the accumulator model for a dated section per merge, retire the
gate."

The other repositories that share the `jwh3times/skills` development flow already run the
per-merge scheme: `scripts/next-version.mjs` predicts the tag a merge will mint, the same
computation the tag workflow makes, and `scripts/check-changelog.mjs` requires a section for it.
Both scripts are canonical copies this repository already carries.

## Decision

**Every merge to `main` writes a dated changelog section for the version it mints** (`Scheme:
per-merge` in `docs/agents/releases.md`).

- **One computation.** `version.yml` computes the tag with `scripts/next-version.mjs`, and the
  required changelog job runs `node scripts/check-changelog.mjs`, which reads the same script. The
  ruleset's up-to-date requirement re-runs the job when another merge lands first and takes the
  number.
- **The gate.** The job fails a pull request whose newest `## [x.y.z]` section does not name the
  version its merge will mint, whatever the pull request changes. Docs-, CI- and test-only pull
  requests get a section too: each mints a tag.
- **Exemptions.** Pull requests authored by `dependabot[bot]` still mint a tag without writing a
  section. The next human pull request backfills it (`Skipped tags: backfill`), and the gate fails
  that pull request until it does. The `skip-changelog` label no longer exempts anything.
- **The switch.** The change cut `0.28.0`. Tags on the `v0.2` to `v0.27` lines predate the scheme,
  and the `Skipped tags` **Ignore** list exempts them.
- **The job's name stays** `CHANGELOG [Unreleased] updated`, because the ruleset requires that
  check by name.

## Consequences

- **Every tag has a section.** What a build contains is in the changelog under its own version, and
  `ship` writes it, so no merge is described only by its commits.
- **Every human pull request edits `CHANGELOG.md`,** including docs and CI changes, and two open
  pull requests can claim the same version: whichever merges second renumbers its section.
- **Costs accepted.** The required check's name no longer describes what it checks. The
  `skip-changelog` label stays defined but exempts nothing.

## Revisit trigger

Reopen if releases stop following merges (a release branch, or a deploy that batches several
merges), or if renumbering after a competing merge becomes routine enough to slow merges down.
