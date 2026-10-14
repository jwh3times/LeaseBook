import assert from "node:assert/strict";
import {
  existsSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import {
  journalQuery,
  listed,
  parseConnection,
  parseJournalRows,
  paymentConnection,
  planPasses,
  playwright,
  specsBypassingFixture,
  standInConnection,
  withDatabase,
} from "./e2e-coverage.mjs";
import { PASSES, verifyRun } from "./scenario-catalogue.mjs";

const webRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "web",
);

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
    assert.deepEqual(specsBypassingFixture(path.join(webRoot, "e2e")), []);
  });
});

const MAIN =
  "Host=localhost;Port=5432;Database=leasebook;Username=leasebook_app;Password=dev_app_pw";

describe("withDatabase", () => {
  it("swaps the database whatever the case of the key, and keeps the rest", () => {
    assert.equal(
      withDatabase("Host=h;database=leasebook;Username=u", "other"),
      "Host=h;database=other;Username=u",
    );
  });

  it("refuses a connection string that names no database", () => {
    assert.throws(() => withDatabase("Host=h", "other"), /names no database/);
  });
});

describe("standInConnection", () => {
  it("names the stand-in fixture's database and keeps the rest", () => {
    assert.equal(
      standInConnection(MAIN),
      "Host=localhost;Port=5432;Database=leasebook_payment_fixture_standin;Username=leasebook_app;Password=dev_app_pw",
    );
  });
});

describe("planPasses", () => {
  const root = path.join(path.sep, "repo");
  const plan = (argv, env = {}) =>
    planPasses(argv, { mainConnection: MAIN, env, root });

  it("runs the main pass alone, over every spec, on the main database", () => {
    const passes = plan([]);
    assert.deepEqual(
      passes.map((pass) => pass.name),
      ["main"],
    );
    assert.deepEqual(passes[0].specs, []);
    assert.equal(passes[0].connection, MAIN);
    assert.deepEqual(passes[0].env, {});
    assert.equal(passes[0].manifest, undefined);
  });

  it("adds the stand-in pass with --stand-in, on its own database, manifest and report", () => {
    const passes = plan(["--stand-in"]);
    assert.deepEqual(
      passes.map((pass) => pass.name),
      ["main", "stand-in"],
    );
    const standIn = passes[1];
    assert.deepEqual(standIn.specs, ["stripe-stand-in.spec.ts"]);
    assert.equal(standIn.connection, standInConnection(MAIN));
    assert.equal(
      standIn.manifest,
      path.join(root, "payment-fixture-standin.local"),
    );
    assert.deepEqual(standIn.env, {
      STRIPE_STAND_IN_E2E: "1",
      Payments__ManifestPath: path.join(root, "payment-fixture-standin.local"),
      ConnectionStrings__Default: standInConnection(MAIN),
      PLAYWRIGHT_HTML_OUTPUT_DIR: "playwright-report-stand-in",
    });
  });

  it("keeps the simulated-payment pass as it was with --payments", () => {
    const [, payments] = plan(["--payments"]);
    assert.equal(payments.name, "payments");
    assert.deepEqual(payments.specs, ["simulated-payment.spec.ts"]);
    assert.deepEqual(payments.env, {
      PAYMENT_SIMULATION_E2E: "1",
      Payments__ManifestPath: path.join(root, "payment-fixture.local"),
      ConnectionStrings__Default: paymentConnection(MAIN),
      PLAYWRIGHT_HTML_OUTPUT_DIR: "playwright-report-payments",
    });
  });

  it("runs main, payments, then stand-in, whatever order the flags come in, and each once", () => {
    for (const argv of [
      ["--payments", "--stand-in"],
      ["--stand-in", "--payments"],
      ["--stand-in", "--payments", "--stand-in"],
    ]) {
      assert.deepEqual(
        plan(argv).map((pass) => pass.name),
        ["main", "payments", "stand-in"],
      );
    }
  });

  it("gives the payment manifest a caller names to the simulated-payment pass only", () => {
    const [, payments, standIn] = plan(["--payments", "--stand-in"], {
      Payments__ManifestPath: "/elsewhere/payment-fixture.local",
    });
    assert.equal(payments.manifest, "/elsewhere/payment-fixture.local");
    assert.equal(
      payments.env.Payments__ManifestPath,
      "/elsewhere/payment-fixture.local",
    );
    assert.equal(
      standIn.env.Payments__ManifestPath,
      path.join(root, "payment-fixture-standin.local"),
    );
  });

  it("keeps each fixture pass's switch out of the other", () => {
    const [main, payments, standIn] = plan(["--payments", "--stand-in"]);
    assert.equal(main.env.STRIPE_STAND_IN_E2E, undefined);
    assert.equal(main.env.PAYMENT_SIMULATION_E2E, undefined);
    assert.equal(payments.env.STRIPE_STAND_IN_E2E, undefined);
    assert.equal(standIn.env.PAYMENT_SIMULATION_E2E, undefined);
  });

  it("names only passes the scenario check knows", () => {
    for (const pass of plan(["--payments", "--stand-in"])) {
      assert.ok(PASSES.includes(pass.name), pass.name);
    }
  });

  it("refuses an argument it does not know, and names both flags", () => {
    assert.throws(
      () => plan(["--standin"]),
      /Unknown argument: --standin\. Usage: e2e-coverage\.mjs \[--payments\] \[--stand-in\]/,
    );
  });
});

describe("listed", () => {
  it("joins one, two or three names", () => {
    assert.equal(listed(["main"]), "main");
    assert.equal(listed(["main", "stand-in"]), "main and stand-in");
    assert.equal(
      listed(["main", "payments", "stand-in"]),
      "main, payments and stand-in",
    );
  });
});

/**
 * A real Playwright run, with no browser and no host, of a spec shaped like the stand-in's expected
 * failure: setup that must pass, then `test.fail()`, then the intended outcome. An automatic fixture
 * records each test's status in its teardown, which is where `web/e2e/coverage-fixture.ts` records
 * the status the scenario check reads.
 */
describe("an expected failure, from Playwright through the scenario check", () => {
  const havePlaywright = existsSync(
    path.join(webRoot, "node_modules", "@playwright", "test"),
  );

  function runSpec(intended) {
    const directory = mkdtempSync(path.join(os.tmpdir(), "leasebook-xfail-"));
    scratch.push(directory);
    symlinkSync(
      path.join(webRoot, "node_modules"),
      path.join(directory, "node_modules"),
      "dir",
    );
    writeFileSync(
      path.join(directory, "playwright.config.mjs"),
      "import { defineConfig } from '@playwright/test';\n" +
        "export default defineConfig({ testDir: '.', reporter: 'dot', workers: 1, retries: 2 });\n",
    );
    writeFileSync(
      path.join(directory, "limit.spec.mjs"),
      [
        "import { writeFileSync } from 'node:fs';",
        "import path from 'node:path';",
        "import { test as base, expect } from '@playwright/test';",
        "const test = base.extend({",
        "  record: [async ({}, use, testInfo) => {",
        "    const startedAt = Date.now();",
        "    await use();",
        "    writeFileSync(",
        "      path.join(import.meta.dirname, `record-${testInfo.retry}-${testInfo.title.split(' ')[0]}.json`),",
        "      JSON.stringify({ pass: 'stand-in', tags: testInfo.tags, status: testInfo.status, startedAt, endedAt: Date.now() }),",
        "    );",
        "  }, { auto: true }],",
        "});",
        "test('setup @scenario:a-working-flow', () => { expect(1).toBe(1); });",
        "test('limit @scenario:a-known-limit @expected-failure', () => {",
        "  expect(1).toBe(1);",
        "  test.fail(true, 'a known limit');",
        `  expect(${intended}).toBe(true);`,
        "});",
      ].join("\n"),
    );
    const status = playwright(
      ["--config", path.join(directory, "playwright.config.mjs")],
      {},
      { stdio: "ignore" },
    );
    const tests = readdirSync(directory)
      .filter((name) => name.startsWith("record-"))
      .map((name) => JSON.parse(readFileSync(path.join(directory, name))));
    return { status, tests };
  }

  const catalogue = {
    scenarios: [
      {
        id: "a-working-flow",
        pass: "stand-in",
        events: [],
        routes: [],
      },
      {
        id: "a-known-limit",
        pass: "stand-in",
        events: [],
        routes: [],
        expectedFailure: "A known limit.",
      },
    ],
  };
  const verify = (tests) =>
    verifyRun({
      catalogue,
      tests,
      postings: [],
      routes: [],
      passes: ["main", "stand-in"],
      knownEvents: [],
    });

  it(
    "succeeds as a pass, and is reported as an expected failure, when the intended outcome fails",
    { skip: !havePlaywright && "web dependencies are not installed" },
    () => {
      const { status, tests } = runSpec(false);
      assert.equal(status, 0);
      // Not retried: Playwright retries only an unexpected outcome.
      assert.deepEqual(tests.map((test) => test.status).sort(), [
        "failed",
        "passed",
      ]);
      const { rows, problems } = verify(tests);
      assert.deepEqual(problems, []);
      assert.deepEqual(
        rows.map((row) => [row.id, row.state]),
        [
          ["a-working-flow", "verified"],
          ["a-known-limit", "expected failure"],
        ],
      );
    },
  );

  it(
    "fails the pass and the scenario check when the intended outcome holds",
    { skip: !havePlaywright && "web dependencies are not installed" },
    () => {
      const { status, tests } = runSpec(true);
      assert.notEqual(status, 0);
      const { rows, problems } = verify(tests);
      assert.equal(rows[1].state, "failed");
      assert.match(problems.join("\n"), /a test of it passed/);
    },
  );
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
  it("reads the database, whatever the case of the keys", () => {
    const parts = parseConnection(
      "Host=localhost;Port=5432;database=leasebook;Username=leasebook_app;Password=dev_app_pw",
    );
    assert.equal(parts.database, "leasebook");
  });

  it("refuses a connection string with no database", () => {
    assert.throws(
      () => parseConnection("Host=localhost;Username=x;Password=x"),
      /names no database/,
    );
  });
});

describe("journalQuery", () => {
  it("reads in one read-only transaction and sets no organization context", () => {
    const sql = journalQuery(1700000000000);
    assert.equal(sql.match(/BEGIN TRANSACTION READ ONLY;/g).length, 1);
    assert.equal(sql.match(/COMMIT;/g).length, 1);
    assert.doesNotMatch(sql, /set_config|app\.(org_id|persona|user_id)/);
    assert.match(sql, /posted_at >= to_timestamp\(1700000000000 \/ 1000\.0\)/);
  });

  it("refuses anything that is not a time", () => {
    assert.throws(() => journalQuery("now()"), /not a time/);
  });
});

describe("parseJournalRows", () => {
  it("keeps the entry lines and drops what BEGIN and COMMIT print", () => {
    const output =
      "BEGIN\nentry|PaymentReceived|1700000000123\nentry|EntryVoided|1700000000456\nCOMMIT\n";
    assert.deepEqual(parseJournalRows(output, "main"), [
      { pass: "main", eventType: "PaymentReceived", postedAt: 1700000000123 },
      { pass: "main", eventType: "EntryVoided", postedAt: 1700000000456 },
    ]);
  });
});
