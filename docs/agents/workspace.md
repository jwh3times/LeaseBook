# Workspace

Settings the shared `end-session`, `handoff`, `lets-go`, `ship` and `docs-updater` skills read
(installed from `jwh3times/skills`; each skill's `settings/` folder defines them). Each `##` heading
is one setting.

## Private companion

`private/` — an independent Git repository (not a submodule), ignored by this one (`/private/` in
`.gitignore`) and absent from a public clone. Authorized maintainers restore it with
`npm run bootstrap:private`, which reads its locator from 1Password. Repository:
`discover: git -C private remote get-url origin` — the locator stays out of public files
(`AGENTS.md`). `private/README.md` indexes it.

## Workspace status command

`git status --short --branch`, then, when `private/.git` exists,
`git -C private fetch -q --prune origin` and `git -C private status --short --branch` — the fetch
makes the companion's ahead/behind counts current.

## Companion commit policy

ask — show the companion's diff and commit and push it, inside `private/` as its own repository,
only when the user says so. Public first (`private/README.md`, "Two-repository workflow"): the
public change is committed and its PR number or SHA recorded on the private follow-up issue before
the private update is committed.

- **Before committing:**
  - review the diff from `private/`'s own root: no resolved secret, token, recovery material or
    plaintext `.env` content (the `op://` references in `private/config/*.env.op` are allowed);
  - `node private/scripts/check-docs.mjs`, from the public root, passes;
  - `git ls-files private`, from the public root, prints nothing;
  - before pushing confidential content, `gh repo view` on the discovered repository reports its
    visibility as `PRIVATE`.

## Sync with main

`npm run sync:main` — from the public root, fetches, switches to `main` and fast-forwards both the
public checkout and `private/`, refusing a dirty tree or a non-fast-forward; a missing `private/` is
reported and skipped. `npm run sync:main -- --skip-private` updates only the public checkout.

## Safe-to-remove paths

- `TestResults/` at the root and under `tests/*/`; stray `*.trx` and `*.coverage`
- `web/playwright-report/`, `web/playwright-report-payments/`, `web/playwright-report-stand-in/`,
  `web/test-results/`, `web/e2e-results/`
- `web/coverage/` (unit and e2e coverage reports, ADR-055) and `StrykerOutput/` (mutation reports)
- stray `*.tsbuildinfo`
- scratch files the session left at the repository root, named one by one

## Never-remove paths

- `private/`, in any form — an independent repository; `git clean -x` would take it
- `.env`, `.env.*` (only `.env.example` is tracked)
- `*.local` at the root: `payment-fixture.local`, `payment-fixture-stripe.local` and
  `payment-fixture-standin.local` bind a development host to its dedicated payment-fixture database
  (`docs/runbooks/payment-simulation.md`); `stripe-probe.local/` holds captured Stripe sandbox runs,
  the input to `node scripts/stripe-probe.mjs scrub`
- `web/node_modules/`, `web/codegen/node_modules/` — `npm run docs:check` and
  `npm run api:generate` run from them
- `.claude/settings.local.json`, `.claude/worktrees/` — removing a worktree is the user's decision;
  it may hold unpushed commits

## Dev stack

- **Show:** `docker compose --profile full ps` — `db` (Postgres, data volume `leasebook-pgdata`),
  `pgadmin`, and the `full` profile's `migrate`, `seed` and `app`. Also report a host left running
  by `dotnet run --project src/LeaseBook.Web`, `npm run dev` in `web/`, or the Stripe stand-in
  (`scripts/stripe-stand-in.mjs`).
- **Stop** (keeps the data volume): `./scripts/dev.ps1 down`, or `./scripts/dev.sh down` where
  PowerShell is absent. Both run `docker compose --profile full down`, stopping the database and the
  full stack together; `app-down` is the same command.
- **Never run:** `./scripts/dev.ps1 reset-db` or `./scripts/dev.sh reset-db`,
  `docker compose down -v`, or any `docker volume rm` or `prune` — each wipes `leasebook-pgdata`,
  with the seeded organizations, the payment-fixture databases and the e2e template;
  `./scripts/payment-fixture.ps1 reset` and `node scripts/stripe-sandbox-run.mjs discard <run>` —
  each discards a fixture or a sandbox run.

## Generated-config check

`node scripts/sync-agents.mjs --check` (run inside the required **Web (lint + test + build)**
check), regenerated with `npm run docs:format` from `web/` then `node scripts/sync-agents.mjs`,
committing `.claude/skills/` and `.codex/agents/` — when agent sources changed (`.agents/skills/`,
`.claude/agents/`).

## Final portability check

none

## Hook: after sync

1. When the sync changed `web/package-lock.json`, run `npm ci` in `web/`; when it changed
   `.config/dotnet-tools.json`, run `dotnet tool restore`.
2. When it brought new files under `src/LeaseBook.Web/Migrations/` and the local database is
   running, say so and offer the migrator-role `dotnet ef database update` from `AGENTS.md`
   ("Database"). Never `reset-db`.

## Hook: end-session cleanup

1. `git ls-files private`, from the public root, prints nothing. Anything it prints is confidential
   material tracked by the public repository: stop and report it.
