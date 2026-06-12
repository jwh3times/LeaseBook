-- ============================================================================================
-- LeaseBook database bootstrap — creates the application database and the three purpose-separated
-- roles that make Postgres RLS a real security boundary (CLAUDE.md multi-tenancy; §C.3).
--
-- DEV ONLY — these passwords are local placeholders. Real environments create the roles against
-- Azure Flexible Server with real passwords stored in Key Vault and the app connecting via
-- managed identity (see infra/db/azure-bootstrap.md, WP-10). NEVER commit real credentials.
--
-- Runs once on first container init (postgres entrypoint), connected as superuser 'postgres'.
-- ============================================================================================

-- 1. The three roles, separated by purpose (RLS does not apply to a table's owner by default —
--    this separation is what makes FORCE ROW LEVEL SECURITY on the app role meaningful).
--    * leasebook_migrator — owns the schema, runs migrations only, never serves traffic.
--    * leasebook_app       — runtime DML role, RLS-subject (FORCE in migrations).
--    * leasebook_ops       — read-only support/reporting role, also RLS-subject.
CREATE ROLE leasebook_migrator LOGIN PASSWORD 'dev_migrator_pw';
CREATE ROLE leasebook_app      LOGIN PASSWORD 'dev_app_pw';
CREATE ROLE leasebook_ops      LOGIN PASSWORD 'dev_ops_pw';

-- 2. Application database, owned by the migrator (the schema owner).
CREATE DATABASE leasebook OWNER leasebook_migrator;

-- 3. Schema-level setup inside the new database.
\connect leasebook

ALTER SCHEMA public OWNER TO leasebook_migrator;

-- Tighten the default-open public schema, then grant explicitly.
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT  CREATE, USAGE ON SCHEMA public TO leasebook_migrator;
GRANT  USAGE          ON SCHEMA public TO leasebook_app, leasebook_ops;

-- 4. Default privileges: every table the migrator creates later automatically grants DML to the
--    app role and SELECT to the ops role — so a new migration's tables are usable without a manual
--    grant. Append-only tables (audit_events in WP-05; journal_* in M1) additionally REVOKE
--    UPDATE, DELETE on the app/ops roles in their migration (Rls.RevokeAppendOnly).
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO leasebook_app;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT SELECT ON TABLES TO leasebook_ops;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO leasebook_app;

-- Note: IDs are UUIDv7 generated app-side (P6), so sequences are rare; the grant above is
-- forward-looking and harmless.
