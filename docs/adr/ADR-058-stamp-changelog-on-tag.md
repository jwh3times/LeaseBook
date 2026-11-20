# ADR-058: Stamp each merge's changelog section after it lands

- **Status:** Accepted
- **Date:** 2026-11-20
- **Deciders:** Jerry Holland
- **Supersedes:** [ADR-057](ADR-057-changelog-section-per-merge.md)

## Context

Under ADR-057 each branch wrote a dated section for the version its merge would mint, predicted by
`scripts/next-version.mjs`. The prediction held only for the branch that merged next. With several
pull requests open, each claimed the same version, so every merge sent the others back to renumber
their sections, re-run CI and wait again; two that both inserted a section under the same heading
went `DIRTY`. A Dependabot merge minted a tag with no section, and the next human pull request had
to backfill it before its own check passed.

The repositories that share the `jwh3times/skills` development flow are moving to a scheme where no
branch names a version (`Scheme: stamp-on-tag`, its `docs/design.md` decision 9). holland-vip,
caitlyn-holland-vip and several others run it already. Its scripts are canonical copies this
repository carries.

## Decision

**A branch writes its changelog entries to its own fragment, and the tag workflow stamps them into
the merge's dated section after the merge lands** (`Scheme: stamp-on-tag` in
`docs/agents/releases.md`).

- **Fragments.** Each branch adds `changelog.d/<branch-slug>.md`, holding only category headings and
  bullets, and never edits `CHANGELOG.md`. `ship` writes it. `changelog.d/README.md` gives the
  format.
- **The stamp.** On every push to `main`, `version.yml` runs `scripts/stamp-changelog.mjs`. It walks
  `main`'s first-parent history back to the newest three-part tag and, for each untagged merge, writes
  a dated section from the fragments that merge added, or one `### Changed` bullet from its PR title
  when it added none. The version is `next-version.mjs`'s computation over `VERSION` at that merge.
  One `chore(release): v<x.y.z> [skip ci]` commit adds the sections and deletes the fragments.
- **Tags on the merge.** Each tag goes on the merge commit, not on the stamp commit, so a tag is the
  code CI tested on that merge. The stamp commit and the tags go out in one atomic push, and a
  GitHub Release is created from each section.
- **The push bypasses the ruleset.** `GITHUB_TOKEN` cannot bypass a repository ruleset, so the push
  uses a token from the release GitHub App, the `main` ruleset's one bypass actor. Its client ID is
  the `RELEASE_APP_CLIENT_ID` variable and its key the `RELEASE_APP_PRIVATE_KEY` secret.
- **Nothing else acts on the stamp commit.** `[skip ci]` skips every push-triggered workflow,
  `version.yml` included. `deploy-dev.yml` follows CI through `workflow_run`, so it does not run for a
  stamp commit either, and `deploy-prod.yml` is manual. CodeQL's default setup still analyzes it.
- **Runs take turns.** The `version-main` concurrency group never cancels. A push rejected because
  `main` moved is not a failure: the run queued behind it stamps both merges.
- **The gate.** The required job keeps its name, `CHANGELOG [Unreleased] updated`, because the
  ruleset requires it by name. `node scripts/check-changelog.mjs` now fails a pull request that adds,
  removes or renumbers a version section, adds entries to `[Unreleased]`, edits or deletes a fragment
  already on `main`, or adds one that does not parse. It never requires a fragment, and Dependabot is
  no longer exempt.
- **The switch.** `v0.39.5` is on the last merge before the move, so the move's own merge is the
  first one stamped. The `Skipped tags` setting retires: tags on the `v0.2` to `v0.27` lines keep no
  section, and no later tag can lack one.

## Consequences

- **Pull requests merge in any order** with no renumbering, and a Dependabot merge leaves nothing for
  the next pull request to do.
- **Every tag still has a section**, and its GitHub Release body is that section rather than
  generated notes.
- **`main` moves twice per merge**: the merge, then the stamp commit about a minute later. The
  ruleset requires branches to be up to date, so updating the next branch waits for the `Version`
  run (`docs/agents/merging.md`).
- **Costs accepted.** A repository secret, a GitHub App installation and a ruleset bypass actor that
  did not exist before. A branch can no longer show the section its merge will get; the fragment is
  its draft. The required check's name still does not describe what it checks.

## Revisit trigger

Reopen if releases stop following merges (a release branch, or a deploy that batches several
merges), if anything starts to act on the stamp commit (a tag-triggered workflow, a Git-integrated
deployer that ignores `[skip ci]`), or if the bypass App's access has to widen beyond pushing the
stamp commit and tags.
