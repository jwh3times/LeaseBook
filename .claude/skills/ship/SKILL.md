---
name: ship
description: Use when a branch is ready for review or the user says "ship it", "open a PR", or "push this" — refreshes docs, updates the CHANGELOG [Unreleased] section, flags private-roadmap WP drift, runs the fast checks, pushes, and opens or updates the PR. LeaseBook-specific.
---

# Ship

Take the current branch from "code is done" to "PR is open and green-able": refresh docs,
record the change in the changelog, run the cheap gates, push, and open or update the PR.

**Announce at start:** "I'm using the ship skill to open a PR for this branch."

## Why this exists

LeaseBook's `CHANGELOG.md` uses a specific **cut policy** (see the top of that file):
`[Unreleased]` is the **accumulator**. Every merge to `main` is auto-tagged an
incrementing build (`v0.2.1`, `v0.2.2`, …) by `.github/workflows/version.yml`, but those
per-merge build tags get **no** changelog section of their own — they roll up into the next
cut. A dated section is cut only on a **deliberate `VERSION` major/minor bump**.

So on an ordinary ship there is **no version to compute** and **no dated section to write** —
you add the branch's user-visible changes to `[Unreleased]`. The `changelog.yml` CI gate
fails a PR that touches product source without updating `[Unreleased]`, which is why this
step is not optional.

This skill stops at "PR open". The repo tags and releases on merge; it does not self-merge.

## Steps

### 1. Preconditions — stop if any fail

- **Not on `main`.** `main` is protected. If on `main`, stop and offer to branch
  (`git checkout -b <type>/<topic>`, e.g. `feat/owner-statement-pdf`).
- **Clean working tree.** Run `git status --porcelain`. If anything is uncommitted, stop and
  ask whether to commit it — do not commit silently. (This also makes the `git add -A` in
  step 5 safe: the only changes left will be this skill's own doc/changelog edits.)
- **`gh` authenticated.** `gh auth status` must succeed.

### 2. Refresh the docs

Compute the branch's diff base and hand the diff to the `docs-updater` subagent:

```
git fetch -q origin main
git diff $(git merge-base origin/main HEAD)..HEAD --stat
```

Invoke the `docs-updater` subagent (Agent tool, `subagent_type: docs-updater`), scoped to
**this branch's diff only** — not a full audit. Tell it exactly what changed and let it update
the docs it owns (README.md, AGENTS.md, `docs/`, ADRs, runbooks, etc.). It runs
`npm run docs:check` itself.

**Tell it to leave `CHANGELOG.md` alone** — you own the changelog in step 3, so you don't
fight over the file. `CHANGELOG.md` is not in the docs-updater topology anyway.

### 2b. Flag private-roadmap WP drift (warn only — never edit or commit `private/`)

The detailed plan lives in `private/roadmap.md` — **gitignored, so CI can never see it.** This local
ship ritual is therefore the _only_ place its drift can be caught. That file's §10 ("Keeping this
document honest") requires each WP's PR to tick that WP's own checkboxes and update the §1/§2 status
lines in the same change; the `docs-updater` in step 2 cannot do this (it has no `private/` topology).

**Skip entirely if `private/roadmap.md` is absent** — public clones and CI have no `private/` tree;
do not warn about the missing file.

Otherwise, find the WP this branch ships. The branch slug is the WP's roadmap tag, so grep for it:

```
branch=$(git branch --show-current)     # e.g. m8/compliance-pack
grep -n "$branch" private/roadmap.md    # locates the WP block; its header names the slug
```

If the slug matches no WP block (a dependabot bump, a stray fix), there is no WP to tick — skip the
rest. If it matches, read that WP block and **warn** — quoting the exact lines to fix — when any of
these still read as "not done":

- the WP block **header** carries no `✅` / **merged, PR #NN** marker, or its step checkboxes are
  still `- [ ]`;
- §1's **"verified-open positions"** list still names a gap this WP closed (e.g. "no compliance-pack
  code");
- §2's **"Remaining (do in this order)"** table still lists this WP, or the **"N of 12"** counts (§1
  "Closed since this baseline"; §2 "Complete (merged)") were not incremented;
- §3's **execution-order** line does not prefix this WP with `✅`.

Surface the findings and let the maintainer edit `private/roadmap.md` themselves. **Never edit,
stage, or commit it** — it is under `private/` (step 5 and the "Do not" list forbid it). Like the
schema-drift nudge, this only warns; it never blocks the push.

### 3. Update the CHANGELOG `[Unreleased]` section

Read the branch diff (`git diff $(git merge-base origin/main HEAD)..HEAD`) and merge the
user-visible changes into the **existing** `[Unreleased]` section.

Rules:

- Group under Keep a Changelog headings — `Added`, `Changed`, `Deprecated`, `Removed`,
  `Fixed`, `Security`. The file keeps all of a section's standard headings present; replace the
  `- _Nothing yet._` placeholder when you add an entry under a heading, and leave the
  placeholder where a heading stays empty.
- Describe user-visible behavior and its consequences, derived from the branch diff — not a
  commit log. Match the voice of the existing bolded-lead-in entries.
- **Do NOT** compute a version, write a dated `## [x.y.z]` section, or edit the
  `[Unreleased]: …compare` links. Those belong only to a deliberate `VERSION` cut, which is
  out of scope for this skill.
- **Idempotent:** if you already added entries for this branch on a previous `/ship`, rewrite
  them in place — never stack a second copy.

### 4. Fast checks — refuse to push if any fail

Tests, the container build, e2e, migration apply, and the API-client drift check are **not**
run here; CI owns them (`dotnet test` needs Docker/Testcontainers; e2e needs a seeded host).
These are the cheap gates that catch most mistakes in seconds.

Backend, from the repo root:

```
dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations
dotnet build -c Debug
```

Web, from `web/` (run **after** the step 2–3 doc/changelog edits — `docs:check` lints the whole
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

### 5. Commit the docs and changelog

```
git add -A
git commit -m "docs: update docs and changelog"
```

`git add -A` is safe because the tree was clean at step 1 — the only changes are this skill's
edits. Never stage anything under `private/`.

### 6. Push and open or update the PR

```
git push -u origin HEAD
```

Get the branch name (`git branch --show-current`) and check for an existing open PR:

```
gh pr list --head <branch> --state open --json number,url
```

- **No PR** → `gh pr create --base main` with a title and a body derived from the `[Unreleased]`
  entries you added.
- **PR exists** → `gh pr edit <number>` to refresh the body. Do not open a second PR.

**Never merge the PR. Never push to `main`.**

### 7. Report

Give the user:

- the PR URL and branch;
- what `docs-updater` changed;
- the `[Unreleased]` entries you added;
- any private-roadmap WP-drift warning from step 2b (and confirm you did **not** touch `private/`);
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
- Compute a version, write a dated `## [x.y.z]` section, or edit the compare links. Ordinary
  ships only touch `[Unreleased]`.
- Commit anything under `private/`.
- Edit or stage `private/roadmap.md` — the step 2b drift check only **warns**; the maintainer
  updates that file themselves.
