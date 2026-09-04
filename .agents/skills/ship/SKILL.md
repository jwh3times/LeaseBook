---
name: ship
description: Use when a branch is ready for review or the user says "ship it", "open a PR", or "push this" — classifies the release impact as major, minor, or build; refreshes docs and the changelog; flags unlinked issues and missing board entries; runs the fast checks; pushes; and opens or updates the PR. LeaseBook-specific.
---

# Ship

Take the current branch from "code is done" to "PR is open and green-able": refresh docs,
classify its release impact, record the change in the changelog, run the cheap gates, push, and open
or update the PR.

**Announce at start:** "I'm using the ship skill to open a PR for this branch."

## Why this exists

LeaseBook's `CHANGELOG.md` uses a specific **cut policy** (see the top of that file):
`[Unreleased]` is the **accumulator**. Every merge to `main` is auto-tagged an
incrementing build (`v0.2.1`, `v0.2.2`, …) by `.github/workflows/version.yml`, but those
per-merge build tags get **no** changelog section of their own — they roll up into the next
cut. A dated section is cut only on a **deliberate `VERSION` major/minor bump**.

Every ship evaluates whether the diff warrants a major line, a minor line, or the standard build
increment. On an ordinary build ship there is no `VERSION` edit and no dated section: add the
branch's user-visible changes to `[Unreleased]`, then let the merge workflow choose the next build.
A major/minor recommendation is a deliberate release decision and requires maintainer confirmation
before changing `VERSION` and cutting `[Unreleased]`. The `changelog.yml` CI gate fails a PR that
touches product source without updating `[Unreleased]`, which is why the changelog step is not
optional.

This skill stops at "PR open". The repo tags and releases on merge; it does not self-merge.

## Steps

### 1. Preconditions — stop if any fail

- **Not on `main`.** `main` is protected. If on `main`, stop and offer to branch
  (`git checkout -b <type>/<topic>`, e.g. `feat/owner-statement-pdf`).
- **Clean working tree.** Run `git status --porcelain`. If anything is uncommitted, stop and
  ask whether to commit it — do not commit silently. (This also makes the `git add -A` in
  step 6 safe: the only changes left will be this skill's own release-preparation edits.)
- **`gh` authenticated.** `gh auth status` must succeed.

### 2. Evaluate release impact

Fetch `origin/main` and tags, compute the merge-base diff once, and read the current `VERSION` and
the cut policy at the top of `CHANGELOG.md`:

```
git fetch -q --tags origin main
base=$(git merge-base origin/main HEAD)
git diff "$base"..HEAD --stat
git diff "$base"..HEAD
```

Classify the **product diff**, not the version number of a dependency being upgraded. Apply the
highest category that matches:

- **Major** — the first stable `1.0.0` release, or (after 1.0) an incompatible change to a supported
  product contract: public API, CLI, configuration, deployment/upgrade path, persisted data, or an
  established user workflow. A required customer/operator migration, removed behavior, or changed
  accounting meaning is incompatible. While LeaseBook remains on `0.y`, treat an incompatible
  pre-release change as at least minor unless the branch deliberately declares stable `1.0`.
- **Minor** — a backward-compatible user-visible capability or a material expansion of existing
  behavior. On `0.y`, this also carries intentionally incompatible pre-release changes that are not
  the stable `1.0` cut.
- **Build** — backward-compatible fixes (including security fixes), internal refactors, tests, docs,
  CI/tooling, dependency maintenance, and other changes that add no material product capability.

State the recommendation, the concrete diff evidence, and the proposed version action before
editing release files. The default is not evidence: inspect API/CLI/config/schema and user-facing
behavior explicitly. If categories are mixed, the highest one wins.

- **Build:** leave `VERSION` unchanged. The merge workflow increments the third component; do not
  promise an exact tag because another merge can land first.
- **Major/minor already prepared in the branch:** verify that `VERSION` is the expected new line
  (`<next-major>.0.0` or `<major>.<next-minor>.0`) and that the changelog cut matches it.
- **Major/minor recommended but not prepared:** stop and ask the maintainer to confirm the proposed
  line before editing `VERSION` or cutting the changelog. If they explicitly choose build instead,
  record that decision in the final report and continue without a `VERSION` edit.

After approval, set `VERSION` to the confirmed line. The changelog step below performs the matching
dated cut. Version classification is advisory until the maintainer confirms it; never silently turn
a major/minor recommendation into a build ship.

### 3. Refresh the docs

Hand the already-computed branch diff to the `docs-updater` subagent:

```
git diff "$base"..HEAD --stat
```

Invoke the `docs-updater` subagent (Agent tool, `subagent_type: docs-updater`), scoped to
**this branch's diff only** — not a full audit. Tell it exactly what changed and let it update
the docs it owns (README.md, AGENTS.md, `docs/`, ADRs, runbooks, etc.). It runs
`npm run docs:check` itself.

**Tell it to leave `CHANGELOG.md` and `VERSION` alone** — you own the release files in step 4, so
you don't fight over them. `CHANGELOG.md` is not in the docs-updater topology anyway.

### 3b. Check issue linkage (warn only)

Work is tracked in GitHub Issues on the LeaseBook project board, not in Markdown checkboxes. This
step replaced a `private/roadmap.md` work-package drift check that went obsolete when the Track A WP
sequence closed and tracking moved to issues (2026-09-04).

Find the issue this branch closes:

```
branch=$(git branch --show-current)
gh issue list --state open --limit 100 --json number,title --jq '.[] | "\(.number) \(.title)"'
```

**Warn** — never block — when:

- the PR body carries no closing keyword (`Fixes #NN`, `Closes #NN`) **and** the change is not a
  dependency bump, a docs-only edit, or otherwise `skip-changelog`-shaped. Work that closes no issue
  is work nobody filed;
- the issue it closes is **not on the project board**, or is on it with `Track` / `Gate` unset —
  `gh project item-list 3 --owner jwh3times --format json`;
- the branch closes an issue on the **private** tracker. That reference must not appear in a public
  PR body at all; the private issue is closed by hand instead.

Surface the findings and let the maintainer decide. This only warns; it never blocks the push.

### 4. Update the changelog

Read the branch diff (`git diff "$base"..HEAD`) and derive the user-visible changelog entries.

Rules:

- Group under Keep a Changelog headings — `Added`, `Changed`, `Deprecated`, `Removed`, `Fixed`,
  `Security`. Preserve the file's existing heading and placeholder convention; replace
  `- _Nothing yet._` when adding an entry under that heading.
- Describe user-visible behavior and its consequences, derived from the branch diff — not a
  commit log. Match the voice of the existing bolded-lead-in entries.
- **Idempotent:** if you already added entries for this branch on a previous `/ship`, rewrite
  them in place — never stack a second copy.

Then follow the confirmed classification:

- **Build:** merge the branch entries into `[Unreleased]`; do not write a dated section or edit
  comparison links.
- **Major/minor, cut not yet prepared:** merge the branch entries into `[Unreleased]`, then move the
  complete accumulated contents into `## [<confirmed-version>] - YYYY-MM-DD`; restore the current
  empty `[Unreleased]` heading/placeholder shape; change its comparison base to the confirmed
  version; and add the release link.
- **Major/minor, cut already prepared:** rewrite this branch's entries in the matching dated section
  and leave the new `[Unreleased]` accumulator intact. Never duplicate the entries above and below
  the cut.

For every major/minor path, the dated section, `VERSION`, and links must name the same version.

### 5. Fast checks — refuse to push if any fail

Tests, the container build, e2e, migration apply, and the API-client drift check are **not**
run here; CI owns them (`dotnet test` needs Docker/Testcontainers; e2e needs a seeded host).
These are the cheap gates that catch most mistakes in seconds.

Backend, from the repo root:

```
dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations
dotnet build -c Debug
```

Web, from `web/` (run **after** the step 3–4 docs/release edits — `docs:check` lints the whole
markdown set, root `*.md` included):

```
npm run docs:check
npm run format:check
npm run lint
npm run typecheck
```

Fix and re-run if red:

- Backend format: `dotnet format --exclude src/LeaseBook.Web/Migrations`
- Web format: `npm run format` (from `web/`)
- Markdown/docs format: `npm run docs:format` (from `web/`)

If any check is red, **stop and report — do not push.**

**Soft nudge (warn, don't block):** if the branch changed backend endpoints/DTOs (the API
surface) but `web/src/api/schema.d.ts` is unchanged, CI's `schema-drift` job will fail.
Tell the user to run `npm run api:generate` against a host running on `:5080` and commit the
result. This can't be verified locally without a running host, so warn — don't gate on it.

**Accounting-adjacent changes** (Accounting module, posting templates, migrations) rely on the
invariant, property-based, and golden-file suites, which run under `dotnet test` in CI. Per the
Definition of Done these should already have been run during development; note it in the report.

### 6. Commit the ship edits

```
git add -A
git commit -m "docs: update docs and changelog"
```

`git add -A` is safe because the tree was clean at step 1 — the only changes are this skill's docs,
changelog, and any confirmed `VERSION` edit. Never stage anything under `private/`.

### 7. Push and open or update the PR

```
git push -u origin HEAD
```

Get the branch name (`git branch --show-current`) and check for an existing open PR:

```
gh pr list --head <branch> --state open --json number,url
```

- **No PR** → `gh pr create --base main` with a title and a body derived from the changelog entries
  you added or cut.
- **PR exists** → `gh pr edit <number>` to refresh the body. Do not open a second PR.

**Never merge the PR. Never push to `main`.**

### 8. Report

Give the user:

- the PR URL and branch;
- the major/minor/build recommendation, its diff evidence, and the confirmed decision; for a
  major/minor cut, the new `VERSION`; for a build, that the exact tag is assigned on merge;
- what `docs-updater` changed;
- the changelog entries you added and whether they remain in `[Unreleased]` or were cut;
- any issue-linkage warning from step 3b (and confirm you did **not** touch `private/`);
- fast-check results, and any schema-drift or accounting-suite notes.

State plainly that the full test suites run in CI, not locally — do not imply the branch is
verified beyond the fast checks.

**First-time setup (mention once if not already done):** the `changelog.yml` gate only
enforces when marked a **required status check** on the `main` branch protection rule
("CHANGELOG [Unreleased] updated"), and the escape hatch needs the label to exist
(`gh label create skip-changelog`).

## Do not

- Merge the PR. The repo tags/releases on merge; `/ship` stops at "PR open".
- Push to `main`.
- Run the full test suites — that is CI's job and it makes this skill slow.
- Change `VERSION`, write a dated `## [x.y.z]` section, or edit compare links without a confirmed
  major/minor classification. Build ships only touch `[Unreleased]`.
- Commit anything under `private/`.
- Reference a **private-tracker** issue in a public PR body, commit message, or public issue — the
  reference itself leaks its existence. Close it by hand.
