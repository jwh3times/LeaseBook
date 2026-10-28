# Handoffs

## Map key

LeaseBook

## Extra document content

- Machine-local state the other machine must recreate, named and never valued: the payment-fixture
  manifests (`payment-fixture*.local`, from `./scripts/payment-fixture.ps1 init`, per
  `docs/runbooks/payment-simulation.md`), Stripe sandbox runs (`stripe-probe.local/`,
  `node scripts/stripe-sandbox-run.mjs list`), and what the local database holds (seeded
  organizations, migrations applied). Secrets come from 1Password at run time through
  `op run --env-file=./private/config/leasebook.local.env.op`, never from a copied `.env`.
- The companion appears as its state (clean, ahead/behind) and commit hash, and private documents
  by path only — never their contents. A machine without it runs `npm run bootstrap:private`.
