import assert from "node:assert/strict";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { paymentConnection, specsBypassingFixture } from "./e2e-coverage.mjs";

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
