# LeaseBook

**Property-management software for small residential operators, built around correct trust accounting.**

LeaseBook is a modular monolith for managing owners, properties, tenants, and the money that flows
between them. Its differentiator is correctness: a true double-entry accounting engine that keeps
landlord (owner) funds, tenant deposits, and management income strictly separated at the data-model
level — the way fiduciary trust accounting for residential property management is supposed to work.

![License: AGPL v3](https://img.shields.io/badge/license-AGPL--3.0-blue.svg)
![.NET](https://img.shields.io/badge/.NET-10-512BD4.svg)
![React](https://img.shields.io/badge/React-19%20%2B%20TypeScript-61DAFB.svg)
![PostgreSQL](https://img.shields.io/badge/PostgreSQL-18-336791.svg)

> **Status: pre-release, under active development.** The foundations, trust-accounting engine, directory,
> and the tenant ledger action hub are implemented and tested; banking/reconciliation, statements,
> bulk operations, and the migration toolkit are on the roadmap. Not yet deployed for production use.

---

## Why it exists

Most property-management tools treat trust accounting as a reporting concern — a filter applied after
the fact. That is how owner money and management income get commingled, how a security deposit gets
recognized as income before it should, and how a month's books stop tying out. LeaseBook takes the
opposite approach: **correctness is structural, not cosmetic.**

- **Owner income is isolated from management income at the data-model level**, never by a report filter.
  No management-fee entry is reachable by an owner-statement query.
- **Deposits and prepayments are liabilities until applied.** Income is recognized only on application —
  identically in cash and accrual modes.
- **Ledgers are append-only.** Corrections are linked reversal entries; posted rows are never updated or
  deleted (the runtime database role has no `UPDATE`/`DELETE` grant on the journal). Every entry carries
  an actor and a full audit trail.
- **The trust equation holds continuously**: for every trust bank account,
  `book balance = Σ owner equity + Σ deposit liabilities + held management fees`. It is tested on every
  posting and swept by an invariant checker.
- **Money is `decimal` / `NUMERIC(14,2)` end to end** — never floating point.

These rules are enforced by the database, the posting engine, and a property-based + golden-file test
suite — not by convention. See [`docs/accounting.md`](docs/accounting.md) for the plain-English model.

---

## What's implemented

| Area | Capability |
| --- | --- |
| **Foundations** | Email/password auth with TOTP MFA, role-based authorization, Postgres row-level security as the tenancy boundary, an append-only audit log, a ported design system, and CI. |
| **Trust accounting engine** | Double-entry journal with dual-basis (cash/accrual) posting templates per business event, a single write path, linked void/reversal, accounting periods, and a continuously-tested invariant suite. |
| **Directory** | Owners, properties, units, tenants, and lite leases — lists, detail pages, full-text search, a ⌘K command palette, and a live dashboard with all-owner ending balances. |
| **Tenant ledger action hub** | Record a payment or charge in place (≤ 3 interactions), collect/hold/apply deposits and prepayments, void with a linked reversal and a per-entry audit drawer, and a filterable, CSV-exportable running-balance ledger. |

On the roadmap: banking register & reconciliation, owner statements (PDF/CSV/email), bulk operations
(rent runs, late fees, disbursements), an import-first migration toolkit, and a compliance/hardening
pass — followed by online payments, owner/tenant portals, and lease/maintenance workflows.

---

## Architecture

LeaseBook is a **modular monolith**: one ASP.NET Core host composes a set of independent module
projects. Modules reference only a shared kernel; cross-module reads go through consumer-owned ports
implemented by thin host adapters, so each module stays extractable.

```text
ASP.NET Core host (LeaseBook.Web)
├─ Modules.Accounting    journal, accounts, posting templates, periods  — the core
├─ Modules.Directory     orgs, owners, properties, units, tenants, leases-lite
├─ Modules.Banking       register, import, reconciliation               (roadmap)
├─ Modules.Reporting     statement engine, report catalog, PDF/CSV      (roadmap)
├─ Modules.Operations    bulk runs: rent, late fees, disbursements      (roadmap)
├─ Modules.Payments      Stripe Connect, webhooks                       (roadmap)
├─ Modules.Migrator      data-import toolkit                            (roadmap)
└─ SharedKernel          Money, ids, CQRS spine, tenancy, result types
```

Key design decisions (each recorded as an ADR in [`docs/adr/`](docs/adr)):

- **Double-entry journal as the source of truth.** Tenant ledgers, owner ledgers, bank registers, and
  statements are all read-model projections of the journal — never independently maintained state.
  Business events (`RentCharged`, `PaymentReceived`, `DepositApplied`, …) post through balanced,
  per-basis templates so each accounting basis is a *query*, not a transformation.
- **PostgreSQL row-level security is the security boundary.** Every org-scoped table carries an `org_id`
  column with a `FORCE ROW LEVEL SECURITY` policy; org context is set per-transaction with
  `SET LOCAL app.org_id`. EF Core global query filters are ergonomics layered on top, not the boundary.
  Three database roles separate schema ownership, runtime DML, and read-only access.
- **CQRS with vertical slices.** Commands and queries dispatch through a small hand-rolled `ISender`
  with a validation/telemetry decorator pipeline (no MediatR/AutoMapper). Endpoints are minimal APIs
  only — bind → dispatch → `TypedResults`.
- **Generated, type-safe API client.** The SPA's TypeScript client is generated from the host's OpenAPI
  document, so the frontend and backend contracts cannot silently drift — a CI gate regenerates the
  client from a build-time copy of the contract and fails if the committed client is stale (ADR-012).

---

## Tech stack

| Layer | Technology |
| --- | --- |
| Backend | C# / .NET 10, ASP.NET Core minimal APIs, EF Core + Npgsql |
| Database | PostgreSQL 18 (row-level security, `NUMERIC` money) |
| Frontend | React 19 + TypeScript, Vite, TanStack Query, generated OpenAPI client |
| Validation | FluentValidation (one validator per slice) |
| CSV / PDF | CsvHelper (in use) · QuestPDF (planned, for statements) |
| Telemetry | OpenTelemetry |
| Testing | xUnit v3, Shouldly, Testcontainers, CsCheck (property-based), Playwright (e2e) |
| Infra / CI | Docker, Azure Container Apps + Bicep (`infra/`), GitHub Actions |

---

## Repository layout

```text
.
├─ src/                     backend: host + module projects + shared kernel
├─ web/                     React + TypeScript SPA (Vite); e2e specs in web/e2e
├─ tests/                   xUnit test projects (accounting, integration, architecture, shared kernel)
├─ infra/                   Bicep modules and environment parameters
├─ docs/                    ADRs (docs/adr), the accounting model, and runbooks
├─ scripts/                 local dev helpers (dev.ps1)
├─ seed/                    demo seed assets
├─ Dockerfile              production image (serves the API and built SPA on one port)
├─ docker-compose.yml      local Postgres + optional full-product profile
└─ LeaseBook.slnx          the solution
```

> A local-only `private/` directory (product planning and design sources) is intentionally **gitignored**
> and absent from public clones; nothing in it is required to build, run, or test the application.

---

## Getting started

### Prerequisites

- **.NET 10 SDK** (`global.json` pins `10.0.100`)
- **Node.js 26** (see `web/.nvmrc`)
- **Docker** — for local PostgreSQL and the Testcontainers-based integration tests

### Run it locally

The fastest path to a running product is the full Docker flow (database → migrate → seed demo data → app):

```bash
./scripts/dev.ps1 app-up      # builds and runs everything → http://localhost:8082
```

Sign in to the seeded demo organization with the development credentials printed by the script. When
you're done: `./scripts/dev.ps1 app-down`.

### Develop against the codebase

Run the backend and frontend separately for a fast inner loop:

```bash
# 1. Start local Postgres (creates the database roles via bootstrap.sql)
./scripts/dev.ps1 up

# 2. Apply migrations and seed the demo organization
dotnet tool restore
dotnet ef database update --project src/LeaseBook.Web
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/LeaseBook.Web -- seed --org demo

# 3. Run the API (http://localhost:5080)
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/LeaseBook.Web

# 4. Run the SPA (http://localhost:5373, proxies /api → :5080)
cd web && npm install && npm run dev
```

See [`docs/runbooks/local-dev.md`](docs/runbooks/local-dev.md) for the full local-development guide.

### Port map

Every port the project binds, and where each is configured. The inner-loop API and the containerized
app deliberately use **different** ports (`5080` vs `8082`) so both can run side by side. Host ports
(`5373`/`8082`/`5632`/`5250`) are offset from the defaults so the stack coexists with other local
projects. Only host-side ports move; container ports (and the Azure image) never change.

**Inner-loop development** — backend and frontend on the host, Postgres in Docker (`./scripts/dev.ps1 up`):

| Port | Service | Configured in |
| --- | --- | --- |
| `5373` | Vite dev server (SPA); proxies `/api` → `:5080` | `web/vite.config.ts` · `web/playwright.config.ts` |
| `5080` | .NET API / host (`dotnet run`) | `src/LeaseBook.Web/Properties/launchSettings.json` (`http` profile) · `src/LeaseBook.Web/appsettings.Development.json` · proxied from `web/vite.config.ts` · `web/playwright.config.ts` |
| `5632` | PostgreSQL (Docker, published to the host) | `docker-compose.yml` (`db`) · connection strings in `appsettings.Development.json` |
| `5250` | pgAdmin (Docker) | `docker-compose.yml` (`pgadmin`) |

**Full Docker stack** — the whole product in containers (`./scripts/dev.ps1 app-up`, Compose `full` profile):

| Port | Service | Configured in |
| --- | --- | --- |
| `8082` | App container (host port) — SPA + `/api` → `http://localhost:8082`; container listens on `8080` | `Dockerfile` (`ASPNETCORE_HTTP_PORTS` / `EXPOSE` `8080`) · `docker-compose.yml` (`app`) |
| `5632` | PostgreSQL container (internal `db:5432`, published to the host) | `docker-compose.yml` (`db`) |
| `5250` | pgAdmin (Docker) | `docker-compose.yml` (`pgadmin`) |

**Production** — Azure Container Apps (`infra/`):

| Port | Service | Configured in |
| --- | --- | --- |
| `8080` | Container ingress `targetPort` (same image as the full stack) | `infra/modules/containerapp.bicep` · `Dockerfile` |

**Host-port overrides** (the container's internal port is unchanged; only the host mapping moves):

| Variable | Default | Remaps |
| --- | --- | --- |
| `LEASEBOOK_APP_PORT` | `8082` | host port for the full-stack app container (container stays `8080`) |
| `LEASEBOOK_DB_PORT` | `5632` | host port for the Postgres container (container stays `5432`) |
| `LEASEBOOK_PGADMIN_PORT` | `5250` | host port for pgAdmin |

---

## Common commands

**Backend** (from the repo root):

```bash
dotnet build LeaseBook.slnx -c Debug          # build (nullable + warnings-as-errors)
dotnet test LeaseBook.slnx                     # all tests (Docker must be running)
dotnet format --verify-no-changes --exclude src/LeaseBook.Web/Migrations   # format / CI gate
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/LeaseBook.Web -- check-invariants --org demo
```

**Database** (`dotnet tool restore` once for `dotnet-ef`):

```bash
./scripts/dev.ps1 up | down | reset-db | psql
dotnet ef migrations add <Name> --project src/LeaseBook.Web
dotnet ef database update --project src/LeaseBook.Web
```

**Web** (from `web/`):

```bash
npm run dev | lint | typecheck | test | build
npm run api:generate   # regenerate the TS client from the running host's OpenAPI doc
npm run e2e            # Playwright end-to-end specs
```

---

## Testing

Correctness is the product, so the accounting module carries the highest test rigor in the codebase:

- **Invariant tests** assert the trust equation, per-basis balance, deposit-liability non-negativity, and
  management-income isolation on engine-produced data.
- **Property-based tests** (CsCheck) replay random valid event sequences and check the invariants hold.
- **Golden-file tests** replay a fixed demo dataset and assert owner balances, tenant ledgers, and bank
  balances to the cent.
- **Integration tests** (Testcontainers + real migrations) exercise the HTTP surface as the RLS-subject
  application role — never bypassing tenancy — including a cross-org isolation pack.
- **Architecture tests** enforce the module boundaries.
- **End-to-end tests** (Playwright) cover the budgeted user flows against a seeded host.

Docker must be running for the integration and accounting suites (they spin up real PostgreSQL
containers).

---

## Documentation

- [`docs/accounting.md`](docs/accounting.md) — the trust-accounting model in plain English
- [`docs/adr/`](docs/adr) — architecture decision records
- [`docs/runbooks/`](docs/runbooks) — local development and restore runbooks

---

## Contributing

LeaseBook follows a few firm conventions:

- Nullable reference types and warnings-as-errors are on; `dotnet format` and ESLint/Prettier are CI
  gates.
- The accounting invariants are non-negotiable — changes that touch money must keep the invariant,
  property-based, and golden-file suites green.
- Significant decisions are recorded as short ADRs in `docs/adr/`.

CI (GitHub Actions) builds, runs the full test suite against real PostgreSQL, type-checks and builds the
web app, builds the container image, and scans for secrets on every push and pull request.

---

## Security

If you discover a security vulnerability, please report it privately to the maintainers rather than
opening a public issue. As trust-accounting software, LeaseBook treats cross-tenant isolation and the
append-only ledger guarantees as security-critical; tenancy is enforced by PostgreSQL row-level security
in addition to application-layer checks.

---

## License

Licensed under the **GNU Affero General Public License v3.0** — see [`LICENSE`](LICENSE). Under the
AGPL, if you run a modified version of LeaseBook as a network service, you must make the corresponding
source available to its users.
