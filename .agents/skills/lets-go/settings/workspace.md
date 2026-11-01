<!-- Generated from settings/workspace.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: workspace

`docs/agents/workspace.md` — the local checkout: its private companion and how changes there are
committed, how it syncs, and what in it may be cleaned. Each setting below is a `##` heading in
that file.

## Private companion

- **Read by:** docs-updater, end-session, handoff, lets-go, ship
- **Value:** `none`, or the companion's checkout path (normally `private/`, its own Git repository,
  ignored by this one) and its repository as a literal `owner/name` or a `discover:` value.
- **Discover:** `private/.git` exists → `git -C private remote get-url origin`. Repositories that
  keep the companion's name out of their public files use
  `discover: git -C private remote get-url origin`.

## Workspace status command

- **Read by:** end-session, handoff, lets-go, ship
- **Value:** commands that show the state of the checkout and, when there is one, the companion.
- **Default:** `git status --short --branch` and `git worktree list`; with the `Private companion`
  checked out, then `git -C <companion> fetch -q --prune origin` and
  `git -C <companion> status --short --branch` (the fetch makes its ahead/behind counts current).
- **Discover:** a repository script that reports both trees (`scripts/repository-status.mjs` and
  the like). Write the heading only for such a script.

## Companion commit checks

- **Read by:** end-session, ship
- **Value:** `none`, or a bulleted list of checks this repository adds to the `## Procedure` below
  before a companion commit: an index row to update, a docs check to run, records that must never
  be staged, an order between the two repositories.
- **Default:** `none`
- **Discover:** the repository's `AGENTS.md`, `private/README.md`, and the **Before committing**
  list under an older `Companion commit policy` heading. Keep only the checks the procedure lacks,
  and write the heading only when one remains.

## Sync with main

- **Read by:** end-session, handoff, lets-go
- **Value:** the command that brings local `main` up to date, run from `main`.
- **Default:** `node scripts/sync-main.mjs` when `Private companion` is not `none`, the canonical
  script from `jwh3times/skills`, which moves both repositories or neither; otherwise
  `git pull --ff-only origin main`.
- **Discover:** what the project's own version control needs. Write the heading only when neither
  default fits (a repository that lands through another tool).

## Companion restore command

- **Read by:** handoff, lets-go
- **Value:** the command that restores the `Private companion` checkout on a machine without it, or
  `none` when it is cloned by hand.
- **Default:** `npm run bootstrap:private`
- **Discover:** a `bootstrap:private` (or similar) script in `package.json`, and the docs that say
  how a maintainer restores the companion. Write the heading only when it differs from the
  default.

## Safe-to-remove paths

- **Read by:** end-session
- **Value:** bulleted list of regenerable paths (build, coverage and test output) that may be
  deleted once listed to the user.
- **Discover:** `.gitignore` entries for build, coverage and test output, cross-checked against the
  scripts that produce them. Never propose an ignored path that holds data (`.env`, local settings,
  captured responses).

## Never-remove paths

- **Read by:** end-session
- **Value:** bulleted list of paths that are never deleted without an explicit request, each with
  the reason when it is not obvious.
- **Discover:** ignored paths that are not regenerable — the companion, `.env*`, `*.local.*`
  settings, compose overrides, captured data — from `.gitignore` and the docs that mention them.

## Dev stack

- **Read by:** end-session
- **Value:** `none`, or the commands that show and stop the local stack, plus bulleted
  **Never run** commands (destructive resets, volume deletes).
- **Discover:** `docker-compose*.yml` / `compose*.yml`, `supabase/`, dev scripts under
  `scripts/`, and the README's local-development section.

## Generated-config check

- **Read by:** docs-updater, end-session
- **Value:** the command that verifies generated agent config and the command that regenerates it,
  run every time: the check is quick, and it also catches an orphaned mirror.
- **Default:** `node scripts/sync-agents.mjs --check`, regenerated with
  `node scripts/sync-agents.mjs`.
- **Discover:** `scripts/sync-agents.mjs` and the CI workflow that runs it. Write the heading only
  when regenerating needs more, such as a formatter run first.

## AGENTS.md blocks

- **Read by:** `scripts/sync-agents.mjs`
- **Value:** `none`, or the backticked names of the blocks in `agents-md.md` this repository takes.
- **Default:** every block, so `AGENTS.md` carries the markers of each.
- **Discover:** write the heading only when the shared text does not fit this repository yet, or at
  all, as in a repository without the flow skills.

## Final portability check

- **Read by:** end-session
- **Value:** `none`, or a command run last that proves the checkout (and companion) are clean,
  tracked and pushed.
- **Default:** `git status --porcelain` and `git log --branches --not --remotes --oneline`, here
  and, with the `Private companion` checked out, in it: empty output is clean and pushed.
- **Discover:** a `--check` mode in a repository status script. Write the heading only for such a
  script.

## Procedure

Committing changes in the `Private companion`, which `end-session` and `ship` run whenever the
session left changes there. It is the same in every repository: the agent asks first, and no
setting commits companion work without the user's yes.

1. **Inspect.** Work from the companion's own root: the outer repository's `git diff` cannot see
   inside it. Stage only the files this work changed, and review
   `git -C <companion> diff --cached` yourself. Companion content never enters the conversation,
   or any outer-repository file, issue, PR or commit message: describe the change file by file,
   and give the user the command to read the diff.
2. **Check.** The staged content holds no credential value: no private-key header, token,
   password, connection string, credential-bearing URL or `KEY=value` secret. A value-free
   reference to the secret store (`op://…`) is fine; a resolved value is an incident: stop and say
   so. Then run every `Companion commit checks` entry.
3. **Ask** whether to commit and push. On a no, leave the changes uncommitted and report them.
4. **Commit and push** as the companion's own commit, never alongside an outer-repository commit.
   Before the push, `gh repo view <companion repository> --json visibility --jq .visibility` must
   print `PRIVATE`; otherwise stop. Push with a plain `git -C <companion> push`; never force-push.
5. **Verify.** Report the commit SHA, and check that the companion is neither ahead of nor behind
   its upstream.

A companion that has diverged from its upstream (commits on both sides) is never merged, rebased,
reset or force-pushed by a skill: stop and report it.
