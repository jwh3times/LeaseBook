-- Azure adaptation of bootstrap.sql. Database already exists and the administrator is not superuser.
-- Run only through admin.sh: stdin supplies three password confirmations to client-side \password.
\echo dbadmin: acquiring bootstrap transaction lock
SELECT pg_advisory_xact_lock(337, 1) \g /dev/null
\echo dbadmin: ensuring roles and administrator membership
SELECT 'CREATE ROLE leasebook_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'leasebook_migrator') \gexec
SELECT 'CREATE ROLE leasebook_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'leasebook_app') \gexec
SELECT 'CREATE ROLE leasebook_ops LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'leasebook_ops') \gexec
-- PG16+ creators have ADMIN but do not necessarily have SET; make the required membership explicit.
GRANT leasebook_migrator, leasebook_app, leasebook_ops TO :"USER" WITH SET TRUE;
SET LOCAL password_encryption = 'scram-sha-256';
\echo dbadmin: synchronizing role passwords
\password leasebook_migrator
\password leasebook_app
\password leasebook_ops

\echo dbadmin: applying schema privileges and checking ownership
ALTER SCHEMA public OWNER TO leasebook_migrator;
REVOKE ALL ON SCHEMA public FROM PUBLIC;
GRANT CREATE, USAGE ON SCHEMA public TO leasebook_migrator;
GRANT USAGE ON SCHEMA public TO leasebook_app, leasebook_ops;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO leasebook_app;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT SELECT ON TABLES TO leasebook_ops;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO leasebook_app;
CREATE SCHEMA IF NOT EXISTS hangfire AUTHORIZATION leasebook_app;
-- Refuse pre-existing privilege/ownership drift rather than silently blessing it.
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_roles WHERE rolname IN ('leasebook_migrator', 'leasebook_app', 'leasebook_ops')
    AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls OR NOT rolcanlogin))
    OR (SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'hangfire') <> 'leasebook_app'
    OR has_database_privilege('leasebook_app', current_database(), 'CREATE')
    OR has_database_privilege('leasebook_ops', current_database(), 'CREATE')
    OR has_schema_privilege('leasebook_app', 'public', 'CREATE')
    OR has_schema_privilege('leasebook_ops', 'public', 'CREATE') THEN
    RAISE EXCEPTION 'Unexpected role privileges or schema ownership';
  END IF;
END $$;
GRANT USAGE ON SCHEMA hangfire TO leasebook_ops;
ALTER DEFAULT PRIVILEGES FOR ROLE leasebook_app IN SCHEMA hangfire
  GRANT SELECT ON TABLES TO leasebook_ops;
-- Never grant on existing tables: their migration-owned append-only revocations must survive replay.
