# azure-infrastructure Specialist Agent (M8.0) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Author `.claude/agents/azure-infrastructure.md` — the specialist agent that M8's deployment/infra work invokes — encoding the existing `infra/` Bicep, deploy workflows, and runbook conventions, then register it.

**Architecture:** A single Markdown agent file in the house style of `.claude/agents/postgres-specialist.md` (YAML frontmatter + tables + banned-patterns + checklist), authored verbatim from already-read source files. No Bicep, workflow, or application code changes. Two small registration edits (CLAUDE.md table + `private/TODO.md` checkbox) make it discoverable and mark M8.0 done.

**Tech Stack:** Markdown + YAML frontmatter (Claude Code agent format). Verification is grep-based faithfulness checks against the source files, not a test runner — the agent file is not compiled or executed.

**Spec:** `docs/superpowers/specs/2026-06-26-azure-infrastructure-agent-design.md` (read it; this plan implements it).

## Global Constraints

- **Faithful only.** Every fact in the agent (SKUs, role GUIDs, env var names, naming, ports, scale numbers) must be copied from the cited source file. Invent nothing — no staging tier, no unverified values. Use the "Verified values" reference below as the source of truth for exact strings.
- **House style.** Match `.claude/agents/postgres-specialist.md`: frontmatter fields `name`, `description`, `model`, `tools`; `model: opus`; `tools: Read, Grep, Glob, Bash, Edit, Write`; body uses `##` sections, tables, fenced code, and a final checklist.
- **Operator-gated boundary stated up front.** The agent authors/validates only; it never runs live `az deployment`, `what-if`, Postgres role bootstrap, or PITR commands.
- **dev + prod only.** Staging is named as *deferred*, never as an existing tier.
- **No infra changes.** Do not edit `infra/`, the workflows, or runbooks. `az bicep build` is irrelevant to this task (the agent file is not Bicep).
- **Commit message footer** (every commit): `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- Branch is already `m8/azure-infrastructure-agent`.

## Verified values (source of truth for exact strings)

Copy these exactly into the agent. Each is cited to its file.

| Fact | Value | Source |
| --- | --- | --- |
| Entry scope / params | `targetScope = 'subscription'`; `env` `@allowed(['dev','prod'])`; `location='eastus2'`; `postgresAdminLogin`; `postgresAdminPassword` `@secure` | `infra/main.bicep` |
| Wiring order | RG `lb-<env>-rg` → monitoring → registry → storage → database → vault → app; outputs resourceGroup/acrLoginServer/keyVaultName/appFqdn | `infra/main.bicep` |
| Naming | `lb-<env>-<resource>` for rg, pg, kv, cae, app, id, ai, logs; global hyphen-averse `lb<env>acr` and `lb<env>storage<hash>` (`take(...,24)`) | `infra/README.md`, modules |
| AcrPull role GUID | `7f951dda-4ed3-4680-a7ca-43fe172d538d` | `infra/modules/containerapp.bicep` |
| Key Vault Secrets User role GUID | `4633458b-17de-408a-b874-0445c86b69e6` | `infra/modules/containerapp.bicep` |
| Role-assignment idiom | `guid(scope.id, identity.id, roleId)` name; `principalType: 'ServicePrincipal'`; `subscriptionResourceId('Microsoft.Authorization/roleDefinitions', roleId)` | `infra/modules/containerapp.bicep` |
| Container App | user-assigned identity; ingress `external: true`, `targetPort: 8080`, `transport: 'auto'`; image `<acr>/leasebook:latest`; cpu `0.5`, memory `1Gi`; secret `appinsights-connection-string` → env `APPLICATIONINSIGHTS_CONNECTION_STRING` | `infra/modules/containerapp.bicep` |
| Scale (dev/prod) | dev `minReplicas:0, maxReplicas:2`; prod `minReplicas:1, maxReplicas:5` | `infra/modules/containerapp.bicep` |
| DB sku | dev `Standard_B1ms`/`Burstable`; prod `Standard_D2ds_v5`/`GeneralPurpose`; version `18`; storage 32 GB | `infra/modules/database.bicep` |
| DB backup/HA/network | backup dev 7d / prod 35d; geoRedundant prod `Enabled`/dev `Disabled`; HA prod `ZoneRedundant`/dev `Disabled`; publicNetworkAccess prod `Disabled`/dev `Enabled`; dev firewall `AllowAzureServices` 0.0.0.0; db name `leasebook` | `infra/modules/database.bicep` |
| ACR | sku `Basic`; `adminUserEnabled: false` | `infra/modules/registry.bicep` |
| Storage | `StorageV2`, `Standard_LRS`, `minimumTlsVersion: 'TLS1_2'`, `allowBlobPublicAccess: false`, https-only; containers `statements`, `documents` | `infra/modules/storage.bicep` |
| Vault | sku `standard`/`A`; `enableRbacAuthorization: true`; `enableSoftDelete: true`; `softDeleteRetentionInDays: 90` | `infra/modules/vault.bicep` |
| Monitoring | Log Analytics `PerGB2018`, `retentionInDays: 30`; App Insights `kind: 'web'`, workspace-based | `infra/modules/monitoring.bicep` |
| bicepparam | `readEnvironmentVariable('LEASEBOOK_PG_ADMIN_PASSWORD', '')` | `infra/env/{dev,prod}.bicepparam` |
| Secrets contract | `ConnectionStrings__Default` (app role), `ConnectionStrings__Migrations` (migrator role / deploy job only), `APPLICATIONINSIGHTS_CONNECTION_STRING` | `infra/README.md` |
| Postgres roles | `leasebook_migrator` / `leasebook_app` / `leasebook_ops`; created by operator post-provision (Bicep can't); Entra-auth end-state → ADR when it lands | `infra/db/azure-bootstrap.md` |
| Deploy workflows | `azure/login@v3`; `permissions: id-token: write`; secrets `AZURE_CLIENT_ID`/`AZURE_TENANT_ID`/`AZURE_SUBSCRIPTION_ID`/`MIGRATIONS_CONNECTION_STRING`; vars `ACR_NAME`/`APP_NAME`/`RESOURCE_GROUP`; image tagged by git SHA; migrations as migrator role from one-shot job | `.github/workflows/deploy-dev.yml`, `deploy-prod.yml` |
| dev vs prod deploy | dev: `workflow_run` after CI on `main` + `workflow_dispatch`. prod: `workflow_dispatch` with `image_tag` input + `prod` environment required-reviewers gate | `.github/workflows/deploy-{dev,prod}.yml` |
| PITR | restore to a **new** server from a UTC timestamp; retention dev 7d / prod 35d; run the invariant suite before cutover (a restore that doesn't reconcile to the cent is not successful) | `docs/runbooks/restore.md` |

## File Structure

- **Create:** `.claude/agents/azure-infrastructure.md` — the agent (sole new file; one clear responsibility: Azure infra/deploy authority).
- **Modify:** `CLAUDE.md` — add one specialist-table row + one Working-conventions clause.
- **Modify:** `private/TODO.md` — check the M8.0 box (gitignored/local-only; edited but not part of the committed diff).

---

### Task 1: Author the `azure-infrastructure` agent file

**Files:**
- Create: `.claude/agents/azure-infrastructure.md`
- Reference (read-only, already read): `.claude/agents/postgres-specialist.md` (style), `infra/**`, `.github/workflows/deploy-*.yml`, `docs/runbooks/restore.md`, the spec.

**Interfaces:**
- Produces: an agent named `azure-infrastructure` (frontmatter `name:` value) that Task 2 references in CLAUDE.md.

- [ ] **Step 1: Write the frontmatter** (verbatim)

```yaml
---
name: azure-infrastructure
description: Specialist for LeaseBook Azure infrastructure — Bicep authoring/validation, the dev+prod environment model, managed identity + RBAC, the Key Vault secrets contract, the OIDC deploy workflows, Postgres role bootstrap, and PITR/restore. Use before any infra, Bicep, or deployment-wiring work in M8. Authoring is in scope; live Azure deploy is operator-gated.
model: opus
tools: Read, Grep, Glob, Bash, Edit, Write
---
```

- [ ] **Step 2: Write the body sections 1–11** per spec §2, using the exact values from the "Verified values" table.

Section order and required content:
1. **Operator-gated boundary** (first, framing): the agent authors/validates (`az bicep build` is fine, not gated) but NEVER runs `az deployment create`, `... what-if`, the Postgres role bootstrap, or PITR — those need operator Azure access.
2. **Environment model**: a dev-vs-prod table (sku/tier, storage, backup retention, geo-redundant backup, HA, network access, replica scale) from the Verified-values rows; one line stating staging is deferred (M0.4), not an existing tier.
3. **Naming convention**: `lb-<env>-<resource>` vs `lb<env>acr` / `lb<env>storage<hash>`.
4. **Module map + wiring**: one line per module + `main.bicep` subscription-scope wiring and outputs.
5. **Managed identity + RBAC**: user-assigned identity; AcrPull + Key Vault Secrets User assignments (both GUIDs, the `guid()`/`ServicePrincipal` idiom); ACR `adminUserEnabled:false`; KV RBAC mode.
6. **Secrets contract**: the three env vars + which consumer; real passwords in Key Vault only; `infra/db/bootstrap.sql` is dev-only.
7. **Postgres role bootstrap**: operator step; the three roles + grants; Entra-auth future → ADR when it lands.
8. **Deploy workflows (OIDC)**: deploy-dev vs deploy-prod triggers/gates; OIDC login + `id-token: write`; the secrets/vars; migrations as migrator role from a one-shot job, never at app startup.
9. **PITR / restore**: the procedure + the "reconcile to the cent or it's not a restore" rule.
10. **Banned patterns** table: committing secrets/passwords · enabling ACR admin user · leaving prod DB publicly reachable · migrations at app startup · running live deploy/what-if/PITR from the agent · deviating from naming · giving a `@secure` param a committed default.
11. **Authoring checklist**: `az bicep build` clean; what-if/deploy operator-only; new resource follows naming; new secret added to Key Vault AND the README secrets-contract table; cross-refs (README port map / secrets contract, runbooks) kept in sync; deviation from a TODO §1 default or the Entra-auth switch gets an ADR.

- [ ] **Step 3: Verify frontmatter format** matches the house style (exactly the four fields, in order).

Run: `rg -n "^(name|description|model|tools):" .claude/agents/azure-infrastructure.md`
Expected: 4 lines, in the order name / description / model / tools.

- [ ] **Step 4: Verify load-bearing values are present and faithful** (each must appear in the agent file).

Run:
```bash
rg -c "7f951dda-4ed3-4680-a7ca-43fe172d538d|4633458b-17de-408a-b874-0445c86b69e6|Standard_B1ms|Standard_D2ds_v5|lb<env>acr|ConnectionStrings__Migrations|id-token|leasebook_migrator|targetPort|8080" .claude/agents/azure-infrastructure.md
```
Expected: a count ≥ 10 (every probe term present). If any term is missing, add the section that should carry it.

- [ ] **Step 5: Verify no invented staging tier**

Run: `rg -n "staging" .claude/agents/azure-infrastructure.md`
Expected: either no matches, or matches only in a "deferred"/"not an existing tier" context. A line presenting staging as a live environment is a failure — fix it.

- [ ] **Step 6: Commit**

```bash
git add .claude/agents/azure-infrastructure.md
git commit -m "feat(m8): azure-infrastructure specialist agent (M8.0)"
```

---

### Task 2: Register the agent (CLAUDE.md + TODO checkbox)

**Files:**
- Modify: `CLAUDE.md` (Specialist agents table + Working-conventions bullet)
- Modify: `private/TODO.md` (M8.0 checkbox)

**Interfaces:**
- Consumes: the `azure-infrastructure` agent name from Task 1.

- [ ] **Step 1: Read the exact current strings** (whitespace must match for Edit)

Run: read `CLAUDE.md` around the "Specialist agents" table and the "Invoke the specialist agent for the domain you're working in" bullet; read `private/TODO.md` line 700 (the M8.0 checkbox).

- [ ] **Step 2: Add the specialist-table row.** Insert after the `code-reviewer` row (before the `docs-updater` row), matching the table's column widths:

```
| Azure infra, Bicep, deploy workflows, Key Vault/managed identity, PITR | `azure-infrastructure`                   |
```

- [ ] **Step 3: Add the Working-conventions clause.** Append to the "Invoke the specialist agent…" bullet, after "For pre-merge review: `code-reviewer`.":

```
For Azure infra/Bicep/deploy wiring: `azure-infrastructure`.
```

- [ ] **Step 4: Check the M8.0 box** in `private/TODO.md`: change `- [ ] **Create the \`azure-infrastructure\` Claude agent**` to `- [x]`.

- [ ] **Step 5: Verify the registration**

Run: `rg -n "azure-infrastructure" CLAUDE.md`
Expected: at least 2 matches (table row + conventions bullet).
Run: `rg -n "\[x\] \*\*Create the \`azure-infrastructure\`" private/TODO.md`
Expected: 1 match (box checked).

- [ ] **Step 6: Commit** (`private/TODO.md` is gitignored, so only `CLAUDE.md` is staged)

```bash
git add CLAUDE.md
git commit -m "docs(m8): register azure-infrastructure agent in CLAUDE.md"
```

---

### Task 3: Faithfulness & drift verification pass

**Files:** none modified unless a fix is needed.

- [ ] **Step 1: Cross-check every claimed value against its source.** For each row in "Verified values", confirm the agent's wording matches the cited file. Spot-check at minimum: the two role GUIDs, the dev/prod SKU pair, the scale numbers, the secrets-contract env var names, the deploy-workflow secrets/vars, and the dev/prod network + backup values. Fix any mismatch in the agent file.

- [ ] **Step 2: Confirm the operator-gated boundary is the first body section** and explicitly lists deploy/what-if/role-bootstrap/PITR as out of scope for the agent.

- [ ] **Step 3: Run the diff through the docs-updater agent** (or rely on the Stop hook, which auto-runs it at session end) to confirm no documentation drift was introduced — e.g., `infra/README.md` / runbooks still consistent with the agent's claims.

Run: review `git diff main...HEAD --stat` — expected files: `.claude/agents/azure-infrastructure.md`, `CLAUDE.md`, and the two `docs/superpowers/` planning files. No `infra/` or workflow files should appear.

- [ ] **Step 4: Fix and commit** only if Steps 1–3 surfaced an issue.

```bash
git add -A
git commit -m "fix(m8): faithfulness fixes for azure-infrastructure agent"
```

---

## Self-Review (completed during planning)

**Spec coverage:** Spec §Deliverable §1 (frontmatter) → Task 1 Step 1. §2 sections 1–11 → Task 1 Step 2 + the Verified-values table. §Registration edits → Task 2. §Acceptance criteria → Tasks 1 Step 3–5, Task 2 Step 5, Task 3. §Discrepancy note (no staging) → Global Constraints + Task 1 Step 5. No gaps.

**Placeholder scan:** No "TBD"/"add appropriate…" placeholders; every value is concrete in the Verified-values table.

**Type consistency:** The agent name `azure-infrastructure` is used identically in Task 1 frontmatter, Task 2 CLAUDE.md edits, and verification greps. Role GUIDs and SKU strings are single-sourced from the Verified-values table.
