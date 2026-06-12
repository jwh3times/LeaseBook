# Azure Postgres role bootstrap

Bicep cannot create Postgres roles, and Flexible Server's admin login is **not** a true superuser —
so the three application roles (`leasebook_migrator`, `leasebook_app`, `leasebook_ops`) must be
created against the server after it is provisioned, the same way `infra/db/bootstrap.sql` does
locally. This is an idempotent operator step, run once per environment.

## Steps

1. Provision infrastructure (`az deployment sub create …`, see `infra/README.md`). Note the server
   FQDN (`lb-<env>-pg.postgres.database.azure.com`) and the admin login/password used at deploy.
2. Connect as the admin and run the role bootstrap. The committed `infra/db/bootstrap.sql` is for
   **local dev** (it `CREATE DATABASE leasebook` and uses dev passwords); for Azure, the database
   already exists (created by Bicep) and the role passwords come from Key Vault. Run an
   Azure-adapted script that only creates roles + grants + default privileges:

   ```bash
   psql "host=lb-<env>-pg.postgres.database.azure.com port=5432 dbname=leasebook \
         user=lbadmin sslmode=require" -v ON_ERROR_STOP=1 <<'SQL'
   -- passwords pulled from Key Vault, injected by the operator/runbook (never inline)
   CREATE ROLE leasebook_migrator LOGIN PASSWORD :'migrator_pw';
   CREATE ROLE leasebook_app      LOGIN PASSWORD :'app_pw';
   CREATE ROLE leasebook_ops      LOGIN PASSWORD :'ops_pw';
   GRANT ALL ON SCHEMA public TO leasebook_migrator;
   ALTER SCHEMA public OWNER TO leasebook_migrator;
   GRANT USAGE ON SCHEMA public TO leasebook_app, leasebook_ops;
   ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
     GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO leasebook_app;
   ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
     GRANT SELECT ON TABLES TO leasebook_ops;
   SQL
   ```

3. Store the three role passwords as Key Vault secrets and the assembled
   `ConnectionStrings__Default` (app) / `ConnectionStrings__Migrations` (migrator) connection
   strings (see the secrets contract in `infra/README.md`).
4. Run migrations as the migrator role (the deploy workflow's one-shot job).

## AAD / managed identity (preferred, future)

Flexible Server supports Microsoft Entra authentication. The target end-state is for `leasebook_app`
to be a managed-identity-backed role (no stored password) and the Container App to authenticate with
its user-assigned identity. Password roles above are the Phase-1 path; record the switch to Entra
auth as an ADR when it lands.
