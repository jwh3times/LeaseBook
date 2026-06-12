# Runbook: Point-in-time restore (PostgreSQL Flexible Server)

Skeleton procedure. Real timings and screenshots are filled in after the first restore drill (M8
schedules the drill).

## When to use

- Accidental data loss / bad migration in an environment.
- Ransomware / corruption suspicion.
- Compliance drill.

Flexible Server PITR creates a **new** server restored to a chosen timestamp within the backup
retention window (dev: 7 days, prod: 35 days — see `infra/modules/database.bicep`). The original
server is untouched, so restore is non-destructive until you cut over.

## Procedure

1. Identify the target timestamp (UTC) — just before the incident.
2. Restore to a new server:
   ```
   az postgres flexible-server restore \
     --resource-group lb-<env>-rg \
     --name lb-<env>-pg-restored \
     --source-server lb-<env>-pg \
     --restore-time "<YYYY-MM-DDTHH:MM:SSZ>"
   ```
3. Verify the restored data (connect as `leasebook_ops`, spot-check the trust equation and recent
   journal entries on the affected org).
4. Cut over: repoint `ConnectionStrings__Default` / `__Migrations` (Key Vault) at the restored
   server, restart the Container App revision, confirm `/api/health`.
5. Decommission the old server once the restored one is confirmed healthy and reconciled.

## Notes

- Backups are automatic; retention is configured in Bicep. Geo-redundant backup is enabled in prod.
- The trust-accounting invariant suite should be run against the restored database before cutover —
  a restore that doesn't reconcile to the cent is not a successful restore.
- **TODO (first drill):** record actual restore duration, data-loss window observed, and any manual
  steps discovered.
