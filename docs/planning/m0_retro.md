# Milestone 0 — Retrospective

**Status:** all 11 work packages complete and committed on `m0/foundations`. Automated Integration
Gate (§D) green. Two gate items remain operator-only: the manual browser E2E (step 6) and
CI-green-on-GitHub → merge to `main` (step 9) — this environment is headless and the agent commits
but never pushes/merges.

## Gate evidence (§D)

| Step | Result |
| --- | --- |
| 1. `reset-db` → healthy Postgres | ✅ |
| 2. migrations apply from blank (as migrator) | ✅ InitialOrgs + AddAuditEvents + AddIdentity |
| 3. `seed --org demo` ×2 idempotent | ✅ 1 org / 1 user / 1 provisioning audit row after two runs |
| 4. `dotnet build` + full `dotnet test` | ✅ 27 (Accounting 1, SharedKernel 4, Architecture 6, Integration 16) incl. schema guard + 5 isolation tests |
| 5. web `ci/lint/typecheck/test/build` | ✅ lint 0, typecheck 0, test 24, build OK |
| 6. manual browser E2E (login→MFA→shell→routes→logout) | ⏳ operator (headless env); covered automatically by the auth integration tests against the real host |
| 7. flip dev from MSW → real API | ✅ by design — dev already calls the real API via the Vite proxy; MSW is the test double only |
| 8. `docker build` → run → SPA + `/api/health` | ✅ container serves `/api/health` 200 and `/` (SPA index.html) against compose Postgres |
| 9. CI green → merge to main | ⏳ operator (push + GitHub Actions + merge) |
| 10. CLAUDE.md Commands match reality | ✅ rewritten from the actual commands |
| 11. Status Ledger + retro | ✅ this document |

Totals: **51 automated tests green** (27 .NET + 24 web), build 0/0, web lint/typecheck clean,
`az bicep build` clean, container verified.

## Deviations from the plan (and why)

- **P3 Postgres 17 → 18** (operator-directed, early): PG18 is GA on Azure Flexible Server. Applied
  everywhere (compose, Testcontainers, `SetPostgresVersion`, Bicep, CI service, runbook).
- **RLS policy hardened to `NULLIF(current_setting('app.org_id', true), '')::uuid`** (WP-05): the
  plan's literal `…::uuid` threw `22P02` once a placeholder GUC had been `SET LOCAL` and reverted to
  `''` (empty string, not NULL) post-commit. The T3/T4 isolation tests caught it. §C RLS spec updated.
- **Identity table names mapped explicitly to snake_case** (WP-06): EFCore.NamingConventions
  rewrites Identity columns/keys but not table names. Added an identity-class exemption set to the
  schema guard (`asp_net_users` has `org_id` but must not be RLS-protected — pitfall E6).
- **MFA test computes its own RFC 6238 code** (WP-06): the authenticator token provider cannot
  *generate* codes (only validate), so the test plays the authenticator app over the base32 secret.
- **Antiforgery tokens rebind on auth-state change**: the SPA / tests re-fetch `/api/auth/csrf`
  after login and logout. Documented; the test client and login page do this.
- **Dev points at the real API; MSW is tests-only** (WP-08): the WP-06 backend is already merged, so
  a browser MSW worker would be redundant. Gate step 7 ("flip to real API") is therefore a no-op.
- **`openapi-fetch` uses a deferred `fetch` wrapper** so MSW (which patches `globalThis.fetch` after
  module import) intercepts in tests; baseUrl is `window.location.origin` (undici rejects relative URLs).
- **In-memory `localStorage` polyfill in the web test setup**: this jsdom build doesn't provide it.
- **A diagnostics `/api/diagnostics/audit-count` endpoint** is the WP-05 + WP-06 tie surface (auth
  claim → org-context middleware → RLS). It is an M0 plumbing probe — **M1 should remove/replace it**
  with real read endpoints.
- **The seeder writes one provisioning `audit_events` row** through `OrgScopedExecutor` — the only
  org-scoped write that exists in M0 (no business entities yet), which proves the executor + audit +
  RLS path and gives the seeder test something to assert.
- **Deploy workflows + Bicep `what-if`/deploy deferred** on the operator Azure-access gate (no
  `az login` / subscription here). Workflows fail clearly at `azure/login`.

## ADRs

ADR-000…005 were recorded in WP-01 (record-decisions, Hangfire, Redis-deferred, portal sub-org
scoping, single DbContext, CQRS-via-owned-dispatcher). No new ADRs were required in M0. Candidates
to record when their work lands:

- **DB auth via Microsoft Entra managed identity** for `leasebook_app` (noted in
  `infra/db/azure-bootstrap.md`) — replaces stored password roles. Record when Bicep deploys.
- The **NULLIF RLS predicate** is now the canonical helper output; no ADR needed (it's a correctness
  fix, not a default deviation), but M1 authors should know *why* the NULLIF is there.

## What M1 planning must absorb

1. **Audit auto-write fires for real in M1.** The SaveChanges audit pass produces nothing in M0
   (no `IOrgScoped` business entities besides `audit_events`, which is excluded). The first journal
   writes exercise it — add the "writing an aggregate emits an audit_events row" integration test then.
2. **Append-only on `journal_entries` / `journal_lines`** uses the same `RevokeAppendOnly` helper;
   they join the schema guard's expectations automatically (org-scoped → RLS + policy).
3. **Identity tables are the soft spot of the RLS boundary.** `asp_net_users` carries an `org_id`
   but is deliberately exempt from RLS (login runs before org context exists — pitfall E6), so org
   isolation for user rows rests entirely on app logic. Any endpoint that reads or manages users
   (user listing, invitations, role assignment) must filter by org explicitly **and** ship its own
   cross-org isolation test — the T1–T5 pack and the schema guard will not catch a mistake here.
4. **Golden-file tests** validate the accounting engine against `seed/demo-org.json`; the dataset
   beyond org + admin (banks, owners, ledger, statement) gets seeded as those schemas land. Don't
   edit the figures casually — they reconcile to the cent.
5. **Remove the diagnostics audit-count probe** once real org-scoped read endpoints exist.
6. **Telemetry**: the CQRS `cqrs.<MessageName>` spans + Azure Monitor exporter are wired; M1 hangs
   money-path custom events off the same `LeaseBook` ActivitySource.
7. **Playwright e2e** is scaffolded (no specs); add specs for the budgeted UX flows as they ship.
8. **Operator follow-ups**: configure Azure OIDC / ACR / Container App + Postgres role bootstrap to
   enable the deploy workflows; run the first PITR drill to fill in `docs/runbooks/restore.md`.
