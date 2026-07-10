# Architecture Blueprint

- **Audience:** Contributors and maintainers
- **Status:** Historical architecture baseline
- **Owner:** Maintainers
- **Last reviewed:** 2026-07-09
- **Provenance:** this is the public engineering baseline locked before M0 code was written. It
  carries no pricing, strategy, customer detail, or private delivery sequencing.
- **Authority:** decisions marked **PRD-locked** were fixed by the product requirements document;
  the rest are defaults decided here. **Changing any default requires an ADR**
  ([`docs/adr/`](adr/README.md)) in the same pull request — several defaults have since been
  decided or refined that way and are annotated with their ADR below. Accepted ADRs and the living
  [`architecture.md`](architecture.md) supersede this baseline where the implemented system evolved.
- The binding day-to-day rules (invariants, module boundary, working conventions) live in
  [`AGENTS.md`](../AGENTS.md); [`architecture.md`](architecture.md) is the narrative overview of
  the same system, and [`ROADMAP.md`](ROADMAP.md) summarizes public product direction.

## Technology defaults

| Concern             | Decision                                                                                                                                                                                                                                                                                                                                                                                                                                                                                            | Status                      |
| ------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------- |
| Backend             | C# / .NET (current LTS), ASP.NET Core, modular monolith                                                                                                                                                                                                                                                                                                                                                                                                                                             | PRD-locked                  |
| Frontend            | React + TypeScript, Vite build                                                                                                                                                                                                                                                                                                                                                                                                                                                                      | PRD-locked (Vite = default) |
| Database            | PostgreSQL (Azure Database for PostgreSQL Flexible Server)                                                                                                                                                                                                                                                                                                                                                                                                                                          | PRD-locked                  |
| ORM / data access   | EF Core + Npgsql; raw SQL allowed for reporting queries                                                                                                                                                                                                                                                                                                                                                                                                                                             | Default                     |
| Cache / jobs        | Background jobs via a durable scheduler — **Hangfire w/ Postgres storage** (picked in ADR-001; first intended use is the nightly invariant sweep). Redis (Azure Cache) for sessions & rate limiting — **deliberately deferred** until a concrete need appears (ADR-002)                                                                                                                                                                                                                             | Decided (ADR-001, ADR-002)  |
| Hosting             | Azure Container Apps, region East US 2                                                                                                                                                                                                                                                                                                                                                                                                                                                              | PRD-locked                  |
| IaC                 | Bicep (Azure-native, solo-friendly)                                                                                                                                                                                                                                                                                                                                                                                                                                                                 | Default                     |
| CI/CD               | GitHub Actions → ACR → Container Apps                                                                                                                                                                                                                                                                                                                                                                                                                                                               | Default                     |
| AuthN               | ASP.NET Core Identity (email+password, TOTP MFA) issuing cookie for the SPA; evaluate Entra External ID only if social/B2B login becomes a requirement                                                                                                                                                                                                                                                                                                                                              | Default                     |
| AuthZ               | Role-based: `PMAdmin`, `PMStaff`, `Owner` (read-only portal, Phase 2–3), `Tenant` (portal, Phase 2–3) — PRD §7.3                                                                                                                                                                                                                                                                                                                                                                                    | PRD-locked                  |
| Multi-tenancy       | Single DB, `org_id` (the PM company) on every tenant-scoped row; EF query filters + Postgres Row-Level Security as two independent enforcement layers — full design in "Multi-tenancy & RLS" below                                                                                                                                                                                                                                                                                                  | Default                     |
| IDs                 | UUIDv7 primary keys (sortable, index-friendly)                                                                                                                                                                                                                                                                                                                                                                                                                                                      | Default                     |
| Money               | `decimal` in C#, `NUMERIC(14,2)` in Postgres. **Never float/double.** Single currency (USD) but keep amounts in a `Money` value type for discipline                                                                                                                                                                                                                                                                                                                                                 | Default                     |
| Accounting engine   | True double-entry journal: `journal_entries` (header) + `journal_lines` (account, debit/credit, dimensions). Dual-basis posting: business events emit lines tagged `cash` / `accrual` / `both` so each basis is a _query_, not a transformation                                                                                                                                                                                                                                                     | Default                     |
| API style           | REST via **minimal APIs** (route groups, `TypedResults`, endpoint filters — no MVC controllers); OpenAPI spec drives the generated TypeScript client for the SPA (drift-gated in CI, ADR-012)                                                                                                                                                                                                                                                                                                       | Default                     |
| Application pattern | **CQRS with vertical slices** per module: `ICommand<T>`/`IQuery<T>` + handlers, dispatched through a thin hand-rolled `ISender` with a decorator pipeline (validation → telemetry → handler). **No MediatR** (v13+ is commercially licensed) and no AutoMapper (same) — the dispatcher is ~50 lines we own, DTOs are hand-mapped records. Commands mutate only through domain services (the posting engine in M1+); queries read projections/SQL directly and never load aggregates to answer reads | Default (ADR-005)           |
| Validation          | **FluentValidation** (Apache-2.0, still OSS) — one validator per command/query, colocated with its slice, executed in the dispatcher pipeline so HTTP, background jobs, and the seeder share identical validation; failures map to RFC 7807 ProblemDetails. Endpoint filters never duplicate validation — one home only                                                                                                                                                                             | Default                     |
| PDF generation      | QuestPDF (server-side, code-first templates)                                                                                                                                                                                                                                                                                                                                                                                                                                                        | Default                     |
| CSV                 | CsvHelper                                                                                                                                                                                                                                                                                                                                                                                                                                                                                           | Default                     |
| Telemetry           | OpenTelemetry → Azure Application Insights; custom events for click-budget metrics                                                                                                                                                                                                                                                                                                                                                                                                                  | Default                     |
| Secrets             | Azure Key Vault + managed identity; nothing in source or env vars (PRD §7.3)                                                                                                                                                                                                                                                                                                                                                                                                                        | PRD-locked                  |
| Documents/files     | Azure Blob Storage (statements, future lease docs, photos)                                                                                                                                                                                                                                                                                                                                                                                                                                          | Default                     |

## Solution layout (modular monolith)

```text
leasebook/
├─ src/
│  ├─ LeaseBook.Web/              # ASP.NET Core host: API endpoints, auth, composition root
│  ├─ LeaseBook.Modules.Accounting/   # journal, accounts, postings, periods — THE CORE
│  ├─ LeaseBook.Modules.Directory/    # orgs, owners, properties, units, tenants, leases-lite
│  ├─ LeaseBook.Modules.Banking/      # bank accounts, register, import, reconciliation
│  ├─ LeaseBook.Modules.Reporting/    # statement engine, report catalog, PDF/CSV export
│  ├─ LeaseBook.Modules.Operations/   # bulk runs: rent charges, late fees, disbursements
│  ├─ LeaseBook.Modules.Payments/     # Phase 2: Stripe Connect, webhooks, refund checks
│  ├─ LeaseBook.SharedKernel/         # Money, ids, audit, org scoping, result types
│  └─ LeaseBook.Migrator/             # Phase 1–2: AppFolio import toolkit
├─ web/                           # React + TypeScript SPA (Vite)
│  ├─ src/design/                 # tokens + primitives ported from the prototype
│  ├─ src/features/{dashboard,tenants,owners,banking,reports,operations,settings}
│  └─ src/api/                    # generated OpenAPI client
├─ infra/                         # Bicep modules + environment params
├─ tests/
│  ├─ LeaseBook.Tests.Accounting/ # invariant + property-based + golden-file tests
│  ├─ LeaseBook.Tests.Integration/# Testcontainers (Postgres) API tests
│  └─ web e2e (Playwright)
└─ docs/                          # ADRs, runbooks, compliance notes
```

## Multi-tenancy & row-level security (designed before M0 code)

The unit of tenancy is the **org** (one PM company). Trust data is exactly the kind of data
where a cross-tenant leak is an existential bug, not a cosmetic one — so isolation is enforced
in two _independent_ layers: EF query filters for ergonomics, and Postgres RLS as the layer
that still holds when application code is wrong. RLS is the security boundary; EF is
convenience.

- **Every table declares its tenancy class** in its migration:
  - _Org-scoped_ — `org_id NOT NULL` + RLS policy + `FORCE ROW LEVEL SECURITY`. All financial
    and directory data.
  - _Global_ — no `org_id`, no RLS: chart-of-accounts templates, lookup data, system tables.
  - _Identity_ — users belong to exactly one org in v1 (multi-org staff membership deferred;
    revisit if a contractor works for two PM companies — note as ADR).
- **Three DB roles, separated by purpose** (RLS does not apply to table owners by default —
  this separation is what makes RLS real):
  - `leasebook_migrator` — owns the schema, runs migrations only, never serves traffic.
  - `leasebook_app` — runtime DML role, subject to RLS (`FORCE ROW LEVEL SECURITY` covers
    ownership edge cases), and **no UPDATE/DELETE grants** on `journal_entries`,
    `journal_lines`, `audit_events` (append-only enforcement lives here too).
  - `leasebook_ops` — read-only role for support/ad-hoc reporting, also RLS-subject.
- **Session context mechanism:** policies read `current_setting('app.org_id', true)::uuid`.
  The app issues `SET LOCAL app.org_id = ...` _inside the transaction_ via an EF transaction
  interceptor. `SET LOCAL` is transaction-scoped, which is what makes connection pooling safe —
  Npgsql reuses physical connections, and a session-level `SET` would leak org context to the
  next request on that connection. No transaction → no context → `current_setting(..., true)`
  returns NULL → policies match nothing → fail closed (empty reads, rejected writes).
- **Policy shape:** one policy per table, plain equality
  (`org_id = current_setting('app.org_id', true)::uuid`), `FOR ALL` with both `USING` _and_
  `WITH CHECK` — the `WITH CHECK` half is what stops a bugged or compromised code path from
  _inserting/moving_ rows into another org. Keep predicates to bare equality so the planner
  stays index-aligned.
- **Indexing:** composite indexes lead with `org_id` so the RLS predicate rides every query's
  access path; PKs stay plain UUIDv7 (no composite-PK churn through FKs). (PKs did stay plain,
  but the five `journal_lines` dimension FKs were since promoted to composite
  `(org_id, <dim>_id) → (org_id, id)` alternate keys so referential integrity itself cannot
  cross orgs — the ADR-008 revisit, recorded in
  [ADR-013](adr/ADR-013-composite-org-dimension-fks.md).)
- **EF layer (ergonomics, not the boundary):** `IOrgScoped` marker + global query filter bound
  to a request-scoped `ITenantContext`; SaveChanges interceptor stamps `org_id` on new entities
  and throws on mismatch. Raw SQL for reporting runs on the same scoped connection, so RLS
  still applies to it.
- **Background jobs & system paths:** jobs carry `org_id` in their payload; a job wrapper opens
  the transaction and sets context before any DbContext use, and throws if context is missing
  (don't let RLS silently return empty rows to a job that forgot its org). Cross-org work
  (nightly invariant sweep) enumerates orgs via a narrow system path, then processes one
  org-scoped transaction at a time — there is deliberately no "all orgs in one query" path.
- **Portal personas (Phase 2–3):** RLS stays org-level. Owner and tenant portal users need
  _sub-org_ visibility (an owner sees only their properties; a tenant only their own ledger) —
  enforced at the app layer via dedicated portal endpoints + authorization handlers, not by
  stacking more session variables into RLS (four personas × per-table policies multiplies
  complexity for little gain while the portal surface is small). Recorded as
  [ADR-003](adr/ADR-003-portal-suborg-scoping-at-app-layer.md), with the revisit trigger: if
  portal endpoints grow past a handful, reconsider per-persona policies.
- **Org lifecycle:** provisioning (create org → seed chart of accounts → first admin → default
  trust accounts) is one transactional operation; offboarding produces a complete per-org
  export (GLBA + the customer-trust story: "your data leaves with you") and a documented
  hard-delete path with retention rules.
- **Azure note:** Flexible Server's admin login is not a true Postgres superuser — create the
  three roles explicitly in the Bicep/bootstrap script; the app connects as `leasebook_app`
  (managed identity), migrations CI connects as `leasebook_migrator`.

## Trust accounting data model (the heart — designed before M1 code)

- **Chart of accounts per org**, seeded from a template. Account classes:
  - `TrustBank` (asset) — one per trust bank account (Operating Trust, Deposit Trust)
  - `OwnerEquity` (liability to owner) — subledger dimension: owner + property
  - `TenantReceivable` — subledger dimension: tenant/lease
  - `DepositLiability` — subledger: tenant (held deposits & prepayments)
  - `PMIncome` (management fees, late-fee share, tenant-sourced PM income) — **structurally
    excluded from owner reporting: owner-statement queries select by account class, and
    `PMIncome` is not in the allowed set. Enforce with a DB constraint or posting-rule test.**
  - `PMOperatingBank` — the PM's own account (fee sweeps land here)
- **Journal entry** = header (id, org, date, business event type, source document ref, created_by,
  posted_at, `reverses_entry_id` nullable) + lines (account, debit, credit, basis tag,
  dimensions: property, unit, owner, tenant, bank account).
- **Posting templates** map each business event → balanced lines per basis. Initial event catalog:
  `RentCharged`, `FeeCharged (late/maintenance-recharge/other)`, `PaymentReceived`,
  `DepositCollected`, `PrepaymentReceived`, `DepositApplied`, `PrepaymentApplied`,
  `CreditIssued`, `ManagementFeeAssessed`, `OwnerContribution`, `OwnerDisbursed`,
  `VendorPaid`, `RefundIssued`, `EntryVoided`. (The live catalog is recorded in
  [ADR-006](adr/ADR-006-posting-template-catalog.md) and has grown with later ADRs — e.g.
  ADR-020's opening-balance postings; [`accounting.md`](accounting.md) documents the shipped
  event set.)
- **Accounting periods** table per org: open → closed (reconciliation finalize locks it).
  Posting into a locked period is rejected; corrections post into the open period.
- **The trust equation is the master invariant:** for each trust bank account,
  `bank book balance = Σ owner equity + Σ deposit liabilities + Σ undisbursed PM fees held`.
  This is tested continuously (see the correctness harness in `tests/LeaseBook.Tests.Accounting`).
