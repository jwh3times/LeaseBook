import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import {
  COPY,
  Refusal,
  TEMPLATE,
  assertOwnDatabase,
  bootstrapSchema,
  commentSql,
  connectionString,
  copySql,
  dotnetEnvironment,
  dropSql,
  fingerprint,
  needsBuild,
  parseArgs,
  sourceFiles,
} from "./e2e-db.mjs";

const scratch = [];
function tree(files) {
  const root = mkdtempSync(path.join(os.tmpdir(), "leasebook-e2e-db-"));
  scratch.push(root);
  for (const [name, content] of Object.entries(files)) {
    mkdirSync(path.dirname(path.join(root, name)), { recursive: true });
    writeFileSync(path.join(root, name), content);
  }
  return root;
}
afterEach(() => {
  for (const root of scratch.splice(0)) {
    rmSync(root, { recursive: true, force: true });
  }
});

describe("the databases it touches", () => {
  it("are the template and the copy, and no other", () => {
    assert.equal(assertOwnDatabase(TEMPLATE), TEMPLATE);
    assert.equal(assertOwnDatabase(COPY), COPY);
    for (const other of [
      "leasebook",
      "postgres",
      "leasebook_payment_fixture",
      "leasebook_payment_fixture_stripe",
      "leasebook_sandbox_run_0123abcd",
      "leasebook_e2e; DROP DATABASE leasebook",
    ]) {
      assert.throws(() => assertOwnDatabase(other), Refusal);
      assert.throws(() => dropSql(other), Refusal);
      assert.throws(() => connectionString(other, "leasebook_app"), Refusal);
    }
  });

  it("drops only by those names, and copies the copy from the template", () => {
    assert.equal(dropSql(COPY), "DROP DATABASE IF EXISTS leasebook_e2e;");
    assert.equal(
      dropSql(TEMPLATE),
      "DROP DATABASE IF EXISTS leasebook_e2e_template;",
    );
    assert.equal(
      copySql(),
      "CREATE DATABASE leasebook_e2e TEMPLATE leasebook_e2e_template OWNER leasebook_migrator;",
    );
  });

  it("connects as the development roles on the development port", () => {
    assert.equal(
      connectionString(COPY, "leasebook_app"),
      "Host=localhost;Port=5632;Database=leasebook_e2e;Username=leasebook_app;Password=dev_app_pw",
    );
    assert.equal(
      connectionString(TEMPLATE, "leasebook_migrator"),
      "Host=localhost;Port=5632;Database=leasebook_e2e_template;Username=leasebook_migrator;Password=dev_migrator_pw",
    );
    assert.throws(() => connectionString(COPY, "postgres"));
  });
});

describe("bootstrapSchema", () => {
  it("keeps what follows the connect, and leaves the roles and the database out", () => {
    const schema = bootstrapSchema(
      "CREATE ROLE leasebook_app;\nCREATE DATABASE leasebook;\n\\connect leasebook\nGRANT USAGE ON SCHEMA public TO leasebook_app;\n",
    );
    assert.equal(
      schema.trim(),
      "GRANT USAGE ON SCHEMA public TO leasebook_app;",
    );
  });

  it("refuses SQL with no per-database section", () => {
    assert.throws(() => bootstrapSchema("CREATE ROLE x;"), /no `\\connect/);
  });
});

describe("fingerprint", () => {
  const files = {
    "infra/db/bootstrap.sql": "grants",
    "src/Migrations/1_Init.cs": "init",
    "src/Seeding/DemoSeeder.cs": "demo",
  };
  const paths = ["infra/db/bootstrap.sql", "src/Migrations", "src/Seeding"];

  it("lists every file under the sources, sorted, with forward slashes", () => {
    assert.deepEqual(sourceFiles(tree(files), paths), [
      "infra/db/bootstrap.sql",
      "src/Migrations/1_Init.cs",
      "src/Seeding/DemoSeeder.cs",
    ]);
  });

  it("is the same for the same sources, whatever the line endings", () => {
    const unix = tree({ ...files, "src/Seeding/DemoSeeder.cs": "a\nb\n" });
    const windows = tree({
      ...files,
      "src/Seeding/DemoSeeder.cs": "a\r\nb\r\n",
    });
    assert.equal(fingerprint(unix, paths), fingerprint(windows, paths));
  });

  it("changes with a new migration, an edited seeder or a renamed file", () => {
    const base = fingerprint(tree(files), paths);
    assert.notEqual(
      fingerprint(
        tree({ ...files, "src/Migrations/2_Next.cs": "next" }),
        paths,
      ),
      base,
    );
    assert.notEqual(
      fingerprint(
        tree({ ...files, "src/Seeding/DemoSeeder.cs": "demo2" }),
        paths,
      ),
      base,
    );
    const renamed = { ...files };
    delete renamed["src/Seeding/DemoSeeder.cs"];
    renamed["src/Seeding/Demo.cs"] = "demo";
    assert.notEqual(fingerprint(tree(renamed), paths), base);
  });
});

describe("needsBuild", () => {
  const record = commentSql({ fingerprint: "abc", builtAt: "now" });

  it("builds when there is no template, or it was built from other sources", () => {
    assert.equal(needsBuild("", "abc"), true);
    assert.equal(
      needsBuild(JSON.stringify({ fingerprint: "old" }), "abc"),
      true,
    );
    assert.equal(needsBuild("not json", "abc"), true);
  });

  it("reuses a template built from the same sources, unless asked to rebuild", () => {
    const comment = JSON.stringify({ fingerprint: "abc", builtAt: "now" });
    assert.equal(needsBuild(comment, "abc"), false);
    assert.equal(needsBuild(comment, "abc", { rebuild: true }), true);
  });

  it("records the fingerprint as the template's comment, quoted", () => {
    assert.equal(
      record,
      `COMMENT ON DATABASE leasebook_e2e_template IS '{"fingerprint":"abc","builtAt":"now"}';`,
    );
    assert.match(
      commentSql({ fingerprint: "it's" }),
      /'\{"fingerprint":"it''s"\}'/,
    );
  });
});

describe("dotnetEnvironment", () => {
  it("drops payment, connection and pass settings, and sets the given connections", () => {
    const env = dotnetEnvironment(
      {
        PATH: "/bin",
        ConnectionStrings__Default: "Database=leasebook",
        connectionstrings__migrations: "Database=leasebook",
        Payments__Mode: "StripeSandbox",
        Payments__Stripe__SecretKey: "secret",
        STRIPE_STAND_IN_E2E: "1",
        E2E_COVERAGE: "1",
      },
      { ConnectionStrings__Default: "Database=leasebook_e2e_template" },
    );
    assert.deepEqual(env, {
      PATH: "/bin",
      ASPNETCORE_ENVIRONMENT: "Development",
      ConnectionStrings__Default: "Database=leasebook_e2e_template",
    });
  });
});

describe("parseArgs", () => {
  it("passes everything after run to Playwright", () => {
    assert.deepEqual(parseArgs(["run", "a11y.spec.ts", "--headed"]), {
      command: "run",
      playwright: ["a11y.spec.ts", "--headed"],
    });
  });

  it("takes --rebuild on reset, and nothing else", () => {
    assert.deepEqual(parseArgs(["reset"]), {
      command: "reset",
      rebuild: false,
    });
    assert.deepEqual(parseArgs(["reset", "--rebuild"]), {
      command: "reset",
      rebuild: true,
    });
    assert.throws(() => parseArgs(["reset", "--force"]), Refusal);
  });

  it("refuses an unknown or missing command", () => {
    assert.throws(() => parseArgs([]), Refusal);
    assert.throws(() => parseArgs(["seed"]), Refusal);
    assert.throws(() => parseArgs(["drop", "leasebook"]), Refusal);
  });
});
