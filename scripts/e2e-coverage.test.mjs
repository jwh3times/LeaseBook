import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import {
  journalQuery,
  parseConnection,
  parseJournalRows,
  paymentConnection,
  specsBypassingFixture,
} from "./e2e-coverage.mjs";

const scratch = [];
function specDir(files) {
  const directory = mkdtempSync(path.join(os.tmpdir(), "leasebook-specs-"));
  scratch.push(directory);
  for (const [name, source] of Object.entries(files)) {
    writeFileSync(path.join(directory, name), source);
  }
  return directory;
}
afterEach(() => {
  for (const directory of scratch.splice(0)) {
    rmSync(directory, { recursive: true, force: true });
  }
});

describe("specsBypassingFixture", () => {
  it("accepts a spec that takes test from ./test and only types or expect from Playwright", () => {
    const directory = specDir({
      "a.spec.ts":
        "import { expect, type Page } from '@playwright/test';\nimport { test } from './test';\n",
      "b.spec.ts":
        "import { type APIRequestContext, expect } from '@playwright/test';\nimport { test } from './test';\n",
    });
    assert.deepEqual(specsBypassingFixture(directory), []);
  });

  it("names a spec that imports test from Playwright, on one line or several", () => {
    const directory = specDir({
      "one-line.spec.ts": "import { expect, test } from '@playwright/test';\n",
      "several.spec.ts":
        'import {\n  expect,\n  test,\n  type Page,\n} from "@playwright/test";\n',
      "renamed.spec.ts": "import { test as base } from '@playwright/test';\n",
      "fine.spec.ts":
        "import { expect } from '@playwright/test';\nimport { test } from './test';\n",
    });
    assert.deepEqual(specsBypassingFixture(directory), [
      "one-line.spec.ts",
      "renamed.spec.ts",
      "several.spec.ts",
    ]);
  });

  it("looks at specs only: the fixture itself has to import Playwright's test", () => {
    const directory = specDir({
      "test.ts": "import { test as base } from '@playwright/test';\n",
      "helpers.ts": "import { expect, type Page } from '@playwright/test';\n",
    });
    assert.deepEqual(specsBypassingFixture(directory), []);
  });

  it("finds none in the repository's own specs", () => {
    const e2e = path.resolve(
      path.dirname(fileURLToPath(import.meta.url)),
      "..",
      "web",
      "e2e",
    );
    assert.deepEqual(specsBypassingFixture(e2e), []);
  });
});

describe("paymentConnection", () => {
  it("swaps the database and keeps the rest of the connection string", () => {
    assert.equal(
      paymentConnection(
        "Host=localhost;Port=5432;Database=leasebook;Username=leasebook_app;Password=dev_app_pw",
      ),
      "Host=localhost;Port=5432;Database=leasebook_payment_fixture;Username=leasebook_app;Password=dev_app_pw",
    );
  });

  it("refuses a connection string that names no database", () => {
    assert.throws(
      () => paymentConnection("Host=localhost;Port=5432"),
      /names no database/,
    );
  });
});

describe("parseConnection", () => {
  it("reads the parts psql needs, whatever the case of the keys", () => {
    const parts = parseConnection(
      "Host=localhost;Port=5432;Database=leasebook;username=leasebook_app;Password=dev_app_pw",
    );
    assert.equal(parts.database, "leasebook");
    assert.equal(parts.username, "leasebook_app");
    assert.equal(parts.password, "dev_app_pw");
  });

  it("refuses a connection string with no user", () => {
    assert.throws(
      () => parseConnection("Host=localhost;Database=leasebook;Password=x"),
      /names no username/,
    );
  });
});

describe("journalQuery", () => {
  const org = "01923000-0000-7000-8000-0000000d3110";

  it("sets the organization and the persona inside each read-only transaction", () => {
    const sql = journalQuery(
      [org, org.replace("d3110", "d3111")],
      1700000000000,
    );
    assert.equal(sql.match(/BEGIN TRANSACTION READ ONLY;/g).length, 2);
    assert.equal(sql.match(/COMMIT;/g).length, 2);
    assert.match(
      sql,
      /set_config\('app\.org_id', '01923000-0000-7000-8000-0000000d3110', true\), set_config\('app\.persona', 'staff', true\)/,
    );
    assert.match(sql, /posted_at >= to_timestamp\(1700000000000 \/ 1000\.0\)/);
  });

  it("refuses anything that is not an organization id or a time", () => {
    assert.throws(
      () => journalQuery(["x'; DROP TABLE orgs; --"], 1),
      /Not an organization id/,
    );
    assert.throws(() => journalQuery([org], "now()"), /not a time/);
  });
});

describe("parseJournalRows", () => {
  it("keeps the entry lines and drops what set_config and COMMIT print", () => {
    const output =
      "BEGIN\n01923000-0000-7000-8000-0000000d3110|staff\nentry|PaymentReceived|1700000000123\nentry|EntryVoided|1700000000456\nCOMMIT\n";
    assert.deepEqual(parseJournalRows(output, "main"), [
      { pass: "main", eventType: "PaymentReceived", postedAt: 1700000000123 },
      { pass: "main", eventType: "EntryVoided", postedAt: 1700000000456 },
    ]);
  });
});
