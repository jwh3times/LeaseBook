#!/usr/bin/env node

/**
 * A maintainer's run against a real Stripe sandbox, as one command (ADR-054, #546). It never runs
 * in CI. docs/runbooks/payment-simulation.md ("Run the Stripe sandbox fixture") owns the procedure
 * and what to expect; this header says how the script does it.
 *
 * Usage (from the repository root), with the sandbox key injected by a secret manager:
 *
 *   op run --env-file=<refs> -- node scripts/stripe-sandbox-run.mjs start
 *   op run --env-file=<refs> -- node scripts/stripe-sandbox-run.mjs resume <run> [--keep]
 *   node scripts/stripe-sandbox-run.mjs discard <run>
 *   node scripts/stripe-sandbox-run.mjs list
 *
 * The key is read from STRIPE_SANDBOX_SECRET_KEY, or asked for, masked, when it is absent and the
 * terminal is interactive. Only a test-mode key (`sk_test_`, `rk_test_`) is taken.
 *
 * `start` copies the sandbox fixture database `leasebook_payment_fixture_stripe` to a new database
 * `leasebook_sandbox_run_<8 hex>` on the `leasebook-db` container, migrates the copy, takes the
 * webhook signing secret from `stripe listen --print-secret`, starts a host on the copy on port 5090,
 * then `stripe listen` against it, waits for the host's first sweep, checks that the connected
 * account holds nothing Stripe has not yet paid out, and runs the `start` step of
 * `web/e2e/stripe-sandbox.spec.ts`: two test payments, each paid. It stops the listener and the
 * host, runs `check-invariants --all` on the copy and keeps it. `resume <run>` migrates the copy
 * again, starts the same way and runs the `resume` step, which reads Stripe's payout on Operations.
 * A payout waiting for the bank or in review ends the run: the copy is dropped, unless `--keep`
 * keeps it so that bank evidence can still be given by hand. With no payout yet the copy is kept.
 *
 * What it guarantees:
 *
 *   - The fixture database is only ever the copy's source. The copy is made by `CREATE DATABASE …
 *     TEMPLATE`, from the maintenance database; nothing connects to the fixture, migrates it, writes
 *     to it or drops it, and every write goes through `assertRunDatabase`.
 *   - The key and the signing secret reach the host's process environment and nothing else: not
 *     the database, the migrations, the invariant check, Playwright, the SPA or the Stripe CLI.
 *   - One Stripe CLI process at a time: `--print-secret` has exited before `listen` starts. Two at
 *     once have signed the CLI out of the sandbox.
 *   - Everything printed passes through a mask: keys, signing secrets, connected accounts and
 *     persons whole, other Stripe object ids to their last four characters. Nothing it starts writes
 *     its output to a file, and the manifest is never printed (it holds a signing key).
 *   - Every path stops the listener and the host, Ctrl-C included. A `start` that fails before a
 *     charge could have been submitted drops its copy; after that, the copy is kept and the reason
 *     said, because the charges exist at Stripe and only that copy knows them.
 *
 * A test-only override, STRIPE_SANDBOX_RUN_TEST_SOURCE=standin, points every step at the stand-in
 * fixture (`leasebook_payment_fixture_standin`, `payment-fixture-standin.local`) and at a stand-in
 * for Stripe already running on 127.0.0.1:4010, with its own key. Nothing then reaches Stripe: the
 * two Stripe CLI steps are replaced, `--print-secret` by STRIPE_SANDBOX_RUN_TEST_WEBHOOK_SECRET (the
 * secret the stand-in was started with) and `listen` by a loop that tells the stand-in each
 * payment succeeded, and with STRIPE_SANDBOX_RUN_TEST_PAYOUT=1 to pay them out; the quiet check
 * answers from STRIPE_SANDBOX_RUN_TEST_PENDING_CENTS (0 when unset). Start the stand-in first with
 * `--callback=http://127.0.0.1:5090/callbacks/payments/stripe`. It exists to try copy, migrate, host,
 * Playwright, invariants, resume and discard for real without a sandbox.
 */

import { spawn } from "node:child_process";
import { randomBytes } from "node:crypto";
import { mkdtempSync, readFileSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import { createInterface } from "node:readline";
import { fileURLToPath, pathToFileURL } from "node:url";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
const webRoot = path.join(repoRoot, "web");

export const FIXTURE_DATABASE = "leasebook_payment_fixture_stripe";
export const FIXTURE_MANIFEST = "payment-fixture-stripe.local";
export const STAND_IN_DATABASE = "leasebook_payment_fixture_standin";
export const STAND_IN_MANIFEST = "payment-fixture-standin.local";
export const STAND_IN_KEY = "sk_test_leasebook_stand_in";
export const STAND_IN_API_BASE = "http://127.0.0.1:4010";
export const RUN_PREFIX = "leasebook_sandbox_run_";
export const HOST_PORT = 5090;
export const HOST_URL = `http://localhost:${HOST_PORT}`;
export const CALLBACK = `localhost:${HOST_PORT}/callbacks/payments/stripe`;
export const TEST_SOURCE = "STRIPE_SANDBOX_RUN_TEST_SOURCE";
const CONTAINER = "leasebook-db";
const RUN_NAME = /^leasebook_sandbox_run_[0-9a-f]{8}$/;
const SOURCES = [FIXTURE_DATABASE, STAND_IN_DATABASE];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const PAID = /^Paid on \d{4}-\d{2}-\d{2} — on its way to the bank/;
const USAGE = [
  "Usage: node scripts/stripe-sandbox-run.mjs <command>",
  "  start                  copy the sandbox fixture, charge two test payments, keep the copy",
  "  resume <run> [--keep]  look for Stripe's payout of a run's payments",
  "  discard <run>          drop a run's copy",
  "  list                   list the runs' copies",
].join("\n");

/** A reason to stop, said to the maintainer as it is. */
export class Refusal extends Error {}
/** Ctrl-C, or another signal that asks the run to stop. */
export class Interrupted extends Error {
  constructor() {
    super("Interrupted.");
  }
}

// --- Pure helpers, exported for the tests -------------------------------------------------

/** This never runs in CI, whatever else is set. */
export function refuseCi(env) {
  if (env.CI !== undefined && env.CI !== "") {
    throw new Refusal(
      "CI is set. This command charges a real Stripe sandbox and never runs in CI. Nothing was done.",
    );
  }
}

/** Only a Stripe test-mode secret key: a live key is refused before anything is called. */
export function assertTestKey(key) {
  if (typeof key !== "string" || key === "") {
    throw new Refusal("No Stripe key was given.");
  }
  if (/^(sk|rk)_live_/.test(key)) {
    throw new Refusal(
      "That is a live Stripe key. This command takes only a sandbox's test-mode key (sk_test_ or rk_test_). Nothing was done.",
    );
  }
  if (!/^(sk|rk)_test_[\x21-\x7E]+$/.test(key)) {
    throw new Refusal(
      "STRIPE_SANDBOX_SECRET_KEY must be a Stripe test-mode secret key (sk_test_ or rk_test_), with no space or line break. Nothing was done.",
    );
  }
  return key;
}

/**
 * Where a run's data comes from: the sandbox fixture, or, under the test-only override, the
 * stand-in's fixture and the stand-in for Stripe.
 */
export function sourceProfile(env) {
  const chosen = env[TEST_SOURCE];
  if (chosen === undefined || chosen === "") {
    return {
      name: "stripe",
      database: FIXTURE_DATABASE,
      manifest: path.join(repoRoot, FIXTURE_MANIFEST),
      standIn: null,
    };
  }
  if (chosen !== "standin") {
    throw new Refusal(`${TEST_SOURCE} takes only "standin", or is left unset.`);
  }
  return {
    name: "standin",
    database: STAND_IN_DATABASE,
    manifest: path.join(repoRoot, STAND_IN_MANIFEST),
    standIn: { apiBase: STAND_IN_API_BASE, key: STAND_IN_KEY },
  };
}

/**
 * The name of a run's copy, and nothing else, may be written to, migrated, connected to by a host
 * or dropped. The fixture is only ever copied.
 */
export function assertRunDatabase(name) {
  if (
    typeof name !== "string" ||
    SOURCES.includes(name) ||
    name.startsWith("leasebook_payment_fixture")
  ) {
    throw new Refusal(
      `Refusing to write to ${name}: a payment fixture database is only ever the source of a copy.`,
    );
  }
  if (!RUN_NAME.test(name)) {
    throw new Refusal(
      `${name} is not a sandbox run's copy (${RUN_PREFIX}<8 hex digits>).`,
    );
  }
  return name;
}

/** A run named in full or by its eight hex digits. */
export function runName(given) {
  const name = /^[0-9a-f]{8}$/.test(given ?? "")
    ? `${RUN_PREFIX}${given}`
    : given;
  return assertRunDatabase(name);
}

/** A new run's name, from eight random hex digits. */
export function newRunName(bytes = randomBytes(4)) {
  return `${RUN_PREFIX}${Buffer.from(bytes).toString("hex")}`;
}

/** The SQL that copies a fixture to a run's copy. */
export function copySql(source, target) {
  if (!SOURCES.includes(source)) {
    throw new Refusal(
      `${source} is not a payment fixture this command copies.`,
    );
  }
  return `CREATE DATABASE ${assertRunDatabase(target)} TEMPLATE ${source} OWNER leasebook_migrator;`;
}

export function dropSql(target) {
  return `DROP DATABASE ${assertRunDatabase(target)};`;
}

/** What is recorded about a run, on the copy itself, as its database comment. */
export function commentSql(target, record) {
  const text = JSON.stringify(record).replaceAll("'", "''");
  return `COMMENT ON DATABASE ${assertRunDatabase(target)} IS '${text}';`;
}

export function connectionString(database, role, port = 5632) {
  const password = {
    leasebook_app: "dev_app_pw",
    leasebook_migrator: "dev_migrator_pw",
  }[role];
  if (!password) throw new Error(`No development role ${role}.`);
  return `Host=localhost;Port=${port};Database=${assertRunDatabase(database)};Username=${role};Password=${password}`;
}

// An identifier is a short prefix, an underscore and at least six letters and digits that are not
// all lower-case letters, so that Stripe's enumeration values (`in_progress`) are left alone.
const OBJECT_ID =
  /(?<![A-Za-z0-9_])(pi|ch|py|po|evt|txn|re|pyr|dp|du|in|cus|src|ba|card|pm|seti|mandate|req|we|tr|fee|iar|payatt|pd|cs|sub|prod|price)_([A-Za-z0-9]{6,})(?![A-Za-z0-9_])/g;

/**
 * A function that masks what may not be shown: the given secrets wherever they appear, anything
 * shaped like a key or a signing secret, connected accounts and persons whole, and other Stripe
 * object ids to their prefix and last four characters.
 */
export function createMask(secrets = []) {
  const exact = secrets.filter(
    (secret) => typeof secret === "string" && secret.length >= 8,
  );
  return (value) => {
    let text = String(value);
    for (const secret of exact) {
      const shape = /^(whsec_|acct_|person_|(?:sk|rk|pk)_(?:test|live)_)/.exec(
        secret,
      );
      text = text.split(secret).join(shape ? `${shape[1]}…` : "[secret]");
    }
    return text
      .replace(/\bwhsec_[^\s"',)\]]+/g, "whsec_…")
      .replace(/\b(sk|rk|pk)_(test|live)_[^\s"',)\]]+/g, "$1_$2_…")
      .replace(/\bacct_[A-Za-z0-9]+/g, "acct_…")
      .replace(/\bperson_[A-Za-z0-9]+/g, "person_…")
      .replace(OBJECT_ID, (whole, prefix, body) =>
        /^[a-z]+$/.test(body) ? whole : `${prefix}_…${body.slice(-4)}`,
      );
  };
}

/** The process environment every child gets: no Stripe secret, no connection string, no pass switch. */
export function childEnvironment(env) {
  const kept = {};
  for (const [name, value] of Object.entries(env)) {
    if (/^(Payments__|ConnectionStrings__)/i.test(name)) continue;
    if (name === "STRIPE_SANDBOX_SECRET_KEY") continue;
    if (name.startsWith("STRIPE_SANDBOX_RUN_TEST_")) continue;
    if (
      /^(E2E_COVERAGE|STRIPE_STAND_IN_E2E|PAYMENT_SIMULATION_E2E|STRIPE_SANDBOX_E2E)/.test(
        name,
      )
    ) {
      continue;
    }
    kept[name] = value;
  }
  return kept;
}

/**
 * The host's environment: the copy, the fixture's manifest, and the key and signing secret, which
 * no other process is given. `Payments__Stripe__ApiBase` is set only for the stand-in. Loopback is
 * named in NO_PROXY, because the host's HTTP client otherwise sends even loopback calls to a proxy
 * named in the environment (#545).
 */
export function hostEnvironment(
  env,
  { connection, manifest, key, webhookSecret, apiBase = null },
) {
  const host = {
    ...childEnvironment(env),
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__Default: connection,
    Payments__ManifestPath: manifest,
    Payments__Stripe__SecretKey: key,
    Payments__Stripe__WebhookSecret: webhookSecret,
    // The worker's polling otherwise prints every SQL command it runs, hiding the run's own lines.
    "Logging__LogLevel__Microsoft.EntityFrameworkCore": "Warning",
  };
  if (apiBase) host.Payments__Stripe__ApiBase = apiBase;
  for (const name of ["NO_PROXY", "no_proxy"]) {
    const listed = (env[name] ?? "")
      .split(",")
      .map((entry) => entry.trim())
      .filter(Boolean);
    host[name] = [...new Set([...listed, "127.0.0.1", "localhost"])].join(",");
  }
  return host;
}

/** The few fields of a sandbox manifest the run needs. The manifest itself is never printed. */
export function readFixture(text) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw new Refusal("The fixture manifest is not JSON.");
  }
  const fixtures = parsed?.Payments?.Fixtures;
  if (
    parsed?.Payments?.Mode !== "StripeSandbox" ||
    !Array.isArray(fixtures) ||
    fixtures.length === 0
  ) {
    throw new Refusal("The manifest is not a Stripe sandbox fixture's.");
  }
  const [first] = fixtures;
  if (
    !/^acct_[A-Za-z0-9]+$/.test(first?.Account ?? "") ||
    !UUID.test(first?.OrgId ?? "")
  ) {
    throw new Refusal(
      "The manifest's first fixture has no connected account or organization.",
    );
  }
  return { account: first.Account, orgId: first.OrgId, count: fixtures.length };
}

const money = (cents, currency) =>
  `${cents < 0 ? "-" : ""}${(Math.abs(cents) / 100).toFixed(2)} ${String(currency).toUpperCase()}`;

/** What Stripe holds on the account that it has not yet paid out: every non-zero balance. */
export function unpaidBalance(balance) {
  if (balance?.livemode === true) {
    throw new Refusal(
      "Stripe answered the balance request in live mode. Nothing was charged.",
    );
  }
  const held = [];
  for (const kind of ["available", "pending"]) {
    for (const entry of balance?.[kind] ?? []) {
      if (entry.amount !== 0)
        held.push({ kind, amount: entry.amount, currency: entry.currency });
    }
  }
  return held;
}

export function quietRefusal(held) {
  const listed = held.map(
    (entry) => `${money(entry.amount, entry.currency)} ${entry.kind}`,
  );
  return (
    `The connected account holds ${listed.join(" and ")} that Stripe has not yet paid out. ` +
    "A payout is forming, and charging now would put this run's payments into it beside lines " +
    "LeaseBook cannot tie to them, so the payout would go to review instead of posting. " +
    "Wait until Stripe has paid that out, then start again. Nothing was charged."
  );
}

/** True when the start step's record says a charge may have been submitted. */
export function chargeMayExist(result) {
  if (result === undefined) return false; // the step never wrote anything
  if (result === null) return true; // it wrote something that cannot be read
  return result.submitting === true || (result.payments ?? []).length > 0;
}

/**
 * What a `resume` does with the copy, from what its Playwright step saw: keep it, or drop it unless
 * `--keep`. A failed step, or an outcome it does not know, keeps it.
 */
export function resumeDecision({ code, result, keep, invariantsClean = true }) {
  const outcome = result?.outcome;
  if (code !== 0 || !["none", "review", "held"].includes(outcome)) {
    return { outcome: outcome ?? "unknown", ends: false, drop: false };
  }
  if (outcome === "none") return { outcome, ends: false, drop: false };
  return { outcome, ends: true, drop: !keep && invariantsClean };
}

/** The run's database comment, read back. Unreadable is empty. */
export function parseRecord(text) {
  try {
    const parsed = JSON.parse(text);
    return parsed && typeof parsed === "object" ? parsed : {};
  } catch {
    return {};
  }
}

/** Waits for a promise unless the run is interrupted first. */
export function guarded(promise, signal) {
  if (signal?.aborted) return Promise.reject(new Interrupted());
  if (!signal) return promise;
  return new Promise((resolve, reject) => {
    const onAbort = () => reject(new Interrupted());
    signal.addEventListener("abort", onAbort, { once: true });
    promise.then(
      (value) => {
        signal.removeEventListener("abort", onAbort);
        resolve(value);
      },
      (error) => {
        signal.removeEventListener("abort", onAbort);
        reject(error);
      },
    );
  });
}

/**
 * The Stripe CLI, one process at a time. `printSecret` must have finished before `listen` starts,
 * and a listener must have stopped before anything else starts: starting a second while one runs
 * is refused here, whatever the caller does.
 */
export function oneCliAtATime(cli) {
  let running = null;
  const claim = (what) => {
    if (running) {
      throw new Error(
        `Refusing to start Stripe CLI ${what} while ${running} is still running: two at once have signed the CLI out of the sandbox.`,
      );
    }
    running = what;
  };
  return {
    async printSecret(options) {
      claim("--print-secret");
      try {
        return await cli.printSecret(options);
      } finally {
        running = null;
      }
    },
    async listen(options) {
      claim("listen");
      let listener;
      try {
        listener = await cli.listen(options);
      } catch (error) {
        running = null;
        throw error;
      }
      return {
        async stop() {
          try {
            await listener.stop();
          } finally {
            running = null;
          }
        },
      };
    },
  };
}

// --- The run ------------------------------------------------------------------------------

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function obtainKey(profile, deps) {
  if (profile.standIn) return assertTestKey(profile.standIn.key);
  let key = deps.env.STRIPE_SANDBOX_SECRET_KEY;
  if (key === undefined || key === "") {
    if (!deps.isInteractive) {
      throw new Refusal(
        "STRIPE_SANDBOX_SECRET_KEY is not set and there is no terminal to ask in. Inject it with a secret manager, for example `op run -- node scripts/stripe-sandbox-run.mjs start`. Nothing was done.",
      );
    }
    key = (
      await deps.prompt("Sandbox secret key (sk_test_… or rk_test_…): ")
    ).trim();
  }
  return assertTestKey(key);
}

async function waitForHost(host, deps, signal, timeoutMs = 180_000) {
  const deadline = deps.now() + timeoutMs;
  while (deps.now() < deadline) {
    if (host.exitCode() !== null) {
      throw new Refusal(
        `The host exited with code ${host.exitCode()} before it was ready. Its output is above; the startup refusals in docs/runbooks/payment-simulation.md say what each means.`,
      );
    }
    if ((await guarded(deps.answers(`${HOST_URL}/api/health`), signal)) === 200)
      return;
    await guarded(deps.sleep(1000), signal);
  }
  throw new Refusal(
    `The host did not answer on ${HOST_URL} within ${timeoutMs / 1000} s.`,
  );
}

/** The host's first sweep of each fixture: event 4606 for each, or 4607 for any, which fails the run. */
async function waitForSweeps(
  host,
  count,
  deps,
  signal,
  timeoutMs = 15 * 60_000,
) {
  const deadline = deps.now() + timeoutMs;
  while (deps.now() < deadline) {
    const { completed, failed } = host.sweeps();
    if (failed > 0) {
      throw new Refusal(
        "The host's first sweep failed (event 4607): it could not read Stripe's events. Its output is above.",
      );
    }
    if (completed >= count) return;
    if (host.exitCode() !== null) {
      throw new Refusal(
        `The host exited with code ${host.exitCode()} during its first sweep.`,
      );
    }
    await guarded(deps.sleep(1000), signal);
  }
  throw new Refusal(
    `The host's first sweep had not finished after ${timeoutMs / 60_000} minutes.`,
  );
}

/** Copy (or not), migrate, take the secret, start the host, then the listener, then wait for the sweep. */
async function bringUp(state, ctx) {
  const { deps, profile, key, fixture, say, signal } = ctx;
  say(`Migrating ${state.run} as the migrator role.`);
  await guarded(deps.migrate(state.run), signal);

  say(
    "Taking the webhook signing secret from the Stripe CLI (stripe listen --print-secret).",
  );
  const webhookSecret = await guarded(ctx.cli.printSecret({}), signal);
  if (!/^whsec_[\x21-\x7E]+$/.test(webhookSecret ?? "")) {
    throw new Refusal(
      "The Stripe CLI printed no webhook signing secret (whsec_…).",
    );
  }
  ctx.remember(webhookSecret);

  if ((await guarded(deps.answers(`${HOST_URL}/api/health`), signal)) > 0) {
    throw new Refusal(
      `Something already answers on ${HOST_URL}. Stop it (a host left by an earlier run?) and try again.`,
    );
  }
  say(`Starting the host on ${state.run}, at ${HOST_URL}.`);
  state.host = deps.startHost(
    hostEnvironment(deps.env, {
      connection: deps.connection(state.run, "leasebook_app"),
      manifest: profile.manifest,
      key,
      webhookSecret,
      apiBase: profile.standIn?.apiBase ?? null,
    }),
  );
  await waitForHost(state.host, deps, signal);

  say("Forwarding Stripe's events to the host (stripe listen).");
  state.listener = await guarded(
    ctx.cli.listen({ forwardTo: CALLBACK }),
    signal,
  );

  say(
    `Waiting for the host's first sweep of ${fixture.count} fixture(s): events 4606 or 4607.`,
  );
  await waitForSweeps(state.host, fixture.count, deps, signal);
  say("The first sweep is done.");
}

async function stopServers(state, say) {
  if (state.listener) {
    say("Stopping the listener.");
    const listener = state.listener;
    state.listener = null;
    await listener.stop();
  }
  if (state.host) {
    say("Stopping the host.");
    const host = state.host;
    state.host = null;
    await host.stop();
  }
}

async function runPlaywright(state, ctx, step, extra = {}) {
  const { deps, signal } = ctx;
  state.resultDir = deps.tempDir();
  state.resultFile = path.join(state.resultDir, "result.json");
  state.playwright = deps.playwright({
    ...childEnvironment(deps.env),
    STRIPE_SANDBOX_E2E: "1",
    STRIPE_SANDBOX_E2E_STEP: step,
    STRIPE_SANDBOX_E2E_RESULT: state.resultFile,
    STRIPE_SANDBOX_E2E_HOST: HOST_URL,
    ...extra,
  });
  const code = await guarded(state.playwright.exited, signal);
  state.playwright = null;
  return code;
}

async function stopPlaywright(state, deps) {
  if (state.playwright) {
    const playwright = state.playwright;
    state.playwright = null;
    deps.print("Stopping Playwright.");
    await playwright.stop();
  }
  if (state.resultFile) {
    state.result = deps.readResult(state.resultFile);
  }
  if (state.resultDir) {
    deps.removeDir(state.resultDir);
    state.resultDir = null;
    state.resultFile = null;
  }
}

async function invariants(run, ctx) {
  ctx.say(`Running check-invariants --all on ${run}.`);
  const { code } = await ctx.deps.invariants(
    ctx.deps.connection(run, "leasebook_app"),
  );
  return code === 0;
}

function describePayment(payment) {
  const method = payment.method === "ach" ? "ACH " : "Card";
  return `  ${method} $500.00  payment ${payment.id}  ${payment.state ?? "no outcome seen"}`;
}

/** Shared by every verb: the mask, the signal, the CLI and what is said. */
function context(deps) {
  refuseCi(deps.env);
  const profile = sourceProfile(deps.env);
  const secrets = [];
  const mask = (text) => createMask(secrets)(text);
  const say = (line) => deps.print(mask(line));
  const remember = (secret) => {
    secrets.push(secret);
    deps.remember?.(secret);
  };
  return { deps, profile, remember, mask, say, signal: deps.signal };
}

export async function start(deps) {
  const ctx = context(deps);
  const { profile, say, signal } = ctx;
  if (profile.standIn)
    say(
      `Test source: the stand-in fixture and the stand-in at ${profile.standIn.apiBase}. Nothing reaches Stripe.`,
    );
  const key = await obtainKey(profile, deps);
  ctx.remember(key);
  ctx.key = key;
  const fixture = readFixture(deps.readFile(profile.manifest));
  ctx.remember(fixture.account);
  ctx.fixture = fixture;
  ctx.cli = oneCliAtATime(
    deps.cli({ profile, fixture, say: ctx.say, step: "start" }),
  );

  const state = {
    run: newRunName(deps.randomBytes(4)),
    copied: false,
    finished: false,
  };
  try {
    say("Building the host.");
    await guarded(deps.build(), signal);
    say(`Copying ${profile.database} to ${state.run}.`);
    await deps.copy(profile.database, state.run);
    state.copied = true;
    state.record = {
      source: profile.name,
      startedAt: new Date(deps.now()).toISOString(),
      state: "starting",
    };
    await deps.comment(state.run, state.record);

    await bringUp(state, ctx);

    say(
      "Asking Stripe whether the connected account is quiet (GET /v1/balance).",
    );
    const held = unpaidBalance(
      await guarded(deps.balance({ key, account: fixture.account }), signal),
    );
    if (held.length > 0) throw new Refusal(quietRefusal(held));
    say("The connected account holds nothing Stripe has not paid out.");

    say("Playwright: fee rules, then $500.00 by ACH and $500.00 by card.");
    state.playwrightStarted = true;
    const code = await runPlaywright(state, ctx, "start");
    await stopPlaywright(state, deps);
    const payments = state.result?.payments ?? [];
    const allPaid =
      payments.length === 2 &&
      payments.every((payment) => PAID.test(payment.state ?? ""));
    if (code !== 0 || !allPaid) {
      throw new Refusal(
        `The Playwright step did not see both payments paid (exit code ${code}). Its output is above.`,
      );
    }
    state.record = {
      ...state.record,
      state: "charged",
      payments: payments.map(({ method, id }) => ({ method, id })),
    };
    await deps.comment(state.run, state.record);
    await stopServers(state, say);

    const clean = await invariants(state.run, ctx);
    state.finished = true;
    say("");
    say(`Stripe sandbox run ${state.run}`);
    for (const payment of payments) say(describePayment(payment));
    say(
      `  check-invariants --all: ${clean ? "all clean" : "FAILED (output above)"}`,
    );
    say(
      "Next: leave the connected account alone until Stripe pays these out, from a few hours to a few days. Then:",
    );
    say(`  node scripts/stripe-sandbox-run.mjs resume ${state.run}`);
    say(`The copy ${state.run} is kept; \`discard ${state.run}\` drops it.`);
    return clean ? 0 : 1;
  } finally {
    await stopPlaywright(state, deps);
    await stopServers(state, say);
    if (state.copied && !state.finished) {
      const mayExist = state.playwrightStarted && chargeMayExist(state.result);
      if (mayExist) {
        const payments = (state.result?.payments ?? []).filter((payment) =>
          UUID.test(payment.id ?? ""),
        );
        await deps
          .comment(state.run, {
            ...state.record,
            state: "charge may exist",
            payments,
          })
          .catch(() => {});
        say(
          `Kept ${state.run}: a test charge may have been submitted, and only this copy knows it. ` +
            `Look at it with \`resume ${state.run}\` once Stripe has paid out, or drop it with \`discard ${state.run}\`.`,
        );
      } else {
        say(`Dropping ${state.run}: no charge was submitted.`);
        try {
          await deps.drop(state.run);
        } catch (error) {
          say(
            `Could not drop ${state.run} (${error.message}). Drop it with \`discard ${state.run}\`.`,
          );
        }
      }
    }
  }
}

export async function resume(argv, deps) {
  const keep = argv.includes("--keep");
  const names = argv.filter((argument) => argument !== "--keep");
  if (names.length !== 1 || names[0].startsWith("--")) throw new Refusal(USAGE);
  const ctx = context(deps);
  const { profile, say, signal } = ctx;
  const run = runName(names[0]);
  if (!(await deps.exists(run)))
    throw new Refusal(
      `There is no copy named ${run}. \`list\` shows the runs' copies.`,
    );
  const record = parseRecord(await deps.readComment(run));
  if (record.source && record.source !== profile.name) {
    throw new Refusal(
      `${run} was copied from the ${record.source === "standin" ? "stand-in" : "sandbox"} fixture; resume it with the same source.`,
    );
  }
  const key = await obtainKey(profile, deps);
  ctx.remember(key);
  ctx.key = key;
  const fixture = readFixture(deps.readFile(profile.manifest));
  ctx.remember(fixture.account);
  ctx.fixture = fixture;
  ctx.cli = oneCliAtATime(
    deps.cli({ profile, fixture, say: ctx.say, step: "resume" }),
  );

  const state = { run, copied: true };
  let decision = null;
  try {
    say("Building the host.");
    await guarded(deps.build(), signal);
    await bringUp(state, ctx);

    say("Playwright: the Payouts list on Operations.");
    const payments = (record.payments ?? [])
      .map((payment) => payment.id)
      .filter((id) => UUID.test(id ?? ""));
    const code = await runPlaywright(state, ctx, "resume", {
      STRIPE_SANDBOX_E2E_PAYMENTS: payments.join(","),
      STRIPE_SANDBOX_E2E_SINCE: record.startedAt ?? new Date(0).toISOString(),
    });
    await stopPlaywright(state, deps);
    await stopServers(state, say);

    const result = state.result ?? undefined;
    let clean = true;
    decision = resumeDecision({ code, result, keep });
    if (decision.ends) {
      clean = await invariants(run, ctx);
      decision = resumeDecision({ code, result, keep, invariantsClean: clean });
    }
    await deps
      .comment(run, {
        ...record,
        lastResume: {
          at: new Date(deps.now()).toISOString(),
          outcome: decision.outcome,
        },
      })
      .catch(() => {});

    say("");
    say(`Stripe sandbox run ${run}, resumed`);
    if (code !== 0) {
      say(
        `  The Playwright step failed (exit code ${code}); its output is above.`,
      );
      if (result?.outcome)
        say(
          `  It had found: ${result.outcome}${result.payout ? `, payout ${result.payout}` : ""}.`,
        );
    } else if (decision.outcome === "none") {
      say(
        result.others > 0
          ? `  Stripe has not paid out this run's payments yet. ${result.others} other payout(s) are listed; none holds them.`
          : "  Stripe has not paid out this run's payments yet.",
      );
      say(`  Run \`resume ${run}\` again later.`);
    } else if (decision.outcome === "review") {
      say(`  Payout ${result.payout} is in review: ${result.reason}.`);
      if (result.explanation) say(`  The screen says: ${result.explanation}`);
    } else if (decision.outcome === "held") {
      say(
        `  Payout ${result.payout} is waiting for the bank: the processor's amount is $1,000.00, and both lines are payments not on the ledger.`,
      );
    } else {
      say(
        `  Payout ${result?.payout ?? "(unknown)"} is ${result?.status ?? "in a state this command does not know"}.`,
      );
    }
    if (decision.ends)
      say(
        `  check-invariants --all: ${clean ? "all clean" : "FAILED (output above)"}`,
      );
    if (decision.drop) {
      say(`Dropping ${run}: the run has ended.`);
      await deps.drop(run);
      say(`Dropped ${run}.`);
    } else if (decision.ends && keep) {
      say(
        `Kept ${run}, as --keep asked. Give the bank's evidence by hand as the runbook says, then \`discard ${run}\`.`,
      );
    } else if (decision.ends) {
      say(
        `Kept ${run}: check-invariants failed, and the copy is the evidence. \`discard ${run}\` drops it.`,
      );
    } else {
      say(`Kept ${run}.`);
    }
    return code === 0 && ["none", "held"].includes(decision.outcome) && clean
      ? 0
      : 1;
  } finally {
    await stopPlaywright(state, deps);
    await stopServers(state, say);
  }
}

export async function discard(argv, deps) {
  if (argv.length !== 1) throw new Refusal(USAGE);
  const ctx = context(deps);
  const run = runName(argv[0]);
  if (!(await deps.exists(run)))
    throw new Refusal(`There is no copy named ${run}.`);
  if ((await deps.answers(`${HOST_URL}/api/health`)) > 0) {
    throw new Refusal(
      `A host still answers on ${HOST_URL}. Stop it before dropping ${run}: a run from this command stops its own, so this is one left running or started by hand.`,
    );
  }
  const connected = await deps.connections(run);
  if (connected > 0) {
    throw new Refusal(
      `${connected} session(s) are still connected to ${run}. Stop them, then discard it.`,
    );
  }
  const record = parseRecord(await deps.readComment(run));
  await deps.drop(run);
  ctx.say(`Dropped ${run}.`);
  if (
    (record.payments ?? []).length > 0 &&
    !["held", "review"].includes(record.lastResume?.outcome)
  ) {
    ctx.say(
      "Its test payments stay at Stripe. Until Stripe has paid them out, the next run's quiet check refuses to charge.",
    );
  }
  return 0;
}

export async function list(deps) {
  const ctx = context(deps);
  const rows = await deps.listRuns();
  if (rows.length === 0) {
    ctx.say("No sandbox run copies.");
    return 0;
  }
  for (const { name, comment } of rows) {
    const record = parseRecord(comment);
    const parts = [
      name,
      record.startedAt ? `started ${record.startedAt}` : null,
      record.source === "standin" ? "stand-in" : null,
      record.state ?? null,
      record.payments ? `${record.payments.length} payment(s)` : null,
      record.lastResume
        ? `last resumed ${record.lastResume.at}: ${record.lastResume.outcome}`
        : null,
    ];
    ctx.say(parts.filter(Boolean).join("  "));
  }
  return 0;
}

export async function main(argv, deps) {
  const [command, ...rest] = argv;
  switch (command) {
    case "start":
      if (rest.length > 0) throw new Refusal(USAGE);
      return start(deps);
    case "resume":
      return resume(rest, deps);
    case "discard":
      return discard(rest, deps);
    case "list":
      return list(deps);
    default:
      throw new Refusal(USAGE);
  }
}

// --- The real dependencies -----------------------------------------------------------------

/** Starts a process in a group of its own, so that it and its children are stopped together. */
function launch(command, args, { cwd = repoRoot, env, onLine }) {
  const child = spawn(command, args, {
    cwd,
    env,
    detached: true,
    stdio: ["ignore", "pipe", "pipe"],
  });
  let code = null;
  const exited = new Promise((resolve) => {
    child.on("error", (error) => {
      onLine?.(
        `${command}: ${error.code === "ENOENT" ? "not found" : error.message}`,
        "stderr",
      );
      code ??= 127;
      resolve(code);
    });
    child.on("close", (status, signal) => {
      code ??= status ?? (signal ? 128 : 1);
      resolve(code);
    });
  });
  for (const [stream, name] of [
    [child.stdout, "stdout"],
    [child.stderr, "stderr"],
  ]) {
    createInterface({ input: stream }).on("line", (line) =>
      onLine?.(line, name),
    );
  }
  const signalGroup = (signal) => {
    try {
      process.kill(-child.pid, signal);
    } catch {
      // Already gone.
    }
  };
  return {
    exited,
    exitCode: () => code,
    // `group: false` signals the process alone first, for one that stops its own children and
    // takes a second signal as an order to abandon them (Playwright and its web server).
    async stop(signal = "SIGTERM", graceMs = 30_000, { group = true } = {}) {
      if (code !== null || child.pid === undefined) return;
      if (group) signalGroup(signal);
      else child.kill(signal);
      const timer = setTimeout(() => signalGroup("SIGKILL"), graceMs);
      await exited;
      clearTimeout(timer);
    },
  };
}

/** Runs a process to its end and keeps what it printed, for the caller to print masked. */
async function capture(command, args, { cwd, env, input, signal } = {}) {
  const lines = [];
  const child = spawn(command, args, {
    cwd: cwd ?? repoRoot,
    env,
    detached: true,
    stdio: [input === undefined ? "ignore" : "pipe", "pipe", "pipe"],
  });
  if (input !== undefined) child.stdin.end(input);
  const out = [];
  child.stdout.on("data", (chunk) => out.push(chunk));
  createInterface({ input: child.stderr }).on("line", (line) =>
    lines.push(line),
  );
  const onAbort = () => {
    try {
      process.kill(-child.pid, "SIGTERM");
    } catch {
      // Already gone.
    }
  };
  signal?.addEventListener("abort", onAbort, { once: true });
  const code = await new Promise((resolve) => {
    child.on("error", () => resolve(127));
    child.on("close", (status) => resolve(status ?? 1));
  });
  signal?.removeEventListener("abort", onAbort);
  const stdout = Buffer.concat(out).toString("utf8");
  return { code, stdout, stderr: lines.join("\n") };
}

function psql(database, sql) {
  return capture(
    "docker",
    [
      "exec",
      "-i",
      CONTAINER,
      "psql",
      "-U",
      "postgres",
      "-d",
      database,
      "-v",
      "ON_ERROR_STOP=1",
      "-At",
    ],
    { input: sql },
  );
}

// Every statement below runs in the maintenance database `postgres`, as the container's superuser:
// creating, commenting on and dropping a database are done from outside it, and none connects to
// the fixture.
async function maintenance(sql, failure) {
  const result = await psql("postgres", sql);
  if (result.code !== 0) throw failure(result.stderr.trim());
  return result.stdout;
}

function stripeCli({ profile, fixture, say, step, env, mask }) {
  if (profile.standIn) return standInCli({ profile, fixture, say, step, env });
  const cliEnv = childEnvironment(env);
  return {
    async printSecret() {
      // Its output is the secret: kept in memory, never printed.
      const result = await capture("stripe", ["listen", "--print-secret"], {
        env: cliEnv,
      });
      if (result.code !== 0) {
        throw new Refusal(
          `stripe listen --print-secret exited with ${result.code}: ${mask(result.stderr.trim())}. Is the CLI signed in to the sandbox?`,
        );
      }
      return result.stdout.trim().match(/whsec_[\x21-\x7E]+/)?.[0] ?? "";
    },
    async listen({ forwardTo }) {
      let ready;
      const isReady = new Promise((resolve) => (ready = resolve));
      const listener = launch(
        "stripe",
        [
          "listen",
          "--latest",
          "--all-snapshot",
          "--forward-to",
          forwardTo,
          "--forward-connect-to",
          forwardTo,
        ],
        {
          env: cliEnv,
          // Its first line holds the signing secret: printed masked, never written down.
          onLine: (line) => {
            say(`stripe | ${line}`);
            if (/Ready!/.test(line)) ready(true);
          },
        },
      );
      listener.exited.then(() => ready(false));
      const timer = sleep(60_000).then(() => false);
      if (!(await Promise.race([isReady, timer]))) {
        await listener.stop("SIGINT");
        throw new Refusal(
          "stripe listen did not say it was ready. Its output is above.",
        );
      }
      return { stop: () => listener.stop("SIGINT", 15_000) };
    },
  };
}

/**
 * The test-only stand-ins for the two Stripe CLI steps. The secret is the one the stand-in was
 * started with; the listener is a loop that tells the stand-in what Stripe would do by itself.
 */
function standInCli({ profile, fixture, say, step, env }) {
  const { apiBase, key } = profile.standIn;
  const call = async (route, body) => {
    const response = await fetch(`${apiBase}/stand-in/${route}`, {
      method: body === undefined ? "GET" : "POST",
      headers: {
        Authorization: `Bearer ${key}`,
        ...(body === undefined ? {} : { "Content-Type": "application/json" }),
      },
      body: body === undefined ? undefined : JSON.stringify(body),
      signal: AbortSignal.timeout(60_000),
    });
    const answer = await response.json();
    if (!response.ok)
      throw new Error(`stand-in ${route}: HTTP ${response.status}`);
    return answer;
  };
  return {
    async printSecret() {
      const health = await fetch(`${apiBase}/stand-in/health`).catch(
        () => null,
      );
      if (!health?.ok) {
        throw new Refusal(
          `No stand-in answers at ${apiBase}. Start it first (see this script's header).`,
        );
      }
      return env.STRIPE_SANDBOX_RUN_TEST_WEBHOOK_SECRET ?? "";
    },
    async listen() {
      let stopped = false;
      let paidOut = false;
      const payout =
        step === "resume" && env.STRIPE_SANDBOX_RUN_TEST_PAYOUT === "1";
      const loop = (async () => {
        while (!stopped) {
          try {
            const state = await call("state");
            const ours = state.payments.filter(
              (payment) => payment.account === fixture.account,
            );
            for (const payment of ours.filter(
              (made) => made.timelines.length === 0,
            )) {
              const timeline =
                payment.method === "ach" ? "ach-succeeds" : "card-succeeds";
              await call(`payments/${payment.operation}/timeline`, {
                timeline,
              });
              say(`stand-in | ${timeline} for payment ${payment.operation}`);
            }
            const ready = ours.filter((made) => made.paid && !made.payout);
            if (payout && !paidOut && ready.length > 0) {
              const made = await call("payouts", {
                payments: ready.map((made) => made.operation),
              });
              paidOut = true;
              say(`stand-in | payout ${made.id} of ${made.net}`);
            }
          } catch (error) {
            say(`stand-in | ${error.message}`);
          }
          await sleep(1500);
        }
      })();
      return {
        async stop() {
          stopped = true;
          await loop;
        },
      };
    },
  };
}

async function promptMasked(question) {
  const { stdin, stderr } = process;
  stderr.write(question);
  stdin.setRawMode(true);
  stdin.resume();
  stdin.setEncoding("utf8");
  return new Promise((resolve, reject) => {
    let value = "";
    const finish = () => {
      stdin.off("data", onData);
      stdin.setRawMode(false);
      stdin.pause();
      stderr.write("\n");
    };
    const onData = (chunk) => {
      for (const character of chunk) {
        if (character === "\r" || character === "\n") {
          finish();
          resolve(value);
          return;
        }
        if (character === "\u0003") {
          finish();
          reject(new Interrupted());
          return;
        }
        if (character === "\u007f" || character === "\b")
          value = value.slice(0, -1);
        else value += character;
      }
    };
    stdin.on("data", onData);
  });
}

export function realDependencies({ env, signal, print }) {
  const port = Number(env.LEASEBOOK_DB_PORT ?? 5632);
  // Everything printed, a child's output included, passes through one mask that knows the run's
  // secrets once the run has them, and the patterns before that.
  const secrets = [];
  const mask = (text) => createMask(secrets)(text);
  const show = (prefix) => (line) => print(mask(`${prefix} | ${line}`));
  const dotnetEnv = (connection) => ({
    ...childEnvironment(env),
    ASPNETCORE_ENVIRONMENT: "Development",
    ConnectionStrings__Default: connection,
  });
  const deps = {
    env,
    signal,
    isInteractive: Boolean(process.stdin.isTTY),
    prompt: promptMasked,
    print: (line) => print(mask(line)),
    remember: (secret) => secrets.push(secret),
    now: () => Date.now(),
    sleep,
    randomBytes,
    readFile: (file) => readFileSync(file, "utf8"),
    tempDir: () => mkdtempSync(path.join(tmpdir(), "leasebook-sandbox-run-")),
    removeDir: (dir) => rmSync(dir, { recursive: true, force: true }),
    readResult(file) {
      let text;
      try {
        text = readFileSync(file, "utf8");
      } catch {
        return undefined;
      }
      try {
        return JSON.parse(text);
      } catch {
        return null;
      }
    },
    connection: (database, role) => connectionString(database, role, port),
    // The HTTP status something on this machine answers a URL with, or 0 for nothing.
    async answers(url) {
      try {
        const response = await fetch(url, {
          signal: AbortSignal.timeout(2000),
        });
        return response.status;
      } catch {
        return 0;
      }
    },
    async build() {
      const result = await capture(
        "dotnet",
        [
          "build",
          "src/LeaseBook.Web/LeaseBook.Web.csproj",
          "-c",
          "Debug",
          "--nologo",
          "-v",
          "q",
        ],
        { env: childEnvironment(env), signal },
      );
      if (result.code !== 0) {
        for (const line of `${result.stdout}\n${result.stderr}`
          .split("\n")
          .slice(-40))
          show("build")(line);
        throw new Refusal("The host did not build. Nothing was copied.");
      }
    },
    async copy(source, target) {
      await maintenance(copySql(source, target), (said) =>
        /being accessed by other users/.test(said)
          ? new Refusal(
              `${source} is in use: something is connected to it, such as a host started by hand, psql or pgAdmin. ` +
                "Postgres copies a database only while nothing is connected to it. Stop that, then start again. Nothing was copied.",
            )
          : /does not exist/.test(said)
            ? new Refusal(
                `There is no database ${source}. The runbook says how to create the fixture.`,
              )
            : new Refusal(`Could not copy ${source}: ${said}`),
      );
    },
    async comment(target, record) {
      await maintenance(commentSql(target, record), (said) => new Error(said));
    },
    async readComment(target) {
      return (
        await maintenance(
          `SELECT shobj_description(oid, 'pg_database') FROM pg_database WHERE datname = '${assertRunDatabase(target)}';`,
          (said) => new Error(said),
        )
      ).trim();
    },
    async exists(target) {
      const found = await maintenance(
        `SELECT count(*) FROM pg_database WHERE datname = '${assertRunDatabase(target)}';`,
        (said) => new Error(said),
      );
      return found.trim() === "1";
    },
    async connections(target) {
      const count = await maintenance(
        `SELECT count(*) FROM pg_stat_activity WHERE datname = '${assertRunDatabase(target)}';`,
        (said) => new Error(said),
      );
      return Number(count.trim());
    },
    async drop(target) {
      await maintenance(dropSql(target), (said) => new Error(said));
    },
    async listRuns() {
      const out = await maintenance(
        "SELECT datname, coalesce(shobj_description(oid, 'pg_database'), '') FROM pg_database " +
          `WHERE datname LIKE '${RUN_PREFIX.replaceAll("_", "\\_")}%' ORDER BY datname;`,
        (said) => new Error(said),
      );
      return out
        .split("\n")
        .filter((line) => line.trim())
        .map((line) => {
          const [name, ...comment] = line.split("|");
          return { name, comment: comment.join("|") };
        });
    },
    async migrate(target) {
      const result = await capture(
        "dotnet",
        [
          "ef",
          "database",
          "update",
          "--project",
          "src/LeaseBook.Web",
          "--context",
          "AppDbContext",
          "--no-build",
        ],
        {
          env: {
            ...dotnetEnv(connectionString(target, "leasebook_app", port)),
            ConnectionStrings__Migrations: connectionString(
              target,
              "leasebook_migrator",
              port,
            ),
          },
          signal,
        },
      );
      const said = `${result.stdout}\n${result.stderr}`.split("\n");
      for (const line of said.filter((text) =>
        /Applying migration|No migrations were applied|Done\./.test(text),
      )) {
        show("migrate")(line.trim());
      }
      if (result.code !== 0) {
        for (const line of said.slice(-30)) show("migrate")(line);
        throw new Refusal(`Migrating ${target} failed.`);
      }
    },
    startHost(hostEnv) {
      const counts = { completed: 0, failed: 0 };
      const handle = launch(
        "dotnet",
        [
          "run",
          "--project",
          "src/LeaseBook.Web",
          "--no-launch-profile",
          "--no-build",
          "--urls",
          HOST_URL,
        ],
        {
          env: hostEnv,
          onLine: (line) => {
            if (/\[4606\]/.test(line)) counts.completed += 1;
            if (/\[4607\]/.test(line)) counts.failed += 1;
            show("host")(line);
          },
        },
      );
      return { ...handle, sweeps: () => ({ ...counts }) };
    },
    playwright(playwrightEnv) {
      // The list reporter writes no report to disk, and an HTML report would wait to be opened.
      // Playwright's own binary and not `npx`, which would pass a Ctrl-C on as a second one.
      const runner = launch(
        path.join(webRoot, "node_modules", ".bin", "playwright"),
        [
          "test",
          "stripe-sandbox.spec.ts",
          "--reporter=list",
          // Its failure context holds page text; it goes with the run's temporary directory.
          `--output=${path.join(path.dirname(playwrightEnv.STRIPE_SANDBOX_E2E_RESULT), "test-results")}`,
        ],
        {
          cwd: webRoot,
          env: playwrightEnv,
          onLine: show("playwright"),
        },
      );
      // One SIGINT to the runner alone, which then stops the SPA it started.
      return {
        ...runner,
        stop: () => runner.stop("SIGINT", 60_000, { group: false }),
      };
    },
    async invariants(connection) {
      const result = await capture(
        "dotnet",
        [
          "run",
          "--project",
          "src/LeaseBook.Web",
          "--no-launch-profile",
          "--no-build",
          "--",
          "check-invariants",
          "--all",
        ],
        { env: dotnetEnv(connection), signal },
      );
      for (const line of `${result.stdout}\n${result.stderr}`
        .split("\n")
        .filter(Boolean)) {
        show("invariants")(line);
      }
      return { code: result.code };
    },
    async balance({ key, account }) {
      if (sourceProfile(env).standIn) return testSourceBalance(env);
      const response = await fetch("https://api.stripe.com/v1/balance", {
        headers: { Authorization: `Bearer ${key}`, "Stripe-Account": account },
        redirect: "error",
        signal: AbortSignal.timeout(30_000),
      });
      const body = await response.json().catch(() => ({}));
      if (!response.ok) {
        throw new Refusal(
          `Stripe refused the balance request (HTTP ${response.status}${body?.error?.code ? `, ${body.error.code}` : ""}). Nothing was charged.`,
        );
      }
      return body;
    },
    cli: ({ profile, fixture, say, step }) =>
      stripeCli({ profile, fixture, say, step, env, mask }),
  };
  return deps;
}

/** The test source's quiet check answers from an environment variable and never asks Stripe. */
function testSourceBalance(env) {
  const pending = Number(env.STRIPE_SANDBOX_RUN_TEST_PENDING_CENTS ?? 0);
  return {
    object: "balance",
    livemode: false,
    available: [{ amount: 0, currency: "usd" }],
    pending: [
      { amount: Number.isInteger(pending) ? pending : 0, currency: "usd" },
    ],
  };
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  const controller = new AbortController();
  let signals = 0;
  const onSignal = (name) => {
    signals += 1;
    if (signals === 1) {
      console.error(`\n${name}: stopping the run and cleaning up.`);
      controller.abort();
    } else {
      console.error("Still cleaning up; wait for it to finish.");
    }
  };
  for (const name of ["SIGINT", "SIGTERM", "SIGHUP"])
    process.on(name, () => onSignal(name));
  const deps = realDependencies({
    env: process.env,
    signal: controller.signal,
    print: (line) => console.log(line),
  });
  main(process.argv.slice(2), deps).then(
    (code) => process.exit(code),
    (error) => {
      const known = error instanceof Refusal || error instanceof Interrupted;
      console.error(
        createMask([process.env.STRIPE_SANDBOX_SECRET_KEY])(
          known ? error.message : (error?.stack ?? String(error)),
        ),
      );
      process.exit(1);
    },
  );
}
