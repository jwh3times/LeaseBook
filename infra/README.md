# Infrastructure (Bicep)

Authored Azure infrastructure for `dev` and `prod`. Deployment is gated on operator Azure access;
authoring and `az bicep build` are not.

## Layout

- `main.bicep` — subscription-scoped entry point: creates the resource group and wires the modules.
- `modules/` — `monitoring` (Log Analytics + App Insights), `registry` (ACR), `database`
  (PostgreSQL Flexible Server 18), `vault` (Key Vault, RBAC), `storage` (blobs), `network` (VNet,
  delegated subnets, private DNS zone — prod only), `containerapp` (managed identity + Container Apps
  environment + app + migrator job + capabilities job, with AcrPull / Key Vault Secrets User RBAC).
- `env/dev.bicepparam`, `env/prod.bicepparam` — per-environment parameters.
- `jobs/capabilities-exec.yaml` — the execution template an operator copies to run a capability
  command in production. Pinned against `modules/containerapp.bicep` by
  `CapabilitiesJobTemplateTests`; copy it rather than retyping it, because the CLI silently discards
  any key whose casing it does not recognise.
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
| `ConnectionStrings__Default`            | Key Vault secret (app role)          | the running app (RLS-subject); the capabilities Container Apps Job        |
| `ConnectionStrings__Migrations`         | Key Vault secret (migrator role)     | the migrator Container Apps Job **only** (prod); the deploy runner in dev |
| `APPLICATIONINSIGHTS_CONNECTION_STRING` | App Insights (module output)         | telemetry exporter                                                        |
| `AllowedHosts`                          | app setting, supplied at deploy time | ASP.NET Core host filtering (`HostFilteringMiddleware`)                   |
| `LEASEBOOK_OPERATOR`                    | supplied per job execution           | the capabilities job — names the accountable party on every audit row     |

The two connection strings are **different credentials and must stay so**: the migrator job holds
schema-owner rights on `public`, the capabilities job holds only the app role's DML under RLS's
platform escape. Pointing both at one secret would hand an operator tool DDL.

`LEASEBOOK_OPERATOR` is deliberately not stored in Key Vault and not defaulted in the template. It
identifies a person, per invocation, and `platform_audit_events` is append-only in both planes — a
baked-in value would be a permanent, plausible-looking lie. The verb refuses every mutating
subcommand without it outside Development; see the capability control section below.

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

## Production capability control

The same network boundary produces a second job. Feature flags, entitlements and cohorts are written
only by the `capabilities` CLI verb (ADR-028) — there is no endpoint and no UI, deliberately — and
that verb runs inside the app process. With no public endpoint there is no shell into the prod
network, so `lb-<env>-capabilities` is a manual-trigger Container Apps Job in the same environment.
Without it, "roll out and roll back without a deploy" is not delivered and the kill switch cannot be
reached.

Three things differ from the migrator job, and each is deliberate:

|            | migrator job                                       | capabilities job                                                         |
| ---------- | -------------------------------------------------- | ------------------------------------------------------------------------ |
| Image      | `leasebook-migrator:<sha>` (EF bundle)             | `leasebook:<sha>` — the **app** image; the verb lives in `LeaseBook.Web` |
| Role       | `leasebook_migrator` (DDL on `public`)             | `leasebook_app` (DML on the four platform tables)                        |
| Started by | `deploy-prod.yml`, then polled to a terminal state | an operator, by hand, during a rollout or an incident                    |

The app image is used because the capability **registry is source code**: a job built from a different
commit knows a different set of capabilities than the app it is being used to control. `appImageTag`
is therefore shared by the container app and this job in the template — but nothing re-pins the job
after a `deploy-prod` revision update, so the runbook reads the image off the running app instead of
trusting the template.

One job definition serves all eight subcommands, but **only through the `--yaml` execution-template
form**. The `--args` flag is an argparse `nargs='*'` list and argparse treats any unknown token
starting with `-` as an option, so `--args "capabilities" "list" "--org" "demo"` exits 2 with
`unrecognized arguments: --org demo`. `--org` is required by `grant`, `revoke`, `cohort add` and
`cohort remove`, so four of the seven are unreachable that way; `--yaml` carries them verbatim. The
runbook documents that one form only, rather than a short form that silently misbehaves on exactly the
arguments an incident needs.

The job carries no schedule and no event trigger, and must not grow one — a capability flip is a
decision, never a timer. Its default args are `capabilities list`, so a bare start is a read-only
smoke test rather than an ASP.NET host booted inside a job.

**The full operator invocation lives in [`docs/runbooks/diagnostics.md`](../docs/runbooks/diagnostics.md)**,
including the traps a first attempt will hit: `az containerapp job start` does not merge with the job
template (anything omitted from an execution template is simply absent — env, container name,
resources), an omitted container name defaults to the _job_ name, and a misspelled YAML key is
swallowed into `additionalProperties` with no error.

## What the first prod `what-if` / apply must prove

CI compiles every template, but a compile cannot see whether a deployment is _correct_. The
following are unverified by construction and must be checked on the first real deployment — none of
them will fail the build, and several fail silently at deploy and only surface later.

| Check                                                                                                                                                                                        | Why a compile cannot catch it                                                                                                                                                                                                                                                                      |
| -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| **The ACA environment comes up as workload profiles (v2) and accepts the `Microsoft.App/environments` delegation**                                                                           | Environment type and subnet delegation must agree; both compile either way. Highest-risk item.                                                                                                                                                                                                     |
| **The RP accepts the `network` object** (`delegatedSubnetResourceId` + `privateDnsZoneArmResourceId`, no `publicNetworkAccess`)                                                              | Matched to the documented rule, but confirmed only by reference text, not an apply.                                                                                                                                                                                                                |
| **DNS resolves end to end** — the app resolves `lb-prod-pg.postgres.database.azure.com` to a private IP                                                                                      | Azure no longer validates VNet-link presence at server creation. A missing or wrong link is **silent** at deploy and fails at first connect.                                                                                                                                                       |
| **The address plan does not overlap** anything the operator already runs                                                                                                                     | Compile has no view of existing networks, hubs, or VPNs.                                                                                                                                                                                                                                           |
| **The migrator job's first execution** — image pull via AcrPull from inside the VNet, Key Vault reference resolution, and Postgres reachability                                              | Nothing has ever pushed a `leasebook-migrator` image or run this job.                                                                                                                                                                                                                              |
| **`az containerapp job start` returns a usable execution name**                                                                                                                              | `deploy-prod.yml` prefers the name the start prints and otherwise adopts the one execution that did not exist a moment earlier (a set difference, never "most recent"). Both halves are untested against the real CLI output.                                                                      |
| **`:latest` does not exist in ACR** — no workflow ever pushes it; both `deploy-dev` and `deploy-prod` tag only by git SHA                                                                    | Both image tags default to `latest` in the template, so the first apply creates an app revision and two jobs pointing at a tag that is not there. Pass real tags to the first `az deployment sub create`, or accept that no image resolves until the first `deploy-prod` pins a SHA.               |
| **The capabilities job's first execution** — the app image booting as a one-shot CLI, `ConnectionStrings__Default` resolving, and the verb reaching the platform tables under `app.platform` | Nothing has run the app image with a CLI verb in Azure. Run the bare (`capabilities list`) form first: it is read-only and proves pull, secret resolution and reachability in one go.                                                                                                              |
| **A per-execution override reaches the container intact**                                                                                                                                    | The CLI sends an execution template built only from what was passed; whether the RP merges the remainder from the job template is not documented. Both invocations send the complete container spec so they are correct either way — confirm which happens before anyone shortens one.             |
| **Re-applying the template does not roll the app back** — `appImageTag` now drives the container app's image as well as the job's, and it defaults to `latest`                               | An `az deployment sub create` that does not pass the currently-deployed tag rewrites the app revision to that default. The hazard is not new (the image was previously hardcoded `:latest`), but it is now a parameter, so pass the running tag explicitly on any re-apply after the first deploy. |
| **`--cpu` / `--memory` on a per-execution override** — `deploy-prod.yml` now passes them; the CLI sets `resources` only when they are given                                                  | Unverified whether the RP falls back to the job template's resources when an execution template omits them. Passing them makes the question moot for the migrator, and the capabilities runbook's YAML includes them for the same reason.                                                          |
| **Role assignments have propagated before the first job execution**                                                                                                                          | `dependsOn` orders ARM's _creation_ of the AcrPull and Key Vault Secrets User assignments, not Entra's propagation of them, which can lag minutes. A first execution shortly after deployment can fail to pull or to resolve its secret; wait and re-run before investigating.                     |

Ordering note: Key Vault is created empty by this template and the Postgres roles do not exist until
the [role bootstrap](db/azure-bootstrap.md) runs, so `migrationsSecretUri` defaults to empty and the
migrator's secret wiring is omitted until it is supplied. The first prod deployment therefore
succeeds without it; store the secret, pass the URI, redeploy. This un-mutes itself — no code change.

### Deliberate omissions

- **No NSG on either subnet.** Neither service requires one, and an over-restrictive NSG on an ACA
  subnet is a common way to break the platform's own traffic. Revisit if the security review wants
  one, but design it against the Container Apps required-traffic documentation, not from first
  principles.
- **Job sizing is not benchmarked.** `cpu: 0.5 / memory: 1Gi` mirrors the app container, and both
  `replicaTimeout` values are estimates — 1800s for migrations (long enough for a cold GeneralPurpose
  server, short enough that a migration holding schema locks surfaces rather than hangs) and 600s for
  capabilities. The latter is mostly not the write: it is a wall-clock deadline over image pull, host
  boot and the transaction, and the app's own documented cold-start budget is 155s. Revisit both after
  the first real run, not before.
- **The capabilities job gets no `APPLICATIONINSIGHTS_CONNECTION_STRING`, deliberately.** Its stdout
  already reaches Log Analytics through the environment's `appLogsConfiguration`, which is what
  `az containerapp job logs show` reads, and that is the channel an operator actually uses. Wiring the
  OpenTelemetry exporter into a one-shot process would also be unreliable: the batch exporter flushes
  on an interval, and this process exits as soon as the verb returns, so a short run could lose the
  telemetry it paid startup cost for. The durable record of what changed is `platform_audit_events`,
  not a trace.
- **`deploy-prod` does not re-pin the capabilities job's image.** It could
  (`az containerapp job update --image`, which merges rather than replacing), but that would add an
  unexercised step to the prod deploy path for something the runbook does not depend on — the
  invocation reads the image off the running app. Reconsider once the deploy path has run for real.
- **Do not place a resource lock on the private DNS zone.** Azure documents that locks there break
  Postgres HA failover.
