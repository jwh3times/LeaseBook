"""Exercise the actual administration image against disposable PostgreSQL 18 with TLS.

Run: python scripts/test-dbadmin.py. Requires Docker; no Azure access or real credentials.
"""
import os
from pathlib import Path
import subprocess
import time
import unittest
import uuid

ROOT = Path(__file__).resolve().parents[1]
IMAGE = "leasebook-dbadmin:test"
HOST = "lb-test-pg.postgres.database.azure.com"
ORG = "11111111-1111-1111-1111-111111111111"


def docker(*args, stdin=None, env=None, check=True):
    result = subprocess.run(
        ["docker", *args], input=stdin, text=True, capture_output=True,
        env={**os.environ, **(env or {})}, timeout=180,
    )
    if check and result.returncode:
        raise AssertionError(f"Docker command failed: {result.stdout}\n{result.stderr}")
    return result


class DatabaseAdministrationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.name = "lb-dbadmin-test-" + uuid.uuid4().hex[:10]
        docker("build", "-q", "-t", IMAGE, str(ROOT / "infra/db"))
        docker("network", "create", cls.name)
        cls.addClassCleanup(lambda: docker("network", "rm", cls.name, check=False))
        cls.addClassCleanup(lambda: docker("rm", "-fv", cls.name, check=False))
        docker(
            "run", "-d", "--name", cls.name, "--network", cls.name,
            "--network-alias", HOST, "-e", "POSTGRES_PASSWORD",
            "--entrypoint", "bash", IMAGE, "-c",
            "openssl req -x509 -newkey rsa:2048 -nodes -keyout /tmp/server.key "
            "-out /tmp/server.crt -days 1 -subj /CN=localhost >/dev/null 2>&1 && "
            "chmod 600 /tmp/server.key && "
            "exec docker-entrypoint.sh postgres -c ssl=on "
            "-c ssl_cert_file=/tmp/server.crt -c ssl_key_file=/tmp/server.key",
            env={"POSTGRES_PASSWORD": "test-only-root-password"},
        )
        for _ in range(60):
            if docker("exec", cls.name, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", check=False).returncode == 0:
                break
            time.sleep(1)
        else:
            raise AssertionError("Disposable PostgreSQL did not become ready")

    def sql(self, sql):
        return docker("exec", "-i", self.name, "psql", "-X", "-qAt", "-U", "postgres",
                      "-d", "leasebook", "-v", "ON_ERROR_STOP=1", stdin=sql).stdout.strip()

    def run_job(self, operation="bootstrap", **overrides):
        variables = {
            "PGHOST": HOST, "LEASEBOOK_CONFIRM_HOST": HOST,
            "PGUSER": "lbadmin", "PGPASSWORD": "test-only-admin-password",
            "LEASEBOOK_OPERATOR": "dbadmin-test", "LEASEBOOK_ORG_ID": ORG,
            "LEASEBOOK_MIGRATOR_PASSWORD": "test-only-migrator-'\\-password",
            "LEASEBOOK_APP_PASSWORD": "test-only-app-'\\-password",
            "LEASEBOOK_OPS_PASSWORD": "test-only-ops-'\\-password",
            **overrides,
        }
        args = ["run", "--rm", "--network", self.name]
        for name in variables:
            args += ["-e", name]
        result = docker(*args, IMAGE, operation, env=variables, check=False)
        for name, value in variables.items():
            if "PASSWORD" in name and value:
                self.assertNotIn(value, result.stdout + result.stderr)
        return result

    def test_bootstrap_replay_failure_and_scoped_restore_check(self):
        # The administrator owns the database and can create roles, but is NOT superuser.
        docker("exec", "-i", self.name, "psql", "-X", "-U", "postgres", "-v", "ON_ERROR_STOP=1",
               stdin="CREATE ROLE lbadmin LOGIN CREATEDB CREATEROLE PASSWORD 'test-only-admin-password';\n"
                     "CREATE DATABASE leasebook OWNER lbadmin;\n")
        self.sql("ALTER SYSTEM SET log_statement = 'all';\nSELECT pg_reload_conf();")
        with self.subTest("no implicit action, identity or target"):
            for operation, overrides in [
                ("refuse", {}), ("bootstrap", {"LEASEBOOK_OPERATOR": ""}),
                ("bootstrap", {"LEASEBOOK_CONFIRM_HOST": "wrong"}),
                ("bootstrap", {"LEASEBOOK_APP_PASSWORD": ""}),
                ("bootstrap", {"LEASEBOOK_APP_PASSWORD": "has\na-newline-password"}),
            ]:
                self.assertNotEqual(0, self.run_job(operation, **overrides).returncode)
            self.assertEqual("0", self.sql("SELECT count(*) FROM pg_roles WHERE rolname = 'leasebook_app';"))

        with self.subTest("initial non-superuser bootstrap"):
            result = self.run_job()
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("leasebook_app", self.sql(
                "SELECT pg_get_userbyid(nspowner) FROM pg_namespace WHERE nspname = 'hangfire';"))
            self.assertEqual("f|f|f", self.sql(
                "SELECT has_database_privilege('leasebook_app','leasebook','CREATE'), "
                "has_schema_privilege('leasebook_app','public','CREATE'), "
                "has_schema_privilege('leasebook_ops','public','CREATE');"))

        # Synthetic migrated tables exercise actual role grants/RLS without touching golden data.
        self.sql(f"""
SET ROLE leasebook_migrator;
CREATE TABLE orgs (id uuid PRIMARY KEY);
CREATE TABLE journal_entries (org_id uuid NOT NULL, entry_date date NOT NULL);
INSERT INTO orgs VALUES ('{ORG}');
INSERT INTO journal_entries VALUES ('{ORG}', '2026-09-09'),
 ('22222222-2222-2222-2222-222222222222', '2099-01-01');
ALTER TABLE journal_entries ENABLE ROW LEVEL SECURITY;
ALTER TABLE journal_entries FORCE ROW LEVEL SECURITY;
CREATE POLICY org_scope ON journal_entries USING (org_id = current_setting('app.org_id', true)::uuid);
REVOKE UPDATE, DELETE ON journal_entries FROM leasebook_app;
RESET ROLE;
""")
        with self.subTest("replay preserves append-only revocations"):
            result = self.run_job()
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertEqual("f|f", self.sql(
                "SELECT has_table_privilege('leasebook_app','journal_entries','UPDATE'), "
                "has_table_privilege('leasebook_app','journal_entries','DELETE');"))
            self.assertEqual("3", self.sql(
                "SELECT count(*) FROM pg_roles WHERE rolname IN ('leasebook_app','leasebook_migrator','leasebook_ops');"))

        with self.subTest("restore verification is scoped and uses ops only"):
            result = self.run_job("verify", PGPASSWORD="", LEASEBOOK_APP_PASSWORD="", LEASEBOOK_MIGRATOR_PASSWORD="")
            self.assertEqual(0, result.returncode, result.stdout + result.stderr)
            self.assertIn("leasebook_ops", result.stdout)
            self.assertIn("2026-09-09", result.stdout)
            self.assertNotIn("2099-01-01", result.stdout)
            self.assertNotEqual(0, self.run_job("verify", LEASEBOOK_ORG_ID="33333333-3333-3333-3333-333333333333").returncode)
            self.assertNotEqual(0, self.run_job("verify", LEASEBOOK_ORG_ID="").returncode)

        with self.subTest("permission failure rolls back password and schema changes"):
            before = self.sql("SELECT rolpassword FROM pg_authid WHERE rolname='leasebook_app';")
            self.sql("ALTER SCHEMA hangfire OWNER TO lbadmin;")
            result = self.run_job(LEASEBOOK_APP_PASSWORD="test-only-different-password")
            self.assertNotEqual(0, result.returncode)
            self.assertEqual(before, self.sql("SELECT rolpassword FROM pg_authid WHERE rolname='leasebook_app';"))
            self.sql("ALTER SCHEMA hangfire OWNER TO leasebook_app;")

        with self.subTest("server statement logging contains no cleartext role passwords"):
            logs = docker("logs", self.name).stdout + docker("logs", self.name).stderr
            for password in ["test-only-migrator-'\\-password", "test-only-app-'\\-password",
                             "test-only-ops-'\\-password", "test-only-different-password"]:
                self.assertNotIn(password, logs)


if __name__ == "__main__":
    unittest.main(verbosity=2)
