# Releases

## Scheme

cut-on-line — `.github/workflows/version.yml` tags every merge to `main`
`v<major>.<minor>.<build>` with a GitHub Release, but a build tag gets no changelog section:
`Unreleased` accumulates every change, and a dated section is cut only by a deliberate major or
minor `VERSION` change (the cut policy at the top of `CHANGELOG.md`; ADR-024).

## Version file

`VERSION` — the requested `<major>.<minor>.<build>` line, which `version.yml` reads

## Ask before bumping

always — a major or minor line is the maintainer's release decision: state the evidence and the
proposed line, and edit nothing until it is confirmed. An explicit "build" ships as a build.

## Changelog check

`node scripts/check-changelog.mjs --source src --source web/src`

From the repository root, against the working tree: the local form of the required **CHANGELOG
[Unreleased] updated** job (`.github/workflows/changelog.yml`). A `VERSION` change needs its dated
section and both release links; otherwise a change under `src/` or `web/src/` needs `Unreleased` to
change. The job also requires a cut to move to the next `x.y.0` line with at least one entry, and
exempts Dependabot and the `skip-changelog` label: a product-source change with no user-visible
effect (a pure refactor) takes that label on the PR instead of an entry, and this check's failure
is then expected — report it.
