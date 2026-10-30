<!-- Generated from settings/releases.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: releases

`docs/agents/releases.md` — how a merge becomes a version, and how the changelog records it. Each
setting below is a `##` heading in that file.

## Scheme

- **Read by:** ship
- **Value:** one of
  - `per-merge` — every merge to `main` is tagged with the next version, and the changelog gives
    each merge a dated section for the version it will mint. `Unreleased` stays as an empty
    placeholder between merges.
  - `cut-on-line` — `Unreleased` accumulates each change's entries, whether a workflow tags every
    merge or a maintainer tags releases by hand; a dated section is cut only at a deliberate
    release, which `ship` makes when a branch starts a new major or minor line.
  - `none` — no versions and no changelog sections: `ship` skips its release steps.
- **Discover:** the tag workflow (`.github/workflows/version.yml` and the like), the policy at the
  top of `CHANGELOG.md`, how its recent sections line up with the `v*` tags, and the existing `ship`
  skill. No tag workflow and no changelog means `none`.

## Skipped tags

- **Read by:** ship
- **Value:** `per-merge` only; otherwise `none`. What happens to a tag minted by a merge that wrote
  no changelog section (a dependency bot's PR):
  - `backfill` — `ship` adds a dated section for it from the tagged merge;
  - `roll up` — its changes are folded into the next section written, and no section is invented
    for it; `ship` finds the tags released after the newest section.

  May add a bulleted **Ignore** list of tag patterns that predate the changelog (e.g. `v1.0.0.x`).
- **Discover:** the existing `ship` skill's backfill step, and tags with no section in
  `CHANGELOG.md`.

## Version file

- **Read by:** ship
- **Value:** the file that holds the current release line (`package.json`, `web/package.json`,
  `VERSION`), or `none` with `Scheme: none`.
- **Discover:** the file the tag workflow reads the line from.

## Bump command

- **Read by:** ship
- **Value:** the command that sets a new release line, with `<version>` where the new `x.y.0` goes
  (e.g. `npm version <version> --no-git-tag-version`, run from the folder that holds the version
  file), or `edit` when the version file is edited by hand.
- **Discover:** the existing `ship` skill's bump step; for an npm version file, the npm form above.

## Ask before bumping

- **Read by:** ship
- **Value:** `always` — confirm every major or minor bump with the user before editing the version
  file; or `when ambiguous` — state the decision and its evidence, and ask only when the evidence
  supports two levels.
- **Discover:** the existing `ship` skill. Default to `when ambiguous`.

## Next-version command

- **Read by:** ship
- **Value:** `per-merge` only; otherwise `none`. The command that prints the version the next
  merge will mint, as a bare SemVer — the same computation the tag workflow makes.
- **Discover:** `scripts/next-version.*`, and what the tag workflow runs.

## Unreleased placeholder

- **Read by:** ship
- **Value:** the line `Unreleased` holds when it is empty, or `empty` for a bare heading.
- **Default:** `No unreleased changes.`
- **Discover:** the current `CHANGELOG.md`. Write the heading only when it differs from the
  default.

## Changelog check

- **Read by:** ship
- **Value:** `none`, or the command that verifies the changelog names the version the merge will
  mint (the local form of a changelog CI job), run after the changelog is written.
- **Discover:** changelog jobs in `.github/workflows/` and the scripts they run
  (`scripts/verify-changelog-version.sh`, `scripts/check-changelog.mjs`).

## Release commit message

- **Read by:** ship
- **Value:** the message for the commit that carries `ship`'s own edits, with `<version>` where the
  version goes, or `<summary>` for a short description of the branch.
- **Default:** by `Scheme`:
  - `per-merge` — `docs: update docs and changelog for v<version>`;
  - `cut-on-line` — `docs: update docs and changelog` for a build, and
    `chore(release): cut <version> — <headline>` for a cut, `<headline>` naming the release;
  - `none` — `docs: update docs for <summary>`.
- **Discover:** the subjects of recent commits on `main`. Write the heading only when the
  repository's commit convention rules the default out (no type prefix, say), and name that
  convention.
