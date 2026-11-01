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

## Companion commit checks

- Public first (`private/README.md`, "Two-repository workflow"): the public change is committed and
  its PR number or SHA recorded on the private follow-up issue before the private update is
  committed.
- `node private/scripts/check-docs.mjs`, from the public root, passes.
- `git ls-files private`, from the public root, prints nothing.
- No recovery material or plaintext `.env` content is staged (the `op://` references in
  `private/config/*.env.op` are allowed).

## Safe-to-remove paths

- `TestResults/` at the root and under `tests/*/`; stray `*.trx` and `*.coverage`
- `web/playwright-report/`, `web/playwright-report-payments/`, `web/playwright-report-stand-in/`,
  `web/test-results/`, `web/e2e-results/`
- `web/coverage/` (unit and e2e coverage reports, ADR-055) and `StrykerOutput/` (mutation reports)
- stray `*.tsbuildinfo`

## Never-remove paths

- `private/`, in any form — an independent repository; `git clean -x` would take it
- `.env`, `.env.*` (only `.env.example` is tracked)
- `*.local` at the root: `payment-fixture.local`, `payment-fixture-stripe.local` and
  `payment-fixture-standin.local` bind a development host to its dedicated payment-fixture database
  (`docs/runbooks/payment-simulation.md`); `stripe-probe.local/` holds captured Stripe sandbox runs,
  the input to `node scripts/stripe-probe.mjs scrub`
- `web/node_modules/`, `web/codegen/node_modules/` — `npm run docs:check` and
  `npm run api:generate` run from them
- `.claude/settings.local.json`

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

`node scripts/sync-agents.mjs --check && node scripts/check-skills.mjs` (run inside the required
**Web (lint + test + build)** check), regenerated with `npm run docs:format` from `web/` then `node scripts/sync-agents.mjs`,
committing `.claude/skills/` and `.codex/agents/` — always: it is quick, and it also catches an orphaned mirror.

## Hook: after sync

1. When the sync changed `.config/dotnet-tools.json`, run `dotnet tool restore`.
2. When it brought new files under `src/LeaseBook.Web/Migrations/` and the local database is
   running, say so and offer the migrator-role `dotnet ef database update` from `AGENTS.md`
   ("Database"). Never `reset-db`.

## Hook: end-session cleanup

1. `git ls-files private`, from the public root, prints nothing. Anything it prints is confidential
   material tracked by the public repository: stop and report it.
