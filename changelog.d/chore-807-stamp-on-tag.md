### Changed

- **The changelog moves from `per-merge` to `stamp-on-tag`** (#807, ADR-058, superseding ADR-057).
  A branch no longer predicts the version its merge will mint: `/ship` writes its entries to its own
  `changelog.d/<branch-slug>.md` fragment and never edits `CHANGELOG.md`. After each merge to
  `main`, the Version workflow runs the new `scripts/stamp-changelog.mjs`, which turns every
  untagged merge's fragments (or, with none, its PR title) into a dated section with its footer
  link, pushes one `chore(release): v<x.y.z> [skip ci]` commit through the release GitHub App the
  `main` ruleset lets bypass it, tags each merge commit, and creates its GitHub Release from that
  section. Open pull requests now merge in any order without renumbering, and a Dependabot merge
  gets its section without a backfill. No product behavior changes.
- **The required `CHANGELOG [Unreleased] updated` check keeps its name** but now checks fragments:
  it fails a branch that edits a version section or `[Unreleased]`, edits or deletes another
  merge's fragment, or adds one that does not parse, and it no longer exempts Dependabot.
- **The docs link check no longer excludes the predicted version's release links**, since no
  branch writes them any more.
- **The shared skills and canonical scripts are refreshed** from `jwh3times/skills`, so `ship`
  writes fragments.
