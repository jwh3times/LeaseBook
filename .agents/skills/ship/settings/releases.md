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

  May add a bulleted **Ignore** list of three-part tag patterns that predate the changelog (e.g.
  `v0.1.*`). Four-part tags (`v1.0.0.x`) never need one: `ship` reads only three-part tags.
- **Default:** `backfill` with `Scheme: per-merge`; otherwise `none`.
- **Discover:** tags with no section in `CHANGELOG.md`. Write the heading only for `roll up` or an
  **Ignore** list.

## Version file

- **Read by:** ship
- **Value:** the file that holds the current release line (`package.json`, `web/package.json`,
  `VERSION`), or `none` with `Scheme: none`.
- **Default:** `none` with `Scheme: none`; otherwise `package.json`.
- **Discover:** the file the tag workflow reads the line from. Write the heading only when it is
  another file.

## Bump command

- **Read by:** ship
- **Value:** the command that sets a new release line, with `<version>` where the new `x.y.0` goes,
  or `edit` when the version file is edited by hand. Running it twice with the same version must
  succeed, since a re-run of `ship` sets the same target again.
- **Default:** by the `Version file`:
  - a `package.json` — `npm version <version> --no-git-tag-version --allow-same-version`, run from
    the folder that holds it;
  - any other file — `printf '%s\n' <version> > <file>`;
  - `none` — `none`.
- **Discover:** the existing `ship` skill's bump step. Write the heading only when the default does
  not fit.

## Ask before bumping

- **Read by:** ship
- **Value:** `always` — confirm every major or minor bump with the user before editing the version
  file; or `when ambiguous` — state the decision and its evidence, and ask only when the evidence
  supports two levels.
- **Default:** `none` with `Scheme: none`, where nothing is bumped. Otherwise there is none: the
  value records how closely the user reviews this repository's releases, so each repository
  writes its own.
- **Discover:** the existing `ship` skill. Propose `when ambiguous`.

## Next-version command

- **Read by:** ship
- **Value:** `per-merge` only; otherwise `none`. The command that prints the version the next
  merge will mint, as a bare SemVer — the same computation the tag workflow makes.
- **Default:** `none` unless `Scheme: per-merge`, which has no default yet.
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
- **Default:** `none` with `Scheme: none`; otherwise there is none.
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
