# Releases

## Scheme

stamp-on-tag — `.github/workflows/version.yml` tags every merge to `main`
`v<major>.<minor>.<build>` with a GitHub Release. Each branch's entries live in its own
`changelog.d/<branch-slug>.md` fragment; the workflow stamps them into the merge's dated section
and tags the merge commit (`changelog.d/README.md`; ADR-058). `Unreleased` stays an empty
placeholder.

## Version file

`VERSION` — the release line in `<major>.<minor>.<build>` form, whose build is a floor;
`scripts/next-version.mjs` reads it, and `scripts/stamp-changelog.mjs` through it

## Ask before bumping

always — a major or minor line is the maintainer's release decision: state the evidence and the
proposed line, and edit nothing until it is confirmed. An explicit "build" ships as a build.

## Changelog check

`node scripts/check-changelog.mjs`

From the repository root: the local form of the required **CHANGELOG [Unreleased] updated** job
(`.github/workflows/changelog.yml`, which keeps that name because the ruleset requires it by name).
It fails a branch that adds, removes or renumbers a version section, adds entries to
`[Unreleased]`, edits or deletes a fragment already on `main`, or adds a fragment that does not
parse. It never requires a fragment: a merge with none, Dependabot's included, gets a section from
its PR title.
