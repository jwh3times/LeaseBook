#!/usr/bin/env node

/**
 * Runs the Playwright suite with coverage and writes what it reached (ADR-055,
 * docs/testing/coverage.md): the host's code by module, the web source by area, and the API routes.
 *
 * Usage (from `web/`):
 *
 *   npm run e2e:coverage                              # the main pass
 *   npm run e2e:coverage -- --payments                # and the simulated-payment pass
 *   npm run e2e:coverage -- --stand-in                # and the Stripe stand-in pass
 *   npm run e2e:coverage -- --payments --stand-in     # all three
 *
 * The database is the one `npm run e2e` uses, and it must be seeded the same way (demo, cutover,
 * portal and scenario). `--payments` also needs the payment fixture `scripts/payment-fixture.ps1
 * init` creates, and `--stand-in` the stand-in fixture `scripts/payment-fixture.ps1 init -StandIn`
 * creates. Each fixture pass runs on its own database, named from the main connection string with
 * the database swapped, and its own manifest at the repository root. The journal is read through
 * `psql` inside the Postgres container, which is `leasebook-db` unless E2E_DB_CONTAINER names
 * another.
 *
 * The stand-in pass runs `stripe-stand-in.spec.ts` with STRIPE_STAND_IN_E2E=1, so
 * `playwright.config.ts` starts `scripts/stripe-stand-in.mjs` on loopback before the host, and the
 * host runs in `StripeSandbox` mode against it (ADR-054). It makes no call to Stripe.
 *
 * What it does, in order:
 *
 *   1. Refuses to start if a spec takes `test` from '@playwright/test' and not from `./test`: that
 *      spec would run unrecorded and the figures would be short without saying so.
 *   2. Starts a `dotnet-coverage` collector in server mode. `playwright.config.ts` starts the host
 *      as a client of that session when E2E_COVERAGE=1, so one collector spans every host the run
 *      starts, and the host can stop without ending the collection.
 *   3. Runs Playwright with E2E_COVERAGE=1, once for each pass.
 *   4. Shuts the collector down, which is what writes the host's coverage file.
 *   5. Builds the reports under `web/coverage/e2e/` and joins their summaries into `summary.md`.
 *   6. Reads the journal entries each pass posted, and checks every scenario in the catalogue
 *      against what its tests did (ADR-056, docs/testing/scenarios.md).
 *   7. Runs `check-invariants --all` on each database a pass used, so every organization a
 *      scenario drove is checked after the last scenario has finished with it.
 *
 * A failing test fails this script, after the reports are written. A test marked with
 * `test.fail()` that fails does not: Playwright counts it as the expected outcome and exits 0, and
 * the scenario check reports its scenario as an expected failure. One that passes does fail it,
 * through both. A report that cannot be produced fails it. So does a scenario that did not produce
 * what it declares, and so does a violated invariant. A coverage figure never does.
 */

import { spawn, spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const webRoot = path.join(repoRoot, "web");
const outputRoot = path.join(webRoot, "coverage", "e2e");
const hostDir = path.join(outputRoot, "host");
const hostCoverage = path.join(hostDir, "host.cobertura.xml");
const routesDir = path.join(outputRoot, "routes");
const journalDir = path.join(outputRoot, "journal");
const scenariosDir = path.join(outputRoot, "scenarios");
const openApiDocument = path.join(
  repoRoot,
  "src",
  "LeaseBook.Web",
  "obj",
  "openapi",
  "LeaseBook.Web.json",
);

const DEFAULT_CONNECTION =
  "Host=localhost;Port=5632;Database=leasebook;Username=leasebook_app;Password=dev_app_pw";
const PAYMENT_DATABASE = "leasebook_payment_fixture";
const STAND_IN_DATABASE = "leasebook_payment_fixture_standin";
const USAGE = "Usage: e2e-coverage.mjs [--payments] [--stand-in]";

/**
 * The spec files that import `test` straight from '@playwright/test'. They would run without the
 * recording fixture.
 */
export function specsBypassingFixture(specDir) {
  return readdirSync(specDir)
    .filter((name) => name.endsWith(".spec.ts"))
    .filter((name) => {
      const source = readFileSync(path.join(specDir, name), "utf8");
      const imports = source.matchAll(
        /import\s*(?:type\s*)?\{([^}]*)\}\s*from\s*['"]@playwright\/test['"]/g,
      );
      for (const [, names] of imports) {
        const bound = names.split(",").map((entry) => entry.trim());
        if (
          bound.includes("test") ||
          bound.some((entry) => /^test\s+as\s/.test(entry))
        ) {
          return true;
        }
      }
      return false;
    })
    .sort();
}

/** A connection string with its database swapped and the rest kept. */
export function withDatabase(connection, database) {
  if (!/(^|;)\s*Database=/i.test(connection)) {
    throw new Error("The connection string names no database.");
  }
  return connection.replace(/(^|;)(\s*Database=)[^;]*/i, `$1$2${database}`);
}

/** The payment fixture's connection string: the main one with the database swapped. */
export function paymentConnection(mainConnection) {
  return withDatabase(mainConnection, PAYMENT_DATABASE);
}

/** The stand-in fixture's connection string: the main one with the database swapped. */
export function standInConnection(mainConnection) {
  return withDatabase(mainConnection, STAND_IN_DATABASE);
}

/**
 * The Playwright passes the arguments ask for, in the order they run: `main` always, then
 * `payments` with `--payments`, then `stand-in` with `--stand-in`, whatever order the flags came in.
 *
 * Each pass has a name, which is what its test records and journal reading are filed under and what
 * the scenario check calls it (`PASSES` in `scenario-catalogue.mjs`); a title for the log; the spec
 * files it runs, none meaning all; the connection string its journal and invariants are read
 * through; the manifest it needs, if any; and the environment its Playwright run adds. A fixture pass
 * names its own database to the host and to the test's own commands, and its own HTML report
 * directory, so that it does not overwrite the main pass's.
 *
 * `env` is the caller's environment. Only the simulated-payment pass takes a manifest from it,
 * `Payments__ManifestPath`, as it did before the stand-in pass existed. The stand-in pass never
 * does: that variable names the payment fixture's manifest when it is set.
 */
export function planPasses(
  argv,
  { mainConnection, env = {}, root = repoRoot },
) {
  const flags = { "--payments": "payments", "--stand-in": "stand-in" };
  const unknown = argv.filter((argument) => !(argument in flags));
  if (unknown.length > 0) {
    throw new Error(`Unknown argument: ${unknown[0]}. ${USAGE}`);
  }
  const passes = [
    {
      name: "main",
      title: "main pass",
      specs: [],
      connection: mainConnection,
      env: {},
    },
  ];
  if (argv.includes("--payments")) {
    const connection = paymentConnection(mainConnection);
    const manifest =
      env.Payments__ManifestPath ?? path.join(root, "payment-fixture.local");
    passes.push({
      name: "payments",
      title: "simulated-payment pass",
      specs: ["simulated-payment.spec.ts"],
      connection,
      manifest,
      create: "./scripts/payment-fixture.ps1 init",
      env: {
        PAYMENT_SIMULATION_E2E: "1",
        Payments__ManifestPath: manifest,
        ConnectionStrings__Default: connection,
        PLAYWRIGHT_HTML_OUTPUT_DIR: "playwright-report-payments",
      },
    });
  }
  if (argv.includes("--stand-in")) {
    const connection = standInConnection(mainConnection);
    const manifest = path.join(root, "payment-fixture-standin.local");
    passes.push({
      name: "stand-in",
      title: "Stripe stand-in pass",
      specs: ["stripe-stand-in.spec.ts"],
      connection,
      manifest,
      create: "./scripts/payment-fixture.ps1 init -StandIn",
      env: {
        STRIPE_STAND_IN_E2E: "1",
        Payments__ManifestPath: manifest,
        ConnectionStrings__Default: connection,
        PLAYWRIGHT_HTML_OUTPUT_DIR: "playwright-report-stand-in",
      },
    });
  }
  return passes;
}

/** `a`, `a and b`, `a, b and c`. */
export function listed(names) {
  if (names.length <= 1) return names.join("");
  return `${names.slice(0, -1).join(", ")} and ${names.at(-1)}`;
}

/** The parts of an Npgsql connection string, by lower-cased key. */
export function parseConnection(connection) {
  const parts = {};
  for (const pair of connection.split(";")) {
    const index = pair.indexOf("=");
    if (index < 0) continue;
    parts[pair.slice(0, index).trim().toLowerCase()] = pair
      .slice(index + 1)
      .trim();
  }
  if (!parts.database) {
    throw new Error("The connection string names no database.");
  }
  return parts;
}

/**
 * The SQL that reads the journal entries posted since a moment, in every organization.
 *
 * It sets no organization context. `journal_entries` is under row-level security, and only
 * `OrgScopedExecutor` may set the context a runtime role reads through (ADR-048; an architecture
 * test scans `scripts/` for a second setter). So the reader is the container's own superuser, which
 * row-level security does not bind, in a read-only transaction, the way `payment-fixture.ps1`
 * reads its fixture. That is acceptable only because this database is a development or CI one.
 */
export function journalQuery(sinceMs) {
  if (!Number.isInteger(sinceMs)) throw new Error("`since` is not a time.");
  return [
    "BEGIN TRANSACTION READ ONLY;",
    "SELECT 'entry', event_type, floor(extract(epoch FROM posted_at) * 1000)::bigint",
    `  FROM journal_entries WHERE posted_at >= to_timestamp(${sinceMs} / 1000.0);`,
    "COMMIT;",
  ].join("\n");
}

/** The `entry|<event type>|<milliseconds>` lines of the journal read, as postings of a pass. */
export function parseJournalRows(output, pass) {
  return output
    .split("\n")
    .map((line) => line.trim().split("|"))
    .filter((fields) => fields[0] === "entry" && fields.length === 3)
    .map(([, eventType, postedAt]) => ({
      pass,
      eventType,
      postedAt: Number(postedAt),
    }));
}

function step(title) {
  console.log(`\n==> ${title}`);
}

function runOrThrow(command, args, options = {}) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    stdio: "inherit",
    ...options,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(
      `${command} ${args.join(" ")} exited with ${result.status}.`,
    );
  }
}

/**
 * Runs Playwright and returns its exit status, which is the whole of a pass's own verdict here.
 * Playwright exits 0 when every test had the outcome it expected. A test marked with `test.fail()`
 * that fails had it, so a pass whose only failure is an expected one succeeds; one that passes did
 * not, so the pass fails. The scenario check then reports the first as an expected failure and fails
 * on the second, from the outcome each test recorded.
 */
export function playwright(args, env, options = {}) {
  const result = spawnSync("npx", ["playwright", "test", ...args], {
    cwd: webRoot,
    stdio: "inherit",
    ...options,
    env: { ...process.env, ...env },
  });
  if (result.error) throw result.error;
  return result.status ?? 1;
}

function startCollector(session) {
  mkdirSync(hostDir, { recursive: true });
  const collector = spawn(
    "dotnet",
    [
      "dotnet-coverage",
      "collect",
      "--server-mode",
      "--session-id",
      session,
      "--settings",
      path.join(repoRoot, "tests", "coverage.settings.xml"),
      "--output",
      hostCoverage,
      "--output-format",
      "cobertura",
      "--nologo",
    ],
    { cwd: repoRoot, stdio: ["ignore", "pipe", "inherit"] },
  );
  const exited = new Promise((resolve) => {
    collector.on("exit", (code) => resolve(code));
    collector.on("error", () => resolve(1));
  });
  // The collector prints its session id once it is listening; a client that connects before that
  // has nothing to connect to.
  const ready = new Promise((resolve, reject) => {
    let output = "";
    collector.stdout.on("data", (chunk) => {
      process.stdout.write(chunk);
      output += chunk;
      if (output.includes("SessionId:")) resolve();
    });
    exited.then((code) =>
      reject(new Error(`The coverage collector exited early (${code}).`)),
    );
    setTimeout(
      () => reject(new Error("The coverage collector did not start in 60 s.")),
      60_000,
    ).unref();
  });
  return { collector, exited, ready };
}

function reportHost() {
  if (!existsSync(hostCoverage) || statSync(hostCoverage).size === 0) {
    throw new Error(
      `The collector wrote no host coverage at ${path.relative(repoRoot, hostCoverage)}.`,
    );
  }
  runOrThrow("dotnet", [
    "reportgenerator",
    `-reports:${hostCoverage}`,
    `-targetdir:${path.join(hostDir, "report")}`,
    "-reporttypes:Html;Cobertura;JsonSummary",
    "-title:Host coverage as reached by the browser",
    "-verbosity:Warning",
  ]);
  runOrThrow("node", [
    "scripts/coverage-summary.mjs",
    "dotnet",
    "--summary",
    path.join(hostDir, "report", "Summary.json"),
    "--title",
    "Host coverage as reached by the browser, by module",
    "--out-md",
    path.join(hostDir, "summary.md"),
    "--out-json",
    path.join(hostDir, "summary.json"),
  ]);
}

function reportWeb() {
  runOrThrow("node", ["e2e/coverage-report.ts"], { cwd: webRoot });
  runOrThrow("node", [
    "scripts/coverage-summary.mjs",
    "web",
    "--summary",
    path.join(outputRoot, "web", "coverage-summary.json"),
    "--title",
    "Web coverage as reached by the browser, by area",
    "--out-md",
    path.join(outputRoot, "web", "summary.md"),
    "--out-json",
    path.join(outputRoot, "web", "summary.json"),
  ]);
}

function reportRoutes() {
  // The contract the typed client is generated from, emitted the same way `npm run api:generate`
  // emits it.
  runOrThrow("node", ["scripts/emit-openapi.mjs"]);
  runOrThrow("node", [
    "scripts/route-inventory.mjs",
    "--openapi",
    openApiDocument,
    "--requests",
    path.join(outputRoot, "requests"),
    "--out-json",
    path.join(routesDir, "route-inventory.json"),
    "--out-md",
    path.join(routesDir, "route-inventory.md"),
    "--title",
    "API routes reached by the Playwright run",
  ]);
}

// Runs as the container's `postgres` superuser over its local socket; see `journalQuery` for why.
function psql(connection, sql) {
  const { database } = parseConnection(connection);
  const container = process.env.E2E_DB_CONTAINER ?? "leasebook-db";
  const result = spawnSync(
    "docker",
    [
      "exec",
      "-i",
      container,
      "psql",
      "-U",
      "postgres",
      "-d",
      database,
      "-v",
      "ON_ERROR_STOP=1",
      "-At",
    ],
    { input: sql, encoding: "utf8" },
  );
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(
      `Could not read the journal through psql in container ${container}: ${result.stderr.trim()}`,
    );
  }
  return result.stdout;
}

function readJournal(pass, connection, sinceMs) {
  const orgCount = psql(connection, "SELECT id FROM orgs ORDER BY id;")
    .split("\n")
    .filter((line) => line.trim()).length;
  if (orgCount === 0) {
    throw new Error(`The ${pass} pass's database holds no organization.`);
  }
  const postings = parseJournalRows(
    psql(connection, journalQuery(sinceMs)),
    pass,
  );
  mkdirSync(journalDir, { recursive: true });
  writeFileSync(
    path.join(journalDir, `${pass}.json`),
    `${JSON.stringify(postings, null, 2)}\n`,
  );
  console.log(
    `${pass}: ${postings.length} journal entries posted during the run, in ${orgCount} organizations.`,
  );
}

// Returns the scenario check's exit status. It writes its own report either way.
function verifyScenarios(passes) {
  const result = spawnSync(
    "node",
    [
      "scripts/scenario-catalogue.mjs",
      "verify",
      "--openapi",
      openApiDocument,
      "--evidence",
      outputRoot,
      "--passes",
      passes.map((pass) => pass.name).join(","),
      "--out-json",
      path.join(scenariosDir, "scenario-evidence.json"),
      "--out-md",
      path.join(scenariosDir, "scenario-evidence.md"),
    ],
    { cwd: repoRoot, stdio: "inherit" },
  );
  if (result.error) throw result.error;
  return result.status ?? 1;
}

// The host has stopped by now, so this is the state every scenario of the pass left behind.
function checkInvariants(connection) {
  const result = spawnSync(
    "dotnet",
    [
      "run",
      "--project",
      "src/LeaseBook.Web",
      "--no-launch-profile",
      "--",
      "check-invariants",
      "--all",
    ],
    {
      cwd: repoRoot,
      stdio: "inherit",
      env: {
        ...process.env,
        ASPNETCORE_ENVIRONMENT: "Development",
        ConnectionStrings__Default: connection,
      },
    },
  );
  if (result.error) throw result.error;
  return result.status ?? 1;
}

async function main(argv) {
  const mainConnection =
    process.env.ConnectionStrings__Default ?? DEFAULT_CONNECTION;
  const passes = planPasses(argv, { mainConnection, env: process.env });
  // A missing manifest would fail its pass only after the main pass had run for minutes.
  for (const pass of passes) {
    if (pass.manifest && !existsSync(pass.manifest)) {
      throw new Error(
        `The ${pass.title} needs ${path.relative(repoRoot, pass.manifest)}, which does not exist. ` +
          `Create its fixture with \`${pass.create}\`.`,
      );
    }
  }

  const bypassing = specsBypassingFixture(path.join(webRoot, "e2e"));
  if (bypassing.length > 0) {
    throw new Error(
      `These specs import \`test\` from '@playwright/test' and would run unrecorded; ` +
        `import it from './test' instead: ${bypassing.join(", ")}`,
    );
  }

  rmSync(outputRoot, { recursive: true, force: true });
  mkdirSync(outputRoot, { recursive: true });

  const session = `leasebook-e2e-${process.pid}`;
  step(`Starting the coverage collector (session ${session})`);
  const { exited, ready } = startCollector(session);
  await ready;

  const coverageEnv = { E2E_COVERAGE: "1", E2E_COVERAGE_SESSION: session };
  // Only entries posted from here on are the run's own: the seeds posted the rest.
  const since = Date.now();
  const failedPasses = [];
  try {
    for (const pass of passes) {
      step(`Playwright: ${pass.title}`);
      const status = playwright(pass.specs, {
        ...coverageEnv,
        E2E_COVERAGE_PASS: pass.name,
        ...pass.env,
      });
      if (status !== 0) failedPasses.push(pass.name);
    }
  } finally {
    step("Stopping the coverage collector");
    spawnSync("dotnet", ["dotnet-coverage", "shutdown", session, "--nologo"], {
      cwd: repoRoot,
      stdio: "inherit",
    });
    await exited;
  }

  step("Host coverage report");
  reportHost();
  step("Web coverage report");
  reportWeb();
  step("Route inventory");
  reportRoutes();

  step("Scenario catalogue: declared against measured");
  for (const pass of passes) readJournal(pass.name, pass.connection, since);
  const scenariosFailed = verifyScenarios(passes) !== 0;

  const violated = [];
  for (const pass of passes) {
    step(`Invariants: every organization of the ${pass.name} pass`);
    if (checkInvariants(pass.connection) !== 0) violated.push(pass.name);
  }
  const passNames = (names) =>
    `${listed(names)} pass${names.length > 1 ? "es" : ""}`;
  const invariants =
    violated.length === 0
      ? `Every organization passed \`check-invariants --all\` after the ${passNames(passes.map((pass) => pass.name))}.`
      : `\`check-invariants --all\` failed after the ${passNames(violated)}.`;
  writeFileSync(
    path.join(scenariosDir, "invariants.md"),
    `### Invariants after the run\n\n${invariants}\n`,
  );

  const summary = [
    path.join(hostDir, "summary.md"),
    path.join(outputRoot, "web", "summary.md"),
    path.join(routesDir, "route-inventory.md"),
    path.join(scenariosDir, "scenario-evidence.md"),
    path.join(scenariosDir, "invariants.md"),
  ]
    .map((file) => readFileSync(file, "utf8"))
    .join("\n");
  writeFileSync(path.join(outputRoot, "summary.md"), summary);
  console.log(`\n${summary}`);
  console.log(
    `Reports written under ${path.relative(repoRoot, outputRoot)}/ (host/report/index.html, web/html/index.html, routes/, scenarios/).`,
  );

  const failures = [];
  if (failedPasses.length > 0) {
    failures.push(
      `The Playwright ${passNames(failedPasses)} failed. The reports above were still written, but they describe a failing run.`,
    );
  }
  if (scenariosFailed) {
    failures.push(
      "A scenario did not produce what the catalogue declares. See the scenario section above.",
    );
  }
  if (violated.length > 0) {
    failures.push(
      `An invariant was violated after the ${passNames(violated)}. See the check-invariants output above.`,
    );
  }
  if (failures.length > 0) throw new Error(failures.join("\n"));
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    await main(process.argv.slice(2));
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
