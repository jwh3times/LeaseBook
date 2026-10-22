#!/usr/bin/env node

/**
 * A fresh database for each local end-to-end run (#575). The suite changes what it runs against: it
 * signs the cutover organization off, reconciles and locks a month, clears a register line, moves
 * an owner's figures and runs rent. A second run on the same database fails on all of that, so each
 * run gets a new copy of a seeded template instead. docs/runbooks/local-dev.md ("End-to-end
 * (Playwright) + a11y") owns the procedure; this header says how the script does it.
 *
 * Usage (from the repository root, or as `npm run e2e:fresh` from web/):
 *
 *   node scripts/e2e-db.mjs run [playwright args…]   # reset, then run the suite on the copy
 *   node scripts/e2e-db.mjs reset [--rebuild]        # recreate the copy, and the template if stale
 *   node scripts/e2e-db.mjs drop                     # drop the copy and the template
 *
 * `reset` builds `leasebook_e2e_template` when it is missing or stale: it creates the database on
 * the `leasebook-db` container (E2E_DB_CONTAINER names another), applies the per-database part of
 * `infra/db/bootstrap.sql`, migrates it as the migrator, and seeds `demo`, `cutover`, `portal` and
 * `scenario` as the app role, as CI does. It then drops `leasebook_e2e` and copies it from the
 * template with `CREATE DATABASE … TEMPLATE`, which takes seconds. `run` does that and runs
 * `playwright test` in web/ with `ConnectionStrings__Default` naming the copy, so the host Playwright
 * starts uses it.
 *
 * What it guarantees:
 *
 *   - It creates, writes to and drops only those two databases, by fixed name (`assertOwnDatabase`).
 *     The development database `leasebook`, the payment fixtures and the sandbox runs are never
 *     touched, so nothing here needs `./scripts/dev.ps1 reset-db`.
 *   - The template records what it was built from, as its database comment: a hash of the
 *     migrations, the seeders and the bootstrap SQL. A change to any of them rebuilds it. A change to
 *     code a seeder runs through, such as a posting template, does not: pass `--rebuild` then.
 *   - Nothing connects to the template once it is built (`ALLOW_CONNECTIONS false`), because a
 *     connection to it, such as pgAdmin's, would stop every later copy.
 *   - `run` refuses to start while something answers on :5080. Playwright would reuse that host,
 *     and it runs on whatever database it was started with, not on the copy.
 */

import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readdirSync, readFileSync, statSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const webRoot = path.join(repoRoot, "web");

export const TEMPLATE = "leasebook_e2e_template";
export const COPY = "leasebook_e2e";
export const ORGS = ["demo", "cutover", "portal", "scenario"];
export const HOST_HEALTH = "http://localhost:5080/api/health";

/** What the template is built from. A change to any of these rebuilds it. */
export const SOURCES = [
  "infra/db/bootstrap.sql",
  "src/LeaseBook.Web/Migrations",
  "src/LeaseBook.Web/Seeding",
];

const USAGE =
  "Usage: e2e-db.mjs run [playwright args…] | reset [--rebuild] | drop";

export class Refusal extends Error {}

/** The name, if it is one of the two databases this script owns; otherwise it refuses. */
export function assertOwnDatabase(name) {
  if (name !== TEMPLATE && name !== COPY) {
    throw new Refusal(`${name} is not a database this command manages.`);
  }
  return name;
}

export function connectionString(database, role, port = 5632) {
  const password = {
    leasebook_app: "dev_app_pw",
    leasebook_migrator: "dev_migrator_pw",
  }[role];
  if (!password) throw new Error(`No development role ${role}.`);
  return `Host=localhost;Port=${port};Database=${assertOwnDatabase(database)};Username=${role};Password=${password}`;
}

/**
 * The per-database part of the bootstrap SQL: everything after its `\connect leasebook`. The roles
 * are cluster-wide and already exist; the grants and the hangfire schema are made in each database.
 */
export function bootstrapSchema(sql) {
  const [, schema] = sql.split("\\connect leasebook", 2);
  if (!schema?.trim()) {
    throw new Error("The bootstrap SQL has no `\\connect leasebook` section.");
  }
  return schema;
}

/** Every file under the given paths, relative to `root` with forward slashes, sorted. */
export function sourceFiles(root, paths = SOURCES) {
  const files = [];
  const walk = (relative) => {
    const full = path.join(root, relative);
    if (statSync(full).isDirectory()) {
      for (const entry of readdirSync(full)) walk(path.join(relative, entry));
    } else {
      files.push(relative.split(path.sep).join("/"));
    }
  };
  for (const relative of paths) walk(relative);
  return files.sort();
}

/** A hash of each source file's path and content. Line endings are normalized first. */
export function fingerprint(root, paths = SOURCES) {
  const hash = createHash("sha256");
  for (const file of sourceFiles(root, paths)) {
    hash.update(file);
    hash.update("\0");
    hash.update(
      readFileSync(path.join(root, file), "utf8").replaceAll("\r\n", "\n"),
    );
    hash.update("\0");
  }
  return hash.digest("hex");
}

/** Whether the template must be built: absent, built from other sources, or asked for. */
export function needsBuild(comment, current, { rebuild = false } = {}) {
  if (rebuild || !comment) return true;
  try {
    return JSON.parse(comment).fingerprint !== current;
  } catch {
    return true;
  }
}

export function commentSql(record) {
  const text = JSON.stringify(record).replaceAll("'", "''");
  return `COMMENT ON DATABASE ${TEMPLATE} IS '${text}';`;
}

export function dropSql(name) {
  return `DROP DATABASE IF EXISTS ${assertOwnDatabase(name)};`;
}

export function copySql() {
  return `CREATE DATABASE ${COPY} TEMPLATE ${TEMPLATE} OWNER leasebook_migrator;`;
}

/** The environment every dotnet step gets: the given connections, and no payment or e2e settings. */
export function dotnetEnvironment(env, connections) {
  const kept = {};
  for (const [name, value] of Object.entries(env)) {
    if (/^(Payments__|ConnectionStrings__)/i.test(name)) continue;
    if (
      /^(E2E_COVERAGE|STRIPE_STAND_IN_E2E|PAYMENT_SIMULATION_E2E|STRIPE_SANDBOX_E2E)/.test(
        name,
      )
    ) {
      continue;
    }
    kept[name] = value;
  }
  return { ...kept, ASPNETCORE_ENVIRONMENT: "Development", ...connections };
}

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (command === "run") return { command, playwright: rest };
  if (command === "reset") {
    const unknown = rest.filter((arg) => arg !== "--rebuild");
    if (unknown.length > 0) throw new Refusal(USAGE);
    return { command, rebuild: rest.includes("--rebuild") };
  }
  if (command === "drop" && rest.length === 0) return { command };
  throw new Refusal(USAGE);
}

const container = () => process.env.E2E_DB_CONTAINER ?? "leasebook-db";

function step(title) {
  console.log(`\n==> ${title}`);
}

// Statements run as the container's superuser. Creating, commenting on, copying and dropping a
// database are done from the maintenance database `postgres`, never from inside it.
function psql(database, sql) {
  const result = spawnSync(
    "docker",
    [
      "exec",
      "-i",
      container(),
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
    throw new Refusal(
      result.stderr.trim() || `psql exited with ${result.status}.`,
    );
  }
  return result.stdout.trim();
}

const maintenance = (sql) => psql("postgres", sql);

function run(command, args, env) {
  const result = spawnSync(command, args, {
    cwd: repoRoot,
    stdio: "inherit",
    env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Refusal(
      `${command} ${args.join(" ")} exited with ${result.status}.`,
    );
  }
}

function templateComment() {
  return maintenance(
    `SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = '${TEMPLATE}';`,
  );
}

function buildTemplate(current) {
  step(`Building ${TEMPLATE}`);
  dropTemplate();
  maintenance(`CREATE DATABASE ${TEMPLATE} OWNER leasebook_migrator;`);
  psql(
    TEMPLATE,
    bootstrapSchema(
      readFileSync(path.join(repoRoot, "infra/db/bootstrap.sql"), "utf8"),
    ),
  );
  const env = dotnetEnvironment(process.env, {
    ConnectionStrings__Default: connectionString(TEMPLATE, "leasebook_app"),
    ConnectionStrings__Migrations: connectionString(
      TEMPLATE,
      "leasebook_migrator",
    ),
  });
  step("Migrating the template");
  run(
    "dotnet",
    [
      "ef",
      "database",
      "update",
      "--project",
      "src/LeaseBook.Web",
      "--context",
      "AppDbContext",
    ],
    env,
  );
  for (const org of ORGS) {
    step(`Seeding ${org}`);
    run(
      "dotnet",
      [
        "run",
        "--project",
        "src/LeaseBook.Web",
        "--no-build",
        "--no-launch-profile",
        "--",
        "seed",
        "--org",
        org,
      ],
      env,
    );
  }
  maintenance(
    commentSql({ fingerprint: current, builtAt: new Date().toISOString() }),
  );
  maintenance(`ALTER DATABASE ${TEMPLATE} ALLOW_CONNECTIONS false;`);
}

function dropTemplate() {
  const exists = maintenance(
    `SELECT count(*) FROM pg_database WHERE datname = '${TEMPLATE}';`,
  );
  if (exists === "0") return;
  // A template is closed to connections; a drop needs it open, and needs nothing connected.
  maintenance(`ALTER DATABASE ${TEMPLATE} ALLOW_CONNECTIONS true;`);
  maintenance(dropSql(TEMPLATE));
}

function dropCopy() {
  try {
    maintenance(dropSql(COPY));
  } catch (error) {
    throw new Refusal(
      `${COPY} could not be dropped. Stop whatever is connected to it, such as a host Playwright left ` +
        `running, psql or pgAdmin, and try again.\n${error.message}`,
    );
  }
}

export function reset({ rebuild = false } = {}) {
  const current = fingerprint(repoRoot);
  if (needsBuild(templateComment(), current, { rebuild })) {
    buildTemplate(current);
  } else {
    step(`${TEMPLATE} is current`);
  }
  step(`Copying ${TEMPLATE} to ${COPY}`);
  dropCopy();
  maintenance(copySql());
  return connectionString(COPY, "leasebook_app");
}

async function hostAnswers() {
  try {
    await fetch(HOST_HEALTH, { signal: AbortSignal.timeout(2000) });
    return true;
  } catch {
    return false;
  }
}

async function main(argv) {
  const args = parseArgs(argv);
  if (args.command === "drop") {
    dropCopy();
    dropTemplate();
    console.log(`Dropped ${COPY} and ${TEMPLATE}.`);
    return 0;
  }
  if (args.command === "run" && (await hostAnswers())) {
    throw new Refusal(
      "A host already answers on :5080. Playwright would reuse it, and it does not run on the fresh copy. " +
        "Stop it and run this again.",
    );
  }
  const connection = reset({ rebuild: args.rebuild });
  if (args.command === "reset") {
    console.log(
      `\n${COPY} is ready. Point a host or an e2e run at it with\n  ConnectionStrings__Default="${connection}"`,
    );
    return 0;
  }
  step(`Running the suite on ${COPY}`);
  const result = spawnSync("npx", ["playwright", "test", ...args.playwright], {
    cwd: webRoot,
    stdio: "inherit",
    env: { ...process.env, ConnectionStrings__Default: connection },
  });
  if (result.error) throw result.error;
  return result.status ?? 1;
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    process.exit(await main(process.argv.slice(2)));
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
