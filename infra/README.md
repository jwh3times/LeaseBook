# Infrastructure (Bicep)

Authored Azure infrastructure for `dev` and `prod`. Deployment is gated on operator Azure access;
authoring and `az bicep build` are not.

## Layout

- `main.bicep` — subscription-scoped entry point: creates the resource group and wires the modules.
- `modules/` — `monitoring` (Log Analytics + App Insights), `registry` (ACR), `database`
  (PostgreSQL Flexible Server 18), `vault` (Key Vault, RBAC), `storage` (blobs), `containerapp`
  (managed identity + Container Apps environment + app, with AcrPull / Key Vault Secrets User RBAC).
- `env/dev.bicepparam`, `env/prod.bicepparam` — per-environment parameters.
- `db/azure-bootstrap.md` — how the operator creates the three Postgres roles (Bicep can't).

## Naming convention

`lb-<env>-<resource>` for hyphen-friendly resources (`lb-dev-rg`, `lb-dev-pg`, `lb-dev-kv`,
`lb-dev-cae`, `lb-dev-app`, `lb-dev-id`, `lb-dev-ai`, `lb-dev-logs`). Globally-unique,
hyphen-averse names compress to `lb<env>acr` (ACR) and `lb<env>storage<hash>` (storage).

## Validate / deploy

```bash
az bicep build --file infra/main.bicep
LEASEBOOK_PG_ADMIN_PASSWORD=... az deployment sub what-if \
  --location eastus2 --template-file infra/main.bicep --parameters infra/env/dev.bicepparam
LEASEBOOK_PG_ADMIN_PASSWORD=... az deployment sub create \
  --location eastus2 --template-file infra/main.bicep --parameters infra/env/dev.bicepparam
```

## Secrets contract

The app reads configuration from environment variables supplied by Container Apps, each referencing
a Key Vault secret (resolved via the app's managed identity):

| Env var                                 | Source                           | Used by                           |
| --------------------------------------- | -------------------------------- | --------------------------------- |
| `ConnectionStrings__Default`            | Key Vault secret (app role)      | the running app (RLS-subject)     |
| `ConnectionStrings__Migrations`         | Key Vault secret (migrator role) | the deploy migration job **only** |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights (module output)     | telemetry exporter                |

Real role passwords live in Key Vault only; `infra/db/bootstrap.sql` dev passwords are dev-only.

## Production networking

`database.bicep` sets `publicNetworkAccess: 'Disabled'` for prod. A real prod deploy must also wire
`network.delegatedSubnetResourceId` + a private DNS zone (VNet integration) so the Container App can
reach the server privately. Dev stays public + firewall-gated (Allow Azure Services) for the CI
migration job.
