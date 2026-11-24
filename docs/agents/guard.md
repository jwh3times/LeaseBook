# Guard

- **Audience:** Coding agents and maintainers
- **Status:** Living policy
- **Owner:** Maintainers
- **Last reviewed:** 2026-11-23

The commands and paths `scripts/guard.mjs` stops in this repository, on top of the rules it applies
everywhere. Each heading is a setting defined in `.agents/skills/open-pr/settings/guard.md`.

## Deny commands

- `^(docker compose|docker-compose)\b.*\sdown\b.*\s(-[a-zA-Z]*v[a-zA-Z]*|--volumes)\b` — `docker compose down -v`
  wipes `leasebook-pgdata`, with the seeded organizations, the payment-fixture databases and the e2e
  template. Stop the stack with `./scripts/dev.ps1 down`, which keeps the volume.
- `^docker (volume (rm|prune)|system prune\b.*\s--volumes)\b` — removing or pruning Docker volumes
  wipes `leasebook-pgdata`. Stop the stack with `./scripts/dev.ps1 down`, which keeps the volume.
- `^((npx|bunx|npm exec|pnpm (dlx|exec))( -y| --yes)? )?skills(@\S+)? update\b` — `npx skills update`
  leaves `.claude/skills/` linked to its source instead of copied. Re-run the `npx skills add`
  commands in `AGENTS.md`, then `node scripts/sync-agents.mjs`.
- `^az deployment\b` — `az deployment` (`what-if` and `create`) is operator-gated and never run
  under the authoring role; hand it to the operator through `docs/agents/human-follow-up.md`.
  `az bicep build` validates the template locally.
- `^az postgres flexible-server restore\b` — a PITR restore is operator-gated and never run under
  the authoring role; hand it to the operator through `docs/agents/human-follow-up.md`.

## Ask commands

- `(^dev|(^|[\s\\/])dev\.(ps1|sh))\s+reset-db\b` — `reset-db` wipes `leasebook-pgdata`, with the seeded
  organizations, the payment-fixture databases and the e2e template. A PostgreSQL major or a fixture
  refresh documents it; run it only on the user's word.
- `(^payment-fixture|(^|[\s\\/])payment-fixture\.ps1)\s+reset(\s|$)` — discards a payment-fixture database and its
  manifest; follow `docs/runbooks/payment-simulation.md` and run it only on the user's word.
- `(^|[\s\\/])stripe-sandbox-run\.mjs\s+discard(\s|$)` — drops a captured Stripe sandbox run; run
  it only on the user's word.

## Protected paths

- `^private/` — the companion, an independent repository.
- `(^|/)\.env(\.(?!example$)[^/]+)?$` — local secrets, not regenerable from the repository; only
  `.env.example` is tracked.
- `^[^/]+\.local(/|$)` — the root `*.local` files bind a development host to its payment-fixture
  database, and `stripe-probe.local/` holds captured Stripe sandbox runs.
- `^web/node_modules/` — `npm run docs:check` and `npm run api:generate` run from it.
- `^web/codegen/node_modules/` — `npm run api:generate` runs from it.
- `^\.claude/settings\.local\.json$` — the contributor's own Claude Code settings.
