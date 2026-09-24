"""Exercise the production migrator's TLS preflight without Azure or real credentials.

Run: python scripts/test-migrator-tls-guard.py. Requires a POSIX shell.
"""

import os
from pathlib import Path
import shutil
import subprocess
import unittest


ROOT = Path(__file__).resolve().parents[1]
GUARD = ROOT / "infra" / "migrator" / "require-verified-postgres-tls.sh"
ENTRYPOINT = ROOT / "infra" / "migrator" / "entrypoint.sh"
SECRET = "test-only-schema-owner-password"


def shell():
    located = shutil.which("sh")
    if located:
        return located

    git = shutil.which("git")
    if git:
        bundled = Path(git).resolve().parents[1] / "bin" / "sh.exe"
        if bundled.exists():
            return str(bundled)

    raise FileNotFoundError("A POSIX shell is required to exercise the migrator entrypoint")


def run_script(script, *args, connection=None, required=None):
    env = os.environ.copy()
    env.pop("ConnectionStrings__Migrations", None)
    env.pop("LEASEBOOK_REQUIRE_VERIFIED_POSTGRES_TLS", None)
    if connection is not None:
        env["ConnectionStrings__Migrations"] = connection
    if required is not None:
        env["LEASEBOOK_REQUIRE_VERIFIED_POSTGRES_TLS"] = required

    result = subprocess.run(
        [shell(), script.as_posix(), *args],
        cwd=ROOT,
        env=env,
        text=True,
        capture_output=True,
        timeout=10,
    )
    output = result.stdout + result.stderr
    if connection and connection in output:
        raise AssertionError("the guard printed the connection string")
    if SECRET in output:
        raise AssertionError("the guard printed the schema-owner password")
    return result


class MigratorTlsGuardTests(unittest.TestCase):
    def test_certificate_verifying_modes_are_accepted(self):
        for mode in ("VerifyFull", "verify-full", "VerifyCA", "verify-ca"):
            with self.subTest(mode=mode):
                result = run_script(
                    GUARD,
                    connection=(
                        "Host=db;Database=leasebook;Username=leasebook_migrator;"
                        f"Password={SECRET};SSL Mode={mode}"
                    ),
                )
                self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_missing_or_non_verifying_mode_is_refused_without_printing_the_secret(self):
        connections = [
            None,
            "",
            f"Host=db;Password={SECRET}",
            f"Host=db;Password={SECRET};SSL Mode=Prefer",
            f"Host=db;Password={SECRET};SSL Mode=Require",
            f"Host=db;Password={SECRET};SSL Mode=Allow",
            f"Host=db;Password={SECRET};SSL Mode=Disable",
        ]
        for connection in connections:
            with self.subTest(connection=connection):
                result = run_script(GUARD, connection=connection)
                self.assertNotEqual(0, result.returncode)

    def test_duplicate_ssl_mode_is_refused_instead_of_guessing_which_value_wins(self):
        for modes in (
            "SSL Mode=VerifyFull;SSL Mode=Disable",
            "SSL Mode=VerifyCA;SSL Mode=VerifyFull",
        ):
            with self.subTest(modes=modes):
                result = run_script(
                    GUARD,
                    connection=f"Host=db;Password={SECRET};{modes}",
                )
                self.assertNotEqual(0, result.returncode)

    def test_entrypoint_checks_before_running_the_bundle_when_required(self):
        result = run_script(
            ENTRYPOINT,
            shell(),
            "-c",
            "exit 42",
            connection=f"Host=db;Password={SECRET};SSL Mode=Prefer",
            required="true",
        )

        self.assertNotEqual(42, result.returncode, "the bundle command ran before the TLS guard")
        self.assertNotEqual(0, result.returncode)

    def test_entrypoint_runs_after_a_valid_guard_and_local_mode_can_opt_out(self):
        for required, mode in (("true", "VerifyFull"), ("false", "Prefer")):
            with self.subTest(required=required):
                result = run_script(
                    ENTRYPOINT,
                    shell(),
                    "-c",
                    "exit 0",
                    connection=f"Host=db;Password={SECRET};SSL Mode={mode}",
                    required=required,
                )

                self.assertEqual(0, result.returncode, result.stdout + result.stderr)


if __name__ == "__main__":
    unittest.main(verbosity=2)
