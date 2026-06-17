# Runbook: Local development environment

## Prerequisites

- **Docker Desktop** running (Postgres runs in a container).
- **.NET 10 SDK** and **Node 26** (`.nvmrc` pins 26; the repo also builds on Node 22.14+).
- **OneDrive hazard:** this repo lives under OneDrive. OneDrive file locks intermittently break
  builds and Docker bind-mounts. Either move the repo outside OneDrive, or exclude `bin/`, `obj/`,
  `node_modules/`, `.vite/`, and `TestResults/` from OneDrive sync. If you see `EBUSY`/locked-file
  errors, this is the cause — do not retry-loop.

## The database and its three roles

Postgres 18 runs via `docker-compose.yml`. On first start, `infra/db/bootstrap.sql` creates the
`leasebook` database and three **purpose-separated** login roles. This separation is what makes
Row-Level Security a real boundary: RLS does not apply to a table's owner by default, so the role
that runs traffic must not be the role that owns the tables (see CLAUDE.md → Multi-tenancy).

| Role | Purpose | RLS | Dev password |
| --- | --- | --- | --- |
| `leasebook_migrator` | Owns the schema; runs migrations only; never serves traffic | owner (bypasses unless FORCE) | `dev_migrator_pw` |
| `leasebook_app` | Runtime DML role used by the app | subject (FORCE ROW LEVEL SECURITY) | `dev_app_pw` |
| `leasebook_ops` | Read-only support / ad-hoc reporting | subject | `dev_ops_pw` |

Superuser `postgres` password (dev): `dev_postgres_pw`. **All dev-only** — real credentials live
in Key Vault.

Default privileges are configured so every table the migrator creates automatically grants DML to
`leasebook_app` and SELECT to `leasebook_ops`. Append-only tables (`audit_events`, `journal_*`)
additionally revoke UPDATE/DELETE in their migration.

## Scripts

Cross-platform helpers in `scripts/` (`dev.ps1` for Windows/pwsh, `dev.sh` for POSIX):

| Command | Effect |
| --- | --- |
| `./scripts/dev.ps1 up` | Start **Postgres only** (inner-loop dev — see "Two ways to run" below) and wait until healthy |
| `./scripts/dev.ps1 down` | Stop the containers, keep the data volume |
| `./scripts/dev.ps1 reset-db` | Wipe the data volume (`down -v`) and re-bootstrap from scratch |
| `./scripts/dev.ps1 psql` | Open `psql` inside the container as the migrator role |
| `./scripts/dev.ps1 app-up` | Build + run the **whole product** in Docker (db → migrate → seed → app on :8080) |
| `./scripts/dev.ps1 app-down` | Stop the full stack, keep the data volume |
| `./scripts/dev.ps1 app-logs` | Follow the app container's logs |

## Two ways to run

There are two distinct local modes, selected by Docker Compose **profile**:

1. **Inner-loop dev (default).** `dev.ps1 up` starts **only Postgres**. You run the API on the host
   (`dotnet run --project src/LeaseBook.Web`, :5080) and the SPA via Vite (`npm run dev`, :5373,
   proxying `/api` → :5080). Fast edit/reload; this is the day-to-day developer loop.

2. **Full product in containers.** `dev.ps1 app-up` builds and runs the entire product — Postgres,
   schema migration, demo seed, and the app (SPA + API) — at **<http://localhost:8082>**. This is for
   demoing or sanity-checking the shipped artifact, not for fast iteration (each change needs a
   rebuild). Sign in with the seeded dev admin (below).

Both modes share the same `docker-compose.yml` and the same data volume; the difference is the
`full` profile. `docker compose up -d` runs db-only; `docker compose --profile full up -d --build`
runs everything.

## Running the full product in Docker

```bash
./scripts/dev.ps1 app-up      # build images, start db→migrate→seed→app, wait for /api/health
# → browse http://localhost:8082, sign in as the seeded admin (see "Migrations and seed")
./scripts/dev.ps1 app-logs    # tail the app
./scripts/dev.ps1 app-down    # stop (keeps data; `reset-db` wipes it)
```

The stack is four services wired by `depends_on` conditions so they start in the only safe order:

| Service | Image / role | What it does |
| --- | --- | --- |
| `db` | `postgres:18` | The database (always on, both profiles). |
| `migrate` | `leasebook-migrator`, **migrator role** | One-shot. Runs an **EF migrations bundle** (built in the Dockerfile `migrator` stage) to bring the schema to the latest migration, then exits 0. |
| `seed` | `leasebook`, **app role** | One-shot. `dotnet LeaseBook.Web.dll seed --org demo` — provisions the demo org + admin + directory + journal. Idempotent, so it runs every `up` and skips when already seeded. Exits 0. |
| `app` | `leasebook`, **app role** | Serves the SPA + `/api` on :8080. |

`migrate` and `seed` are **one-shot** containers: in Docker Desktop they show as **`Exited (0)`** once
the schema is current and the org is seeded — that is success, not a crash. The chain is
`db healthy → migrate done → seed done → app serving`.

Why a separate migrator image rather than migrating at app startup: schema changes must run as
`leasebook_migrator`, never the runtime `leasebook_app` role (that separation is what makes RLS a real
boundary — see Multi-tenancy in `CLAUDE.md`). The chiseled runtime image carries no SDK/`dotnet ef`, so
the `migrator` stage compiles a self-applying migrations bundle that connects as the migrator role.

Notes:

- **Port already in use?** Override with `LEASEBOOK_APP_PORT` (app) or `LEASEBOOK_DB_PORT` (db), e.g.
  `$env:LEASEBOOK_APP_PORT='8090'; ./scripts/dev.ps1 app-up`.
- **Rebuild after code changes:** `app-up` always passes `--build`, so re-running it picks up changes.
- **Auth across restarts:** DataProtection keys are ephemeral in the container, so the auth/antiforgery
  cookies reset when the app container is recreated — just sign in again. (Real environments persist
  keys; not worth the volume-permission friction for a local demo.)
- The full stack is **dev-only** and uses the placeholder passwords from `infra/db/bootstrap.sql`. Real
  environments use Azure Flexible Server + Key Vault + managed identity (WP-10 / `infra/`).

## Connecting as each role

From the host (TCP, password auth — these connection strings land in `appsettings.Development.json`
in WP-04):

```bash
# app role (runtime)
psql "host=localhost port=5432 dbname=leasebook user=leasebook_app password=dev_app_pw"

# migrator role (migrations / design-time)
psql "host=localhost port=5432 dbname=leasebook user=leasebook_migrator password=dev_migrator_pw"
```

Or inside the container (local socket, no password): `./scripts/dev.ps1 psql`.

Verify the app role can connect:

```bash
docker compose exec db psql -U leasebook_app -d leasebook -c "SELECT current_user;"
```

## Migrations and seed

Restore the local tool manifest once (`dotnet tool restore`), then apply migrations as the
**migrator** role (the design-time factory uses `ConnectionStrings:Migrations`):

```bash
dotnet ef database update --project src/LeaseBook.Web
```

Seed the demo org (`Tarheel Property Group`) and its admin — idempotent, safe to re-run:

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"   # loads the dev connection strings
dotnet run --project src/LeaseBook.Web -- seed --org demo
```

The dataset is `seed/demo-org.json` (ported from the prototype; the golden-file fixture from M1
on). The seeder creates the org and the admin user, then materializes the **directory** (M2 —
owners, properties, units, tenants, leases, bank accounts and org settings, reusing the journal's
dimension ids) and **replays the demo journal** (§C.8): provision the chart of accounts, post the
cutover BalanceForward, and post every Feb–Jun event through the real engine. The directory step
runs **before** the journal so every journal-dimension FK (ADR-008) has a target. The derived
ledgers reconcile to the cent against the dataset, now rendered with names (golden tests).
Re-running is idempotent (both steps skip if already seeded).

**Seeded dev admin — DEV ONLY:** `renee.calloway@tarheelpg.test` / `Tarheel-Trust-2026!`. MFA is
not enrolled (enroll on first login). Real environments provision operators by invite; passwords
never live in the repo.

## Checking the accounting invariants

The `check-invariants` verb sweeps the core correctness invariants (I1 entries balance per basis,
I2 the trust equation per trust bank, I3 PM-income isolation, I4 deposit liabilities ≥ 0) and exits
non-zero on any violation. It is the body of the future nightly sweep (P33 / ADR-006).

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
dotnet run --project src/LeaseBook.Web -- check-invariants --all          # every org
dotnet run --project src/LeaseBook.Web -- check-invariants --org demo     # one org (or a GUID)
```

The accounting property suite (`LeaseBook.Tests.Accounting`) runs random valid event sequences
through the real engine. Its iteration count is the `LEASEBOOK_PROPERTY_ITER` environment variable
(default 20); CI and pre-merge runs raise it (e.g. `100`) for deeper coverage:

```powershell
$env:LEASEBOOK_PROPERTY_ITER = "100"; dotnet test tests/LeaseBook.Tests.Accounting/LeaseBook.Tests.Accounting.csproj
```
