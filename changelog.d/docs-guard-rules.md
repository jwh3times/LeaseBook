### Added

- `docs/agents/guard.md` holds this repository's own rules for the shared guard hook
  (`scripts/guard.mjs`), on top of the rules it applies everywhere. It denies
  `docker compose down -v` and removing or pruning Docker volumes (each wipes `leasebook-pgdata`),
  `npx skills update`, and the operator-gated `az deployment` and
  `az postgres flexible-server restore`. It asks before `reset-db`, a payment-fixture reset and
  discarding a captured Stripe sandbox run, and protects `private/`, the `.env` files, the root
  `*.local` fixture bindings and `stripe-probe.local/`, the `node_modules/` folders the docs and
  API-client checks run from, and `.claude/settings.local.json`. The docs map lists the new file.
  No application behavior changes.
