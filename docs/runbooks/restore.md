# Runbook: Point-in-time restore (PostgreSQL Flexible Server)

- **Audience:** Deployment operators and maintainers
- **Status:** Draft runbook; blocked on the first live restore drill
- **Owner:** Maintainers
- **Last reviewed:** 2026-09-20

Skeleton procedure for a future deployment. LeaseBook is not publicly deployed; the first restore
drill is deferred to public distribution under the consolidated deployment handoff. Record real
timings and screenshots after that drill.

## When to use

- Accidental data loss / bad migration in an environment.
- Ransomware / corruption suspicion.
- Compliance drill.

Flexible Server PITR creates a **new** server restored to a chosen timestamp within the backup
retention window (dev: 7 days, prod: 35 days — see `infra/modules/database.bicep`). The original
server is untouched, so restore is non-destructive until you cut over.

## Before you start: production is inside a VNet

Production's server is **VNet-injected and has no public endpoint** ([ADR-027](../adr/ADR-027-prod-private-networking-and-migration-job.md)).
That changes this procedure in four ways, and none of them apply to dev, which stays public and
firewall-gated:

- **The restored server lands in the same delegated subnet** (`10.40.2.0/27`, delegated to
  `Microsoft.DBforPostgreSQL/flexibleServers`). That subnet was deliberately sized above its `/28`
  minimum so a restored HA server can run alongside the original before cutover — check free
  addresses before restoring, and confirm the exact subnet / private-DNS-zone arguments against the
  current `az postgres flexible-server restore` reference, because a VNet-injected restore is not
  the same command shape as a public one.
- **Verify from the manual `lb-prod-dbadmin` job inside the existing VNet.** Use its
  [read-only verification procedure](../../infra/db/azure-bootstrap.md#restore-verification)
  with the restored server selected explicitly. Live DNS, identity, credentials and execution
  remain first-drill verification steps.
- **Do not place a resource lock on the private DNS zone** (`privatelink.postgres.database.azure.com`)
  at any point. Azure documents that locks there break Postgres HA failover.
- **Cutover is a Key Vault edit, not a workflow variable edit.** Both `ConnectionStrings__Default`
  (the app) and `ConnectionStrings__Migrations` (the `<prefix>-migrate` Container Apps Job) are
  Key Vault references, so updating the secret values repoints both. The app needs a revision
  restart; the migration job picks the new value up on its next execution.

## Procedure

1. Identify the target timestamp (UTC) — just before the incident.
2. Restore to a new server:

   ```bash
   az postgres flexible-server restore \
     --resource-group lb-<env>-rg \
     --name lb-<env>-pg-restored \
     --source-server lb-<env>-pg \
     --restore-time "<YYYY-MM-DDTHH:MM:SSZ>"
   ```

3. Verify the restored data (connect as `leasebook_ops`, spot-check the trust equation and recent
   journal entries on the affected org). In production this connection must originate inside the
   VNet — use the administration job above for the org-scoped journal spot-check. It does not
   calculate the trust equation: the separate invariant suite remains required before cutover.
4. Cut over: update the `ConnectionStrings__Default` / `__Migrations` Key Vault secrets to point at
   the restored server, restart the Container App revision, confirm `/api/health`.
5. Decommission the old server once the restored one is confirmed healthy and reconciled.

## Alongside the drill: back up the Data Protection wrapping key

A PITR restores the database, and the Data Protection keyring is persisted in the database — but the
keyring is wrapped by the `dataprotection` RSA key in `lb-<env>-kv` ([ADR-041](../adr/ADR-041-durable-keyring-and-proxy-trust.md)),
whose private half never leaves the vault and is therefore not part of any database backup. The two
have to be recoverable together: without the key, a restored database's `asp_net_user_tokens.value`
(TOTP secrets and two-factor recovery codes) cannot be decrypted and existing auth/antiforgery
cookies cannot be validated. Prod's vault carries purge protection (`infra/modules/vault.bicep`), so
the first recovery path is the vault's own soft-delete window:

```bash
# The key still exists in the vault but was deleted — recover it in place.
az keyvault key recover --vault-name lb-<env>-kv --name dataprotection
```

Keep an independent copy as well, refreshed whenever the key is rotated, and stored where the
vault's own credentials are not:

```bash
az keyvault key backup \
  --vault-name lb-<env>-kv \
  --name dataprotection \
  --file dataprotection-<env>-<YYYYMMDD>.keybackup
```

Restoring that blob is `az keyvault key restore --vault-name <target> --file <path>`. Three
constraints shape where it is useful, so read them before relying on it: the blob can only be
restored into a Key Vault in the **same subscription and the same Azure geography**; the restore
fails if a key of that name already exists in the target vault, **including a soft-deleted one**
(recover it instead, as above); and the file is the key — treat it as a credential even though it is
encrypted and bound to the service.

## Notes

- Backups are automatic; retention is configured in Bicep. Geo-redundant backup is enabled in prod.
- The trust-accounting invariant suite should be run against the restored database before cutover —
  a restore that doesn't reconcile to the cent is not a successful restore.
- **TODO (first drill):** record actual restore duration, data-loss window observed,
  observed behavior of the administration job inside the VNet, whether a `dataprotection` key backup
  round-trips into a second vault, and any manual steps discovered.
