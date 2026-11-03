# Releases

## Scheme

per-merge — `.github/workflows/version.yml` tags every merge to `main`
`v<major>.<minor>.<build>` with a GitHub Release, and every merge's changelog has a dated section
for the version it mints (the policy at the top of `CHANGELOG.md`; ADR-057). `Unreleased` stays an
empty placeholder between merges.

## Skipped tags

backfill — a merged Dependabot PR (exempt from the changelog check) gets its section on the next
ship.

- **Ignore:** `v0.2.*`, `v0.3.*`, `v0.4.*`, `v0.5.*`, `v0.6.*`, `v0.7.*`, `v0.8.*`, `v0.9.*`,
  `v0.10.*`, `v0.11.*`, `v0.12.*`, `v0.13.*`, `v0.14.*`, `v0.15.*`, `v0.16.*`, `v0.17.*`, `v0.18.*`,
  `v0.19.*`, `v0.20.*`, `v0.21.*`, `v0.22.*`, `v0.23.*`, `v0.24.*`, `v0.25.*`, `v0.26.*` and
  `v0.27.*` — released before the per-merge scheme, when a build tag got no section and its changes
  went into the next minor line's section.

## Version file

`VERSION` — the release line in `<major>.<minor>.<build>` form, whose build is a floor; `version.yml`
and `scripts/next-version.mjs` read it

## Ask before bumping

always — a major or minor line is the maintainer's release decision: state the evidence and the
proposed line, and edit nothing until it is confirmed. An explicit "build" ships as a build.

## Changelog check

`node scripts/check-changelog.mjs`

From the repository root: the local form of the required **CHANGELOG [Unreleased] updated** job
(`.github/workflows/changelog.yml`, which keeps that name because the ruleset requires it by name).
The newest section must name the version this merge will mint, and every tag released since the
switch must have a section. The job exempts Dependabot; the next human PR backfills its section.
