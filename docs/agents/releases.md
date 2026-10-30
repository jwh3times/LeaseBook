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

`base="$(git merge-base origin/main HEAD)"; v="$(tr -d '[:space:]' < VERSION)"; unreleased() { awk '/^## \[Unreleased\]/ { c = 1; next } /^## \[/ && c { exit } c'; }; if ! git diff --quiet "$base" -- VERSION; then grep -Eq "^## \[${v//./\\.}\] - [0-9]{4}-[0-9]{2}-[0-9]{2}$" CHANGELOG.md && grep -Fqx "[Unreleased]: https://github.com/jwh3times/LeaseBook/compare/v$v...HEAD" CHANGELOG.md && grep -Fqx "[$v]: https://github.com/jwh3times/LeaseBook/releases/tag/v$v" CHANGELOG.md; elif git diff --name-only "$base" | grep -qE '^(src|web/src)/'; then [ "$(git show "$base:CHANGELOG.md" | unreleased)" != "$(unreleased < CHANGELOG.md)" ]; fi`

From the repository root, against the working tree: the local form of the required **CHANGELOG
[Unreleased] updated** job (`.github/workflows/changelog.yml`). A `VERSION` change needs its dated
section and both release links; otherwise a change under `src/` or `web/src/` needs `Unreleased` to
change. The job also exempts Dependabot and the `skip-changelog` label: a product-source change
with no user-visible effect (a pure refactor) takes that label on the PR instead of an entry, and
this check's failure is then expected — report it.
