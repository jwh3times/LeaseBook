# Design: `azure-infrastructure` specialist agent (M8.0)

**Date:** 2026-06-26
**Milestone:** M8.0 — Prerequisites (the only task in M8.0)
**Status:** Approved, ready for implementation plan

## Goal

Author `.claude/agents/azure-infrastructure.md`: the specialist agent that M8's
deployment and infrastructure work (M8.3 cutover, the PITR drill, OIDC/Key
Vault/managed-identity wiring) will invoke. It encodes what already exists in
`infra/`, the deploy workflows, and the runbooks so later M8 work stays
consistent with the authored conventions.

This is a documentation/tooling task. It writes **no** Bicep, no workflows, and
no application code — it only creates the agent definition (plus two small
registration edits, see §5).

## Non-goals

- No changes to `infra/` Bicep, the deploy workflows, or runbooks.
- No live Azure work (deploy, what-if, role bootstrap, PITR) — all of that is
  operator-gated and out of scope for M8.0.
- No new ADR (the agent encodes existing decisions; it does not make new ones).

## Verified ground truth (read before writing)

Sources the agent is authored from, all read and confirmed on
`m8/azure-infrastructure-agent`:

- `infra/main.bicep` — subscription-scoped entry point. Params `env`
  (`@allowed(['dev','prod'])`), `location` (`eastus2`), `postgresAdminLogin`,
  `postgresAdminPassword` (`@secure`). Creates RG `lb-<env>-rg`, wires modules
  monitoring → registry → storage → database → vault → app. Outputs
  resourceGroup / acrLoginServer / keyVaultName / appFqdn.
- `infra/modules/*.bicep` — monitoring (Log Analytics PerGB2018 30d + App
  Insights), registry (ACR Basic, `lb<env>acr`, `adminUserEnabled:false`),
  storage (StorageV2 `lb<env>storage<hash>`, TLS1_2, no public blob; containers
  `statements` + `documents`), database (PG Flexible Server 18; dev Burstable
  B1ms + public/firewall-gated, prod GeneralPurpose D2ds_v5 + ZoneRedundant HA +
  35d PITR + public access disabled; roles **not** created here), vault (Key
  Vault RBAC mode, soft-delete 90d), containerapp (user-assigned identity +
  AcrPull + Key Vault Secrets User role assignments, CAE → Log Analytics, app
  ingress :8080, scale min/max by env).
- `infra/env/{dev,prod}.bicepparam` — admin password via
  `readEnvironmentVariable('LEASEBOOK_PG_ADMIN_PASSWORD', '')`, never committed.
- `infra/README.md` — layout, naming convention, validate/deploy commands,
  secrets contract, prod-networking note (prod must wire delegated subnet +
  private DNS zone; dev stays public/firewall-gated).
- `infra/db/azure-bootstrap.md` — the operator creates the three Postgres roles
  after provisioning (Bicep can't); Entra/managed-identity DB auth is the future
  end-state and gets an ADR when it lands.
- `docs/runbooks/restore.md` — PITR procedure skeleton; run the invariant suite
  before cutover; first drill records real timings.
- `.github/workflows/deploy-dev.yml` / `deploy-prod.yml` — OIDC login
  (`azure/login@v3`, `id-token: write`), migrations as the migrator role from a
  one-shot job, Container App revision update. dev runs after CI on `main`
  (`workflow_run`) + dispatch; prod is manual dispatch with an `image_tag` input
  and a `prod` environment required-reviewers gate.

**Discrepancy noted:** the TODO §M8.0 prose calls this a "three-environment
model," but the built reality is **two** (`dev` + `prod`); M0.4 records "Staging
deferred." The agent encodes the real dev+prod model and names staging as
deferred — it does not invent a staging tier.

## Deliverable

`.claude/agents/azure-infrastructure.md`, in the house style of
`.claude/agents/postgres-specialist.md` (frontmatter + tables + banned-patterns
+ checklist, file-cited to the real `infra/` sources).

### 1. Frontmatter

```yaml
---
name: azure-infrastructure
description: Specialist for LeaseBook Azure infrastructure — Bicep authoring/validation,
  the dev+prod environment model, managed identity + RBAC, the Key Vault secrets
  contract, the OIDC deploy workflows, Postgres role bootstrap, and PITR/restore.
  Use before any infra, Bicep, or deployment-wiring work in M8. Authoring is in scope;
  live Azure deploy is operator-gated.
model: opus
tools: Read, Grep, Glob, Bash, Edit, Write
---
```

### 2. Body sections (each cites the real files)

1. **Operator-gated boundary** (the framing that governs everything). The agent
   *authors and validates* — edits Bicep / workflows / runbooks and runs
   `az bicep build` (not gated). It **never** runs live `az deployment create`,
   `az deployment ... what-if`, the Postgres role bootstrap, or PITR commands —
   those need operator Azure access and are out of scope. State this first.
2. **Environment model** — a dev-vs-prod table (sku/tier, storage, backup
   retention, geo-redundant backup, HA mode, network access, replica scale),
   sourced from `database.bicep` + `containerapp.bicep`. Staging = deferred.
   dev = auto-deploy-from-main + public/firewall-gated + scale-to-zero;
   prod = manual-approval + private-networking-TODO + ZoneRedundant + 35d PITR +
   min 1 replica.
3. **Naming convention** — `lb-<env>-<resource>` (hyphen-friendly: rg, pg, kv,
   cae, app, id, ai, logs) vs the global, hyphen-averse `lb<env>acr` and
   `lb<env>storage<hash>`.
4. **Module map + wiring** — one line per module, plus how `main.bicep` wires
   them at subscription scope (RG then modules) and its outputs.
5. **Managed identity + RBAC pattern** — user-assigned identity; the AcrPull and
   Key Vault Secrets User role-assignment idiom (well-known role-definition
   GUIDs, `guid(scope, identity, roleId)` names, `principalType:
   'ServicePrincipal'`); ACR `adminUserEnabled:false`; Key Vault RBAC mode
   (`enableRbacAuthorization:true`).
6. **Secrets contract** — the three env vars (`ConnectionStrings__Default` app
   role, `ConnectionStrings__Migrations` migrator role / deploy job only,
   `APPLICATIONINSIGHTS_CONNECTION_STRING`); real passwords live in Key Vault
   only; `infra/db/bootstrap.sql` passwords are dev-only.
7. **Postgres role bootstrap** — operator step (Bicep can't create roles); the
   three roles (`leasebook_migrator` / `leasebook_app` / `leasebook_ops`) and
   grants from `azure-bootstrap.md`; Entra-auth end-state → ADR when it lands.
8. **Deploy workflows (OIDC)** — deploy-dev (post-CI `workflow_run` on `main` +
   dispatch; build→push image by git SHA→migrate→update revision) vs deploy-prod
   (manual dispatch, `image_tag` input, `prod` required-reviewers gate); the
   secrets (`AZURE_CLIENT_ID` / `AZURE_TENANT_ID` / `AZURE_SUBSCRIPTION_ID` /
   `MIGRATIONS_CONNECTION_STRING`) and vars (`ACR_NAME` / `APP_NAME` /
   `RESOURCE_GROUP`); migrations run as the migrator role from a one-shot job,
   never at app startup.
9. **PITR / restore** — the `restore.md` procedure (restore to a new server from
   a UTC timestamp, verify, cut over, decommission); the invariant rule: a
   restore that doesn't reconcile to the cent is not a successful restore.
10. **Banned patterns** table — committing secrets/passwords; enabling the ACR
    admin user; leaving prod DB publicly reachable; running migrations at app
    startup; running live deploy/what-if/PITR from the agent; deviating from the
    naming convention; giving a `@secure` param a committed default.
11. **Authoring checklist** — `az bicep build` clean; what-if/deploy is
    operator-only; new resources follow the naming convention; a new secret is
    added to Key Vault **and** the README secrets-contract table; cross-refs
    (README port map / secrets contract, runbooks) kept in sync; any deviation
    from a TODO §1 default or the Entra-auth switch gets an ADR.

## Registration edits (agreed)

After the agent file is written:

1. **CLAUDE.md** — add a row to the "Specialist agents" table mapping
   *Azure infrastructure / Bicep / deploy workflows / Key Vault / managed
   identity / PITR* → `azure-infrastructure`, and a line to the "invoke the
   specialist agent for the domain" bullet under Working conventions.
2. **private/TODO.md** — check the M8.0 box (`- [x] **Create the
   azure-infrastructure Claude agent** …`).

## Acceptance criteria

- `.claude/agents/azure-infrastructure.md` exists with valid frontmatter (name,
  model, tools, description) matching the existing-agent format.
- Every body section is faithful to the cited source files (no invented
  resources, SKUs, role GUIDs, env var names, or a staging tier).
- The operator-gated boundary is stated explicitly and up front.
- CLAUDE.md specialist table and Working-conventions bullet list the new agent.
- The M8.0 checkbox in `private/TODO.md` is checked.
- `docs-updater` (Stop hook) finds no drift introduced by this change.

## Out of scope / deferred

The rest of M8 (M8.1 compliance/legal, M8.2 product hardening, M8.3 beta
cutover) — and all live Azure deployment — are separate tasks gated on operator
Azure access. M8.0 only produces the agent.
