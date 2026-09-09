# Azure Postgres role bootstrap

Bicep creates the database; `azure-bootstrap.sql` creates the three application roles and their
schema privileges. It adapts the local-only `bootstrap.sql` for Azure. Production uses the manual
`lb-prod-dbadmin` Container Apps Job inside the existing VNet
([ADR-027](../../docs/adr/ADR-027-prod-private-networking-and-migration-job.md)).

## Prerequisites and boundaries

This procedure is authored and locally tested, **not deployment-validated**. LeaseBook is not
publicly deployed; execution is deferred to public distribution under the consolidated deployment
handoff. Only an authorized operator executes it. Required access: deploy Bicep, push to ACR, populate the dedicated credential
vault, start/read Container Apps Jobs and inspect confidential deployment logs. Restrict job
update/start and identity assignment to trusted database operators: execution overrides can select
arbitrary images, so script guards prevent accidents and do not replace RBAC.

The job has its own identity and `lb-prod-dbadmin-kv` vault. The application identity has no grant
on this vault. Do not put the administrator password in the application vault, where the app has
vault-wide secret read access. The administration identity has AcrPull on ACR and Key Vault Secrets
User on its dedicated vault.

Passwords must contain 16–80 bytes without newlines or carriage returns. Generate strong unique
values in the approved secret store. Bootstrap synchronizes role passwords to those inputs on every
invocation; do not change them between retries. Changing them requires coordinated credential rotation.

## Provision and arm

1. Apply `infra/main.bicep` with the reviewed production parameters and `dbAdminSecretsReady=false`.
   This creates the job, identity and empty vault without password references. Preserve current app
   and migrator image tags, secret URIs and network parameters on every reapply.
2. Build the reviewed image and push an immutable commit tag from the repository root:

   ```bash
   TAG=$(git rev-parse HEAD)
   az acr login --name lbprodacr
   docker build -t "lbprodacr.azurecr.io/leasebook-dbadmin:$TAG" infra/db
   docker push "lbprodacr.azurecr.io/leasebook-dbadmin:$TAG"
   ```

3. Open Azure Portal **Key vaults → lb-prod-dbadmin-kv → Objects → Secrets → Generate/Import**.
   Populate these from the approved secret store. Never put resolved values in command arguments,
   templates, files, issue bodies or logs:

   | Secret                       | Value                                  |
   | ---------------------------- | -------------------------------------- |
   | `postgres-admin-password`    | Existing server administrator password |
   | `postgres-migrator-password` | Intended `leasebook_migrator` password |
   | `postgres-app-password`      | Intended `leasebook_app` password      |
   | `postgres-ops-password`      | Intended `leasebook_ops` password      |

4. Inspect what-if and reapply with `dbAdminImageTag` set to the pushed tag and
   `dbAdminSecretsReady=true`. Preserve all other deployed parameters. Identity propagation may
   still take time after the role assignments exist.
5. Copy `infra/jobs/dbadmin-bootstrap-exec.yaml` to `/tmp/dbadmin-bootstrap.yaml`. Set `image` to the
   tagged image, `PGUSER` to the administrator login, and both `PGHOST` and `LEASEBOOK_CONFIRM_HOST`
   to the selected server FQDN. Set `LEASEBOOK_OPERATOR` to your accountable identifier (letters,
   digits, `@._+-`; no spaces). Keep every `secretRef` intact.

## Execute and verify

The complete YAML replaces the execution's container specification. Missing fields or wrong
`secretRef` casing lose configuration. A bare start deliberately refuses.

```bash
RG=lb-prod-rg
JOB=lb-prod-dbadmin
EXEC=$(az containerapp job start --name "$JOB" --resource-group "$RG" \
  --yaml /tmp/dbadmin-bootstrap.yaml --query name -o tsv)
test -n "$EXEC" || exit 1
STATUS=Unknown
for attempt in $(seq 1 90); do
  STATUS=$(az containerapp job execution show --name "$JOB" --resource-group "$RG" \
    --job-execution-name "$EXEC" --query properties.status -o tsv) || exit 1
  case "$STATUS" in
    Succeeded) break ;;
    Failed|Stopped|Degraded) break ;;
  esac
  sleep 10
done
printf 'Execution %s status %s\n' "$EXEC" "$STATUS"
test "$STATUS" = Succeeded || exit 1
```

Inspect system and console logs in **Container Apps Jobs → lb-prod-dbadmin → Execution history →
the named execution**. Confirm selected host/operator, `bootstrap committed` and completion marker.
Record release tag/digest, UTC time, execution name, terminal status and evidence confidentially.

Bootstrap uses one transaction and an advisory lock. Replaying preserves roles and synchronizes
passwords/default privileges. It never grants privileges on existing tables, preserving migration-owned
append-only revocations. `hangfire` stays app-owned; unexpected ownership or elevated application
roles fail and roll back. The administrator receives explicit SET membership on the three roles:
PostgreSQL 16+ role creation does not guarantee the membership needed to create their schemas.

After bootstrap, store app/migrator connection strings in the application vault using those same
role passwords. Arm the existing jobs with `defaultSecretUri` and `migrationsSecretUri`, then use
the normal migration/deploy procedure. Bootstrap does not migrate or seed data.

## Restore verification

Copy `infra/jobs/dbadmin-verify-exec.yaml`. Set the image, restored-server `PGHOST`, matching
confirmation, operator and an existing affected `LEASEBOOK_ORG_ID`. Start and poll as above using
the copied verification file. This execution receives **only** the ops password and establishes
organization context in a read-only transaction. Missing/unknown organizations fail.

Compare the returned database/role/server, journal count and latest entry date with the expected
restore point. The ops secret must match the password at that point in time; if credentials rotated
after it, recover the corresponding approved secret version. Do not bootstrap a restored server
just to make a verification password work.

This spot-check does not prove financial reconciliation. Run `check-invariants --all` separately
before cutover, following the [restore runbook](../../docs/runbooks/restore.md).
Arbitrary-database invariant execution belongs to the consolidated public-distribution handoff;
[#285](https://github.com/jwh3times/LeaseBook/issues/285) preserves its original specification.
This job does not duplicate that engine.

## Failure, retry and cleanup

- No automatic retry. Missing inputs fail before connection; SQL errors roll back. Connection loss
  or timeout after commit can leave the outcome unknown. Inspect before replaying unchanged inputs.
- If the container never ran, inspect image-pull, secret-resolution, DNS and identity-propagation
  events. Otherwise inspect role/schema state through an approved database support channel before
  retrying. Do not enable shell tracing or query echo to diagnose password operations.
- Query echo and raw bootstrap errors are suppressed to avoid credential-derived material in logs. Phase
  markers and platform status provide non-secret evidence. psql `\password` encrypts client-side,
  keeping cleartext out of SQL and server statement logs
  ([PostgreSQL reference](https://www.postgresql.org/docs/18/app-psql.html)).
- A polling deadline does not prove execution stopped. Inspect execution history, stop a remaining
  execution through the operator control, and establish commit outcome before retry. Do not run
  concurrent bootstraps or drop roles/schemas to undo an ambiguous result. Escalate ownership or
  privilege drift to the database maintainer.
- Remove temporary execution YAML after retaining evidence. During a drill keep live application
  secrets unchanged; decommission only the explicitly verified restored server after drill-owner
  approval. The job leaves no running client between executions; its identity and vault remain.

## Dev and future authentication

Dev stays public and firewall-gated. Run this same image from an authorized client network, using
approved secret injection and environment-variable **names** with `docker run -e`; never put values
in arguments. The same explicit host confirmation is required.

Microsoft Entra database authentication remains future work and requires its own ADR. This path
retains the Phase-1 password roles.
