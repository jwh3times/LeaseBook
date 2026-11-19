import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { annotation, createScanner, parseArgs } from "./e2e-server-crash.mjs";

const SCRIPTS = path.dirname(fileURLToPath(import.meta.url));
const WRAPPER = path.join(SCRIPTS, "e2e-server-crash.mjs");
const WORKFLOWS = path.join(SCRIPTS, "..", ".github", "workflows");

const ABORT =
  "[WebServer] abort: Lazy deopt after a fast API call with return value is unsupported";
const REFUSED =
  "Error: page.goto: net::ERR_CONNECTION_REFUSED at http://localhost:5373/login";

/** Runs the wrapper around a Node child whose body is `source`. */
function wrap(source) {
  return spawnSync(
    process.execPath,
    [WRAPPER, "--", process.execPath, "-e", source],
    { encoding: "utf8" },
  );
}

/** The workflow commands in a wrapper's stdout: lines GitHub reads as `::error`. */
function annotationsIn(stdout) {
  return stdout.split("\n").filter((line) => line.startsWith("::error"));
}

function scan(lines) {
  const scanner = createScanner();
  for (const line of lines) scanner.feed(line);
  return scanner.crash();
}

describe("finding a crashed dev server in Playwright's output", () => {
  it("names the V8 abort the web server printed", () => {
    const crash = scan([
      "Running 166 tests using 1 worker",
      "··········",
      ABORT,
      "[WebServer] ",
      "[WebServer] ==== JS stack trace =========================================",
    ]);

    assert.deepEqual(crash, { kind: "abort", line: ABORT });
  });

  it("names a server that went away without a word, from the refused connections to it", () => {
    const crash = scan([
      "  12) [chromium] › e2e/ledger.spec.ts:20:3 › records a payment",
      `    ${REFUSED}`,
      `    ${REFUSED}`,
    ]);

    assert.deepEqual(crash, { kind: "unreachable", line: REFUSED });
  });

  it("reads through color codes, and quotes the line without them", () => {
    const crash = scan([
      "\u001b[2m[WebServer] \u001b[22mabort: Lazy deopt after a fast API call with return value is unsupported",
    ]);

    assert.deepEqual(crash, { kind: "abort", line: ABORT });
  });

  it("names V8's crash dump from the web server when no abort line came before it", () => {
    const crash = scan([
      "[WebServer] ",
      "[WebServer] ==== JS stack trace =========================================",
      "[WebServer]     0: ExitFrame [pc: 0x70a9bce3ea76]",
    ]);

    assert.deepEqual(crash, {
      kind: "crash-dump",
      line: "[WebServer] ==== JS stack trace =========================================",
    });
  });

  it("names nothing in a run whose server stayed up", () => {
    const green = [
      "Running 166 tests using 1 worker",
      "[WebServer] [vite] connected.",
      "  166 passed (6.1m)",
    ];
    const ordinaryFailure = [
      "  1) [chromium] › e2e/owners.spec.ts:8:3 › shows ending balances",
      "    Error: expect(locator).toHaveText(expected) failed",
      '    Expected: "$1,250.00"',
      "  1 failed",
    ];
    const someOtherServerRefused = [
      "    Error: apiRequestContext.get: connect ECONNREFUSED 127.0.0.1:4010",
      "    Error: page.goto: net::ERR_CONNECTION_REFUSED at http://localhost:5080/api/health",
      "    Error: page.goto: net::ERR_CONNECTION_REFUSED at http://localhost:53730/login",
    ];

    assert.equal(scan(green), null);
    assert.equal(scan(ordinaryFailure), null);
    assert.equal(scan(someOtherServerRefused), null);
  });
});

describe("the annotation", () => {
  it("says a web server crashed without guessing which, what the failures after it are, and quotes the line", () => {
    // Playwright prefixes every web server it starts with `[WebServer]`: the SPA dev server, the
    // API host and, in the stand-in pass, the Stripe stand-in. An abort line does not say which.
    assert.equal(
      annotation({ kind: "abort", line: ABORT }),
      "::error title=E2E web server crashed::A web server Playwright started for this run " +
        "crashed mid-run. The test failures after it are connection-refused fallout, not product " +
        "regressions: re-run the job. First sign: " +
        ABORT,
    );
  });

  it("names the SPA dev server when it is the one that stopped answering", () => {
    assert.equal(
      annotation({ kind: "unreachable", line: REFUSED }),
      "::error title=E2E web server crashed::The SPA dev server stopped answering mid-run " +
        "without printing why. The test failures after it are connection-refused fallout, " +
        "not product regressions: re-run the job. First sign: " +
        REFUSED,
    );
  });

  it("escapes what would end or split the workflow command", () => {
    const text = annotation({
      kind: "abort",
      line: "[WebServer] abort: 100% gone\r\nnext",
    });

    assert.ok(
      text.endsWith("First sign: [WebServer] abort: 100%25 gone%0D%0Anext"),
      text,
    );
    assert.equal(text.split("\n").length, 1);
  });
});

describe("the wrapper around a Playwright run", () => {
  it("passes the run's output through, keeps its exit code, and adds one annotation for a crash", () => {
    const result = wrap(`
      console.log("Running 166 tests using 1 worker");
      console.error(${JSON.stringify(ABORT)});
      console.log(${JSON.stringify(`    ${REFUSED}`)});
      console.log("  25 failed");
      process.exit(3);
    `);

    assert.equal(result.status, 3);
    assert.ok(
      result.stdout.startsWith(
        `Running 166 tests using 1 worker\n    ${REFUSED}\n  25 failed\n`,
      ),
      result.stdout,
    );
    assert.equal(result.stderr, `${ABORT}\n`);
    assert.deepEqual(annotationsIn(result.stdout), [
      annotation({ kind: "abort", line: ABORT }),
    ]);
  });

  it("names the abort in the real run that motivated it (#798, run 44260108109)", () => {
    const fixture = path.join(
      SCRIPTS,
      "..",
      "tests",
      "fixtures",
      "e2e",
      "server-crash-run-44260108109.log",
    );
    const result = wrap(`
      process.stdout.write(require("node:fs").readFileSync(${JSON.stringify(fixture)}));
      process.exitCode = 1;
    `);

    assert.equal(result.status, 1);
    assert.deepEqual(annotationsIn(result.stdout), [
      annotation({ kind: "abort", line: ABORT }),
    ]);
  });

  it("names a server that exited silently mid-run, in the shape a real one left", () => {
    // The shape of a local run on 2026-11-18: the real Vite dev server under real Playwright,
    // exiting with no output after the first test.
    const result = wrap(`
      console.log("Running 2 tests using 1 worker");
      console.log("  ok 1 dies.spec.mjs:9:1 › the first page loads while the server is up (1.3s)");
      console.log("  x  2 dies.spec.mjs:15:1 › a later page, after the server has gone (4.2s)");
      console.log(${JSON.stringify(`    ${REFUSED}`)});
      console.log("  1 failed");
      console.log("  1 passed (10.8s)");
      process.exit(1);
    `);

    assert.equal(result.status, 1);
    assert.deepEqual(annotationsIn(result.stdout), [
      annotation({ kind: "unreachable", line: REFUSED }),
    ]);
  });

  it("starts the annotation on a line of its own when the run's output ended mid-line", () => {
    const result = wrap(`
      console.error(${JSON.stringify(ABORT)});
      process.stdout.write("  25 failed");
      process.exitCode = 1;
    `);

    assert.equal(result.status, 1);
    assert.ok(result.stdout.startsWith("  25 failed\n"), result.stdout);
    assert.deepEqual(annotationsIn(result.stdout), [
      annotation({ kind: "abort", line: ABORT }),
    ]);
  });

  it("adds nothing to a run whose server stayed up, whether it passed or failed", () => {
    const passed = wrap(`console.log("  166 passed (6.1m)");`);
    const failed = wrap(`console.log("  1 failed"); process.exit(1);`);

    assert.equal(passed.status, 0);
    assert.equal(passed.stdout, "  166 passed (6.1m)\n");
    assert.equal(failed.status, 1);
    assert.equal(failed.stdout, "  1 failed\n");
  });

  it("fails with 1 when the command cannot be started", () => {
    const result = spawnSync(
      process.execPath,
      [WRAPPER, "--", "leasebook-no-such-command-798"],
      { encoding: "utf8", timeout: 30_000 },
    );

    assert.equal(result.status, 1);
    assert.match(
      result.stderr,
      /could not start leasebook-no-such-command-798/,
    );
  });

  it("fails with 2 on a usage error, starting nothing", () => {
    for (const args of [
      [],
      ["--"],
      ["--spa", "http://localhost:5373", "--", "x"],
    ]) {
      const result = spawnSync(process.execPath, [WRAPPER, ...args], {
        encoding: "utf8",
      });
      assert.equal(result.status, 2, JSON.stringify(args));
      assert.match(result.stderr, /usage: /);
    }
  });

  it("gives the command every argument after the first --, a later -- included", () => {
    assert.deepEqual(
      parseArgs(["--", "npm", "run", "e2e:coverage", "--", "--payments"]),
      { command: ["npm", "run", "e2e:coverage", "--", "--payments"] },
    );
  });
});

describe("the workflows", () => {
  /** A workflow's text without its comments (as `script-tests-listed.test.mjs` reads them). */
  function withoutComments(workflow) {
    return workflow.replace(/(^|\s)#.*$/gm, "$1");
  }

  /**
   * The lines of the workflows that start a Playwright test run: a one-line `run:` or a line inside
   * a `run: |` block alike. `npx playwright install` is not one.
   */
  function playwrightRuns() {
    const runs = [];
    for (const file of readdirSync(WORKFLOWS).filter((f) =>
      /\.ya?ml$/.test(f),
    )) {
      const text = withoutComments(
        readFileSync(path.join(WORKFLOWS, file), "utf8"),
      );
      for (const line of text.split("\n")) {
        if (/\bplaywright test\b|\bnpm run e2e\b/.test(line)) {
          runs.push({ file, line: line.trim() });
        }
      }
    }
    return runs;
  }

  it("run every Playwright test run through the wrapper", () => {
    const runs = playwrightRuns();
    // The two required e2e jobs alone hold three; finding none means the scan itself broke.
    assert.ok(runs.length >= 3, JSON.stringify(runs));
    const bare = runs.filter(
      (run) =>
        !/^(run: )?node \.\.\/scripts\/e2e-server-crash\.mjs -- /.test(
          run.line,
        ),
    );
    assert.deepEqual(bare, []);
  });
});
