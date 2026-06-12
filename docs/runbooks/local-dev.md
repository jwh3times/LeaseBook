# Runbook: Local development environment

## Prerequisites

- **Docker Desktop** running (Postgres runs in a container).
- **.NET 10 SDK** and **Node 24 LTS** (`.nvmrc` pins 24; the repo also builds on Node 22.14+).
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
| `./scripts/dev.ps1 up` | Start Postgres and wait until the container reports healthy |
| `./scripts/dev.ps1 down` | Stop the container, keep the data volume |
| `./scripts/dev.ps1 reset-db` | Wipe the data volume (`down -v`) and re-bootstrap from scratch |
| `./scripts/dev.ps1 psql` | Open `psql` inside the container as the migrator role |

## Connecting as each role

From the host (TCP, password auth — these connection strings land in `appsettings.Development.json`
in WP-04):

```
# app role (runtime)
psql "host=localhost port=5432 dbname=leasebook user=leasebook_app password=dev_app_pw"

# migrator role (migrations / design-time)
psql "host=localhost port=5432 dbname=leasebook user=leasebook_migrator password=dev_migrator_pw"
```

Or inside the container (local socket, no password): `./scripts/dev.ps1 psql`.

Verify the app role can connect:

```
docker compose exec db psql -U leasebook_app -d leasebook -c "SELECT current_user;"
```
