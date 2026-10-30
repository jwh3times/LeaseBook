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
- **Discover:** a repository script that reports both trees (`scripts/repository-status.mjs` and
  the like). Otherwise propose `git status --short --branch` plus, with a companion,
  `git -C private status --short --branch`.

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
- **Discover:** a `sync:main` (or similar) script in `package.json`. Otherwise
  `git pull --ff-only origin main`.

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
- **Value:** the command that verifies generated agent config (normally
  `node scripts/sync-agents.mjs --check`), the command that regenerates it (normally
  `node scripts/sync-agents.mjs`), and when to run them: `always`, or `when agent sources changed`
  (`.agents/skills/`, `.claude/agents/`).
- **Discover:** `scripts/sync-agents.mjs`, a `sync:agents:check` script, and the CI workflow that
  runs it.

## Final portability check

- **Read by:** end-session
- **Value:** `none`, or a command run last that proves the checkout (and companion) are clean,
  tracked and pushed.
- **Discover:** a `--check` mode in a repository status script. Otherwise `none`.

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
