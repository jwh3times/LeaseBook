<!-- Generated from settings/workspace.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: workspace

`docs/agents/workspace.md` — the local checkout: its private companion, how it syncs, and what in
it may be cleaned. Each setting below is a `##` heading in that file.

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

## Companion commit policy

- **Read by:** end-session, ship
- **Value:** one of
  - `ask` — report the companion's diff and ask before committing;
  - `commit` — commit and push inside the companion as its own repository, without asking;
  - `ship` — leave companion changes for `/ship`.

  May add a bulleted **Before committing** list of checks (a diff review, a value scan).
- **Discover:** the repository's `AGENTS.md`, `private/README.md`, and existing skills for how
  companion changes are committed today. Default to `ask`.

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
