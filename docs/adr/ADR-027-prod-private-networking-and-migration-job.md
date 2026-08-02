# ADR-027: Production private networking and migrations as a Container Apps Job

- **Status:** Accepted
- **Date:** 2026-08-02
- **Deciders:** Engineering

## Context

Production must not expose the trust-accounting database to the internet. The infrastructure has
always said so, but the wiring to make it true did not exist: there was no VNet, no delegated
subnet, and no private DNS zone, so `infra/main.bicep` could not actually produce a privately
networked server.

Closing that opens a second problem. Prod migrations currently run from a GitHub-hosted runner
(`deploy-prod.yml`, `dotnet ef database update`). A runner on the public internet cannot reach a
server that has no public endpoint, so private networking and the existing migration path are
mutually exclusive — one of them has to change.

Two facts discovered while authoring this changed the shape of the decision, and both contradicted
what the repository previously documented:

- **`publicNetworkAccess` and VNet injection are mutually exclusive.** Per the ARM reference for
  `Microsoft.DBforPostgreSQL/flexibleServers`, `publicNetworkAccess` "is only supported for servers
  that are **not** integrated into a virtual network which is owned and provided by customer". A
  VNet-injected server has no public endpoint to disable. The previous description of prod as
  `publicNetworkAccess: 'Disabled'` **plus** VNet integration was not a stricter configuration; it
  was an invalid one that the resource provider rejects.
- **The Container Apps subnet minimum depends on the environment type, and the two types have
  opposite delegation rules.** Workload profiles (v2, the current default) requires a `/27` minimum
  and the subnet **must** be delegated to `Microsoft.App/environments`. Legacy Consumption-only (v1)
  requires `/23` and the subnet must **not** be delegated to anything. The `/23` figure carried in
  our planning notes was the legacy number. Getting the type wrong does not merely waste addresses —
  it makes the delegation illegal and fails at apply.

## Decision

**Prod runs a VNet-injected PostgreSQL Flexible Server, and prod migrations run as a Container Apps
Job inside the same environment.**

Networking is introduced by a new `infra/modules/network.bicep` (VNet, an ACA subnet delegated to
`Microsoft.App/environments`, a Postgres subnet delegated to
`Microsoft.DBforPostgreSQL/flexibleServers`, the `privatelink.postgres.database.azure.com` private
DNS zone, and the VNet link), gated behind an `enablePrivateNetworking` parameter — `true` in prod,
`false` in dev. `database.bicep` switches its whole `network` object rather than adding fields to
it, because of the incompatibility above.

The Container Apps environment declares `workloadProfiles` explicitly **in both environments**
rather than relying on the API default. Prod needs it for the delegation to be legal; dev takes it
so the two environments are the same type. Environment type cannot be changed in place, so parity is
free before the first apply and requires recreating dev's environment at any later date.

Migrations run from the existing `migrator` Dockerfile target as a manual-trigger job, using the
existing user-assigned identity: `AcrPull` for the image, and a Key Vault reference for
`ConnectionStrings__Migrations`. No new identity, no new role assignment, and no new secret in the
contract — only a new consumer of one that already existed. `replicaRetryLimit` is `0`: a failed
migration may have applied part of a batch, and blind retry is how a half-migrated schema happens.

The address plan is `10.40.0.0/22` — `10.40.0.0/23` for ACA, `10.40.2.0/27` for Postgres, remainder
reserved for future private endpoints. It avoids the portal/CLI defaults (`10.0.0.0/16`,
`10.1.0.0/16`), home LAN ranges (`192.168.0.0/16`), Docker's bridge (`172.17.0.0/16`), and every
range Azure reserves for a workload-profiles environment. Both subnets are deliberately larger than
their verified minimums, because an ACA revision change temporarily doubles address demand, the PITR
runbook stands a restored server up alongside the original, and **a subnet cannot be grown once
resources exist in it**. The headroom is free today and cannot be obtained afterwards.

### Rejected: a self-hosted GitHub runner inside the VNet

This works and is the smallest change to the workflow. It is rejected because it introduces a
permanently running, permanently patched compute asset whose only purpose is to sit on the correct
side of a network boundary, and it gives a GitHub-controlled process a durable foothold inside the
VNet containing the trust-accounting database. A job that exists only for the seconds it runs is a
materially smaller blast radius. It would also add a second CI runtime to keep in lockstep with the
pinned `dotnet-ef` version — reintroducing exactly the drift the containerized `migrator` target was
built to remove.

### Rejected: temporary firewall exceptions around each deploy

This one is not a worse trade-off; it is **unavailable**. A VNet-injected server has no public
endpoint and no firewall rules to open. The familiar pattern — punch in the runner's egress IP,
remove it afterwards — belongs to public-access servers. Adopting it here would mean abandoning VNet
injection entirely and running prod on public access with a rule opened for the duration of every
deploy, from GitHub's shared and published egress ranges. That is a standing exception to "the
production database is not reachable from the internet" in exchange for avoiding one Bicep resource.
The mechanism is also fragile independent of the security argument: a cancelled workflow leaves the
rule in place, and GitHub Actions offers no reliable always-remove guarantee.

## Consequences

- Prod's database is unreachable from the internet by construction rather than by configuration —
  there is no public endpoint to misconfigure.
- The prod migration credential never enters the workflow environment. It moves from a GitHub secret
  to a Key Vault reference resolved by the job's managed identity, so `MIGRATIONS_CONNECTION_STRING`
  is no longer needed for prod.
- `deploy-prod.yml` must now **poll the job execution to a terminal state**. `az containerapp job
start` returns when the start succeeds, not when the migration finishes, so without polling a
  failed migration would look like a successful deploy and the app revision would roll on top of a
  half-migrated schema. The workflow also builds and pushes a `leasebook-migrator` image, which
  nothing pushed before.
- Deploying prod is now a two-image operation, and both are tagged with the same commit SHA so the
  schema applied and the app running it are provably the same commit.
- The address plan becomes effectively permanent at first apply. Subnets cannot be resized and a
  Flexible Server cannot be moved between VNets.
- First-deploy ordering is handled by making the Key Vault secret URI a parameter that defaults to
  empty: Key Vault is created empty by this same template and the Postgres roles do not exist until
  the operator runs the role bootstrap, so hard-wiring the reference would make the very first prod
  deployment fail on an unresolvable secret.
- **This decision's correctness is only compile-verified.** CI now compiles every template, but a
  compile cannot see overlapping address space, a subnet delegated to the wrong service, or a DNS
  zone that exists but is not linked. Azure explicitly no longer validates VNet-link presence at
  server creation, so a missing link is silent at deploy and surfaces only when something tries to
  connect. The first `what-if` is a real gate, not a formality.

## Revisit trigger

Reopen if any of the following happens:

- The address plan collides with something the operator already runs, or the project acquires a hub
  network or VPN it must peer with — the ranges are three parameters, but only before first apply.
- Migrations grow beyond the job's `replicaTimeout` (1800s), or a migration needs to run against a
  server the ACA environment cannot reach.
- Azure changes the workload-profiles delegation requirement or the subnet minimums, at which point
  the pinned `workloadProfiles` declaration and the subnet sizes should both be re-derived from the
  live documentation rather than from this record.
- Private endpoints are added for Key Vault, ACR, or Storage — the reserved space in the VNet is
  sized for that, but the DNS story for each needs its own decision.
