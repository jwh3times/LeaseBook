# Infrastructure (Bicep)

Authored Azure infrastructure for `dev` and `prod`. Deployment is gated on operator Azure access;
authoring and `az bicep build` are not.

## Layout

- `main.bicep` — subscription-scoped entry point: creates the resource group and wires the modules.
- `modules/` — `monitoring` (Log Analytics + App Insights), `registry` (ACR), `database`
  (PostgreSQL Flexible Server 18), `vault` (Key Vault, RBAC), `storage` (blobs), `network` (VNet,
  delegated subnets, private DNS zone — prod only), `containerapp` (managed identity + Container Apps
  environment + app + migrator job, with AcrPull / Key Vault Secrets User RBAC).
- `env/dev.bicepparam`, `env/prod.bicepparam` — per-environment parameters.
- `db/azure-bootstrap.md` — how the operator creates the three Postgres roles and the app-owned
  `hangfire` job-storage schema (Bicep can't). Both are prerequisites, not optional: the app fails at
  startup if the `hangfire` schema is missing, because the runtime role cannot create it itself.

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

| Env var                                 | Source                               | Used by                                                                   |
| --------------------------------------- | ------------------------------------ | ------------------------------------------------------------------------- |
| `ConnectionStrings__Default`            | Key Vault secret (app role)          | the running app (RLS-subject)                                             |
| `ConnectionStrings__Migrations`         | Key Vault secret (migrator role)     | the migrator Container Apps Job **only** (prod); the deploy runner in dev |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights (module output)         | telemetry exporter                                                        |
| `AllowedHosts`                          | app setting, supplied at deploy time | ASP.NET Core host filtering (`HostFilteringMiddleware`)                   |

Real role passwords live in Key Vault only; `infra/db/bootstrap.sql` dev passwords are dev-only.

`AllowedHosts` in `appsettings.Production.json` ships as an empty placeholder — the real deploy must
set it to the production hostname(s), semicolon-separated (e.g. `app.leasebook.com;www.leasebook.com`),
as a Container Apps app setting / env var.

## Production networking

Prod's PostgreSQL Flexible Server is **VNet-injected: it has no public endpoint at all.** This is not
the same as "public access disabled" — the two are mutually exclusive. Per the ARM reference,
`publicNetworkAccess` "is only supported for servers that are not integrated into a virtual network
which is owned and provided by customer", so a VNet-injected server has no such property to set and
the resource provider rejects the combination. `database.bicep` therefore switches its whole
`network` object on `delegatedSubnetResourceId` rather than adding fields to it.

`network.bicep` (deployed only when `enablePrivateNetworking` is `true`) provides the VNet, both
subnets, the `privatelink.postgres.database.azure.com` private DNS zone, and the VNet link:

| Range          | Purpose                                                               |
| -------------- | --------------------------------------------------------------------- |
| `10.40.0.0/22` | VNet                                                                  |
| `10.40.0.0/23` | Container Apps — delegated to `Microsoft.App/environments`            |
| `10.40.2.0/27` | PostgreSQL — delegated to `Microsoft.DBforPostgreSQL/flexibleServers` |
| `10.40.2.32`+  | Unallocated; reserved for future private endpoints (Key Vault, ACR)   |

Both subnets are larger than the documented minimums (`/27` for a workload-profiles Container Apps
environment, `/28` for Postgres) on purpose: an ACA revision change temporarily doubles address
demand, the PITR runbook stands a restored server up alongside the original, and **a subnet cannot be
grown once resources exist in it.** The headroom is free before the first apply and unobtainable
after it. The ranges are parameters in `env/prod.bicepparam` — change them there, before first apply.

The Container Apps environment declares `workloadProfiles` explicitly in **both** environments. The
environment type decides whether the subnet delegation is legal (workload profiles v2 _requires_
delegation to `Microsoft.App/environments`; legacy Consumption-only _forbids_ any delegation), and
environment type cannot be changed in place.

Dev stays public + firewall-gated (Allow Azure Services) so its CI migration job can reach the server
from a GitHub-hosted runner.

## Production migrations

A GitHub-hosted runner cannot reach a server with no public endpoint, so prod migrations do **not**
run from the workflow host. `deploy-prod.yml` builds and pushes the `migrator` image, then starts the
`<prefix>-migrate` **Container Apps Job** inside the same environment and polls it to a terminal
state before updating the app revision. The job reads `ConnectionStrings__Migrations` as a Key Vault
reference resolved by the shared user-assigned identity, so the migrator credential never enters the
workflow environment — `MIGRATIONS_CONNECTION_STRING` is not required for prod.

Polling is load-bearing: `az containerapp job start` returns when the _start_ succeeds, not when the
migration finishes. Without it, a failed migration looks like a successful deploy and the new app
revision rolls on top of a half-migrated schema. The job sets `replicaRetryLimit: 0` — a failed
migration may have applied part of a batch, so it is inspected by a human, never retried blindly.

See [ADR-027](../docs/adr/ADR-027-prod-private-networking-and-migration-job.md).

## What the first prod `what-if` / apply must prove

CI compiles every template, but a compile cannot see whether a deployment is _correct_. The
following are unverified by construction and must be checked on the first real deployment — none of
them will fail the build, and several fail silently at deploy and only surface later.

| Check                                                                                                                                           | Why a compile cannot catch it                                                                                                                |
| ----------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------- |
| **The ACA environment comes up as workload profiles (v2) and accepts the `Microsoft.App/environments` delegation**                              | Environment type and subnet delegation must agree; both compile either way. Highest-risk item.                                               |
| **The RP accepts the `network` object** (`delegatedSubnetResourceId` + `privateDnsZoneArmResourceId`, no `publicNetworkAccess`)                 | Matched to the documented rule, but confirmed only by reference text, not an apply.                                                          |
| **DNS resolves end to end** — the app resolves `lb-prod-pg.postgres.database.azure.com` to a private IP                                         | Azure no longer validates VNet-link presence at server creation. A missing or wrong link is **silent** at deploy and fails at first connect. |
| **The address plan does not overlap** anything the operator already runs                                                                        | Compile has no view of existing networks, hubs, or VPNs.                                                                                     |
| **The migrator job's first execution** — image pull via AcrPull from inside the VNet, Key Vault reference resolution, and Postgres reachability | Nothing has ever pushed a `leasebook-migrator` image or run this job.                                                                        |
| **`az containerapp job start` returns a usable execution name**                                                                                 | `deploy-prod.yml` falls back to listing the latest execution if the field is empty; the fallback is untested against the real CLI output.    |

Ordering note: Key Vault is created empty by this template and the Postgres roles do not exist until
the [role bootstrap](db/azure-bootstrap.md) runs, so `migrationsSecretUri` defaults to empty and the
migrator's secret wiring is omitted until it is supplied. The first prod deployment therefore
succeeds without it; store the secret, pass the URI, redeploy. This un-mutes itself — no code change.

### Deliberate omissions

- **No NSG on either subnet.** Neither service requires one, and an over-restrictive NSG on an ACA
  subnet is a common way to break the platform's own traffic. Revisit if the security review wants
  one, but design it against the Container Apps required-traffic documentation, not from first
  principles.
- **Job sizing is not benchmarked.** `cpu: 0.5 / memory: 1Gi` mirrors the app container, and
  `replicaTimeout: 1800` is an estimate — long enough for a cold GeneralPurpose server, short enough
  that a migration holding schema locks surfaces rather than hangs. Both should be revisited after
  the first real migration run, not before.
- **Do not place a resource lock on the private DNS zone.** Azure documents that locks there break
  Postgres HA failover.
