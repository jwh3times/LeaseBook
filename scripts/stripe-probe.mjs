#!/usr/bin/env node

/**
 * Probe a Stripe sandbox for the facts the payment adapter depends on, and
 * record what Stripe actually sends.
 *
 * Usage (from the repository root):
 *
 *   node scripts/stripe-probe.mjs run [--wait-minutes=10] [--no-listen] [--port=4242]
 *   node scripts/stripe-probe.mjs payouts <run-directory>
 *   node scripts/stripe-probe.mjs scrub <run-directory>
 *
 * `run` makes test charges on the connected account and writes every request,
 * response and webhook delivery under `stripe-probe.local/<run>/`, which git
 * ignores, with a `findings.md` that answers the open questions. `payouts`
 * adds payout evidence to a run later, once Stripe has paid out. `scrub`
 * copies a run to `tests/fixtures/stripe/<run>/` with every identifier
 * replaced by a synthetic one, and refuses to finish if any real one is left.
 *
 * It reads STRIPE_SANDBOX_SECRET_KEY and STRIPE_SANDBOX_CONNECTED_ACCOUNT. It
 * refuses a key that is not a test-mode key, stops at the first object Stripe
 * marks as live, and never writes the key or a signing secret to disk. It
 * never creates a payout: which charges a payout covers must come from Stripe.
 *
 * docs/runbooks/stripe-sandbox-probe.md owns the procedure.
 */

import { spawn, spawnSync } from "node:child_process";
import { createHmac, randomUUID, timingSafeEqual } from "node:crypto";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { createServer } from "node:http";
import { basename, dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

/** The API version the adapter's library is pinned to, so recorded shapes match what it will parse. */
export const API_VERSION = "2026-09-30.endive";
export const FIXTURE_ROOT = join("tests", "fixtures", "stripe");
const RAW_ROOT = "stripe-probe.local";
const API = "https://api.stripe.com";

// --- Pure helpers, exported for the tests -------------------------------------------------

export function assertTestKey(key) {
  if (typeof key !== "string" || !/^(sk|rk)_test_.+/.test(key)) {
    throw new Error(
      "STRIPE_SANDBOX_SECRET_KEY must be a Stripe test-mode secret key (sk_test_ or rk_test_).",
    );
  }
}

/** Stripe's form encoding: `a[b]=1`, `list[]=x`. Null and undefined are left out. */
export function encodeForm(params, prefix = "") {
  const parts = [];
  for (const [key, value] of Object.entries(params ?? {})) {
    const name = prefix ? `${prefix}[${key}]` : key;
    if (value === null || value === undefined) continue;
    if (Array.isArray(value)) {
      for (const item of value) {
        parts.push(
          `${encodeURIComponent(`${name}[]`)}=${encodeURIComponent(item)}`,
        );
      }
    } else if (typeof value === "object") {
      parts.push(encodeForm(value, name));
    } else {
      parts.push(`${encodeURIComponent(name)}=${encodeURIComponent(value)}`);
    }
  }
  return parts.filter(Boolean).join("&");
}

/** True when anything in the value says it exists in live mode. */
export function saysLive(value) {
  if (Array.isArray(value)) return value.some(saysLive);
  if (value && typeof value === "object") {
    return value.livemode === true || Object.values(value).some(saysLive);
  }
  return false;
}

/** Stripe's webhook signature: HMAC-SHA256 of `<t>.<body>` under the endpoint secret. */
export function verifySignature(
  body,
  header,
  secret,
  nowSeconds,
  toleranceSeconds = 300,
) {
  const fields = Object.fromEntries(
    String(header ?? "")
      .split(",")
      .map((part) => part.trim().split("=", 2)),
  );
  const timestamp = Number(fields.t);
  if (!Number.isInteger(timestamp) || !fields.v1) return false;
  if (Math.abs(nowSeconds - timestamp) > toleranceSeconds) return false;
  const expected = createHmac("sha256", secret)
    .update(`${timestamp}.${body}`)
    .digest();
  let supplied;
  try {
    supplied = Buffer.from(fields.v1, "hex");
  } catch {
    return false;
  }
  return (
    supplied.length === expected.length && timingSafeEqual(supplied, expected)
  );
}

// An identifier is a short lowercase prefix, an underscore, and at least ten letters and digits
// that are not all lowercase. Enumeration values such as `verify_with_microdeposits` are all
// lowercase, so they are left alone; a real identifier of 14 or more characters that happens to be
// all lowercase is about one in a billion.
const IDENTIFIER = /\b([a-z]{2,12})_(?:test_)?([A-Za-z0-9]{10,})\b/g;
// The payment methods and tokens Stripe documents for testing. They name behaviour, not an account.
const DOCUMENTED_TEST_TOKEN =
  /^(pm_card_|pm_usBankAccount_|tok_|btok_)[A-Za-z_]+$/;
const SYNTHETIC = /^[a-z]{2,12}_synthetic\d{6}$/;
const EMAIL = /[^\s@"<>]+@[^\s@"<>]+\.[^\s@"<>]+/g;
const URL_LIKE = /https?:\/\/[^\s"<>]*/gi;
const IPV4 = /\b\d{1,3}(?:\.\d{1,3}){3}\b/g;

// Values removed whole, whatever they hold: they identify a person, a bank account or a device,
// or they grant access.
const REDACTED_KEYS = new Set([
  "account_holder_name",
  "address",
  "authorization_code",
  "bank_name",
  "billing_details",
  "business_profile",
  "calculated_statement_descriptor",
  "client_secret",
  "company",
  "dob",
  "email",
  "external_accounts",
  "fingerprint",
  "first_name",
  "individual",
  "ip",
  "ip_address",
  "last4",
  "last_name",
  "name",
  "network_transaction_id",
  "phone",
  "receipt_email",
  "receipt_url",
  "reference",
  "reference_number",
  "routing_number",
  "shipping",
  "ssn_last_4",
  "statement_descriptor",
  "statement_descriptor_suffix",
  "support_address",
  "support_email",
  "support_phone",
  "support_url",
  "tos_acceptance",
  "trace_id",
  "url",
  "user_agent",
]);

function isIdentifier(token, tail) {
  return !DOCUMENTED_TEST_TOKEN.test(token) && !/^[a-z]+$/.test(tail);
}

/**
 * Replaces every identifier with a synthetic one, the same one each time it recurs, and removes
 * the values that identify a person, a bank account or a device.
 */
export function createScrubber() {
  const known = new Map();
  const counters = new Map();

  function synthetic(token, prefix) {
    if (!known.has(token)) {
      const next = (counters.get(prefix) ?? 0) + 1;
      counters.set(prefix, next);
      known.set(token, `${prefix}_synthetic${String(next).padStart(6, "0")}`);
    }
    return known.get(token);
  }

  // Addresses of any kind go first, so that an identifier inside a URL is never mapped and kept.
  function text(value) {
    return value
      .replace(URL_LIKE, "[scrubbed]")
      .replace(EMAIL, "[scrubbed]")
      .replace(IPV4, "[scrubbed]")
      .replace(IDENTIFIER, (token, prefix, tail) =>
        isIdentifier(token, tail) ? synthetic(token, prefix) : token,
      );
  }

  function scrub(value, key = "") {
    if (REDACTED_KEYS.has(key)) return value === null ? null : "[scrubbed]";
    if (typeof value === "string") return text(value);
    if (Array.isArray(value)) return value.map((item) => scrub(item));
    if (value && typeof value === "object") {
      return Object.fromEntries(
        Object.entries(value).map(([name, inner]) => [
          text(name),
          scrub(inner, name),
        ]),
      );
    }
    return value;
  }

  return { scrub, text };
}

/** Everything in the text that a scrubbed fixture must not contain. Empty when it is clean. */
export function unsynthetic(text) {
  const found = [];
  for (const match of text.matchAll(IDENTIFIER)) {
    const [token, , tail] = match;
    if (isIdentifier(token, tail) && !SYNTHETIC.test(token)) found.push(token);
  }
  for (const [name, pattern] of [
    ["an email address", EMAIL],
    ["a URL", URL_LIKE],
    ["an IP address", IPV4],
  ]) {
    if (text.search(pattern) !== -1) found.push(name);
  }
  return [...new Set(found)];
}

/** Every `.json` file under a directory, deepest last. A missing directory holds none. */
export function jsonFiles(directory) {
  if (!existsSync(directory)) return [];
  return readdirSync(directory)
    .sort()
    .flatMap((entry) => {
      const path = join(directory, entry);
      if (statSync(path).isDirectory()) return jsonFiles(path);
      return entry.endsWith(".json") ? [path] : [];
    });
}

// --- The probe ----------------------------------------------------------------------------

function createRun(root) {
  const id = new Date().toISOString().replace(/[:.]/g, "-");
  const directory = join(root, RAW_ROOT, id);
  mkdirSync(directory, { recursive: true });
  return openRun(directory);
}

function openRun(directory) {
  let sequence = jsonFiles(directory).length;
  const findings = [];
  return {
    directory,
    record(label, value) {
      sequence += 1;
      const name = `${String(sequence).padStart(3, "0")}-${label}.json`;
      writeFileSync(
        join(directory, name),
        `${JSON.stringify(value, null, 2)}\n`,
      );
      return name;
    },
    find(question, answer) {
      findings.push({ question, answer });
      console.log(`- ${question}\n    ${answer}`);
    },
    findings,
  };
}

function createClient({ key, account, run }) {
  return async function call(method, path, options = {}) {
    const {
      params,
      platform = false,
      idempotencyKey,
      label = "call",
    } = options;
    const headers = {
      Authorization: `Bearer ${key}`,
      "Stripe-Version": API_VERSION,
    };
    if (!platform) headers["Stripe-Account"] = account;
    if (idempotencyKey) headers["Idempotency-Key"] = idempotencyKey;
    let url = API + path;
    let body;
    const form = encodeForm(params);
    if (method === "GET") {
      if (form) url += `?${form}`;
    } else {
      body = form;
      headers["Content-Type"] = "application/x-www-form-urlencoded";
    }
    const started = Date.now();
    const response = await fetch(url, { method, headers, body });
    const raw = await response.text();
    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch {
      parsed = { unparsed: raw.slice(0, 2000) };
    }
    if (saysLive(parsed)) {
      throw new Error(
        `Stripe returned a live-mode object for ${method} ${path}. Stopping.`,
      );
    }
    const file = run.record(label, {
      request: {
        method,
        path,
        params: params ?? null,
        platform,
        idempotencyKey: idempotencyKey ?? null,
      },
      status: response.status,
      requestId: response.headers.get("request-id"),
      stripeVersion: response.headers.get("stripe-version"),
      elapsedMs: Date.now() - started,
      body: parsed,
    });
    return { ok: response.ok, status: response.status, body: parsed, file };
  };
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

function describeError(result) {
  const error = result.body?.error;
  return error
    ? `HTTP ${result.status}, ${error.type ?? "error"} ${error.code ?? ""}: ${error.message ?? ""}`.trim()
    : `HTTP ${result.status}`;
}

async function preflight(context) {
  const { call, run, account } = context;
  const platform = await call("GET", "/v1/account", {
    platform: true,
    label: "platform-account",
  });
  if (!platform.ok) {
    throw new Error(
      `The key was refused (${describeError(platform)}). If the API version is the cause, the ` +
        `adapter's pinned version ${API_VERSION} is wrong for this account; say so on the issue.`,
    );
  }
  const connected = await call("GET", `/v1/accounts/${account}`, {
    platform: true,
    label: "connected-account",
  });
  if (!connected.ok)
    throw new Error(
      `The connected account was not found (${describeError(connected)}).`,
    );
  const body = connected.body;
  const capabilities = body.capabilities ?? {};
  run.find(
    "Is the connected account ready for card and ACH direct charges?",
    `charges_enabled=${body.charges_enabled}, payouts_enabled=${body.payouts_enabled}, ` +
      `card_payments=${capabilities.card_payments}, ` +
      `us_bank_account_ach_payments=${capabilities.us_bank_account_ach_payments}, ` +
      `fee payer=${body.controller?.fees?.payer}, ` +
      `payout schedule=${JSON.stringify(body.settings?.payouts?.schedule ?? null)}`,
  );
  if (body.controller?.fees?.payer !== "account") {
    run.find(
      "WARNING",
      "The connected account does not pay its own Stripe fees. The fee model assumes it does; " +
        "create a new connected account as the procedure describes.",
    );
  }
}

async function charge(context, scenario) {
  const { call, run } = context;
  const operation = randomUUID();
  const params = {
    amount: scenario.amount,
    currency: "usd",
    payment_method: scenario.paymentMethod,
    payment_method_types: [scenario.type],
    confirm: true,
    metadata: {
      leasebook_operation: operation,
      leasebook_generation: context.generation,
    },
    ...(scenario.type === "us_bank_account"
      ? { mandate_data: { customer_acceptance: { type: "offline" } } }
      : {}),
  };
  const started = Date.now();
  const created = await call("POST", "/v1/payment_intents", {
    params,
    idempotencyKey: operation,
    label: `${scenario.name}-create`,
  });
  if (!created.ok) {
    run.find(
      `${scenario.name}: is ${scenario.paymentMethod} accepted on the connected account?`,
      `No: ${describeError(created)} (${created.file})`,
    );
    return { operation, params, intent: null };
  }
  let intent = created.body;
  const seen = [intent.status];
  const deadline = Date.now() + context.waitMinutes * 60_000;
  while (intent.status === "processing") {
    if (Date.now() >= deadline) break;
    await sleep(15_000);
    const polled = await call("GET", `/v1/payment_intents/${intent.id}`, {
      label: `${scenario.name}-poll`,
    });
    if (!polled.ok) break;
    intent = polled.body;
    if (seen.at(-1) !== intent.status) seen.push(intent.status);
  }
  const seconds = Math.round((Date.now() - started) / 1000);
  run.find(
    `${scenario.name}: how does a ${scenario.paymentMethod} payment progress?`,
    `${seen.join(" -> ")} after ${seconds}s` +
      (intent.next_action ? `; next_action=${intent.next_action.type}` : "") +
      (intent.last_payment_error
        ? `; error=${intent.last_payment_error.code}`
        : "") +
      ` (${created.file})`,
  );
  if (intent.latest_charge) {
    const id =
      typeof intent.latest_charge === "string"
        ? intent.latest_charge
        : intent.latest_charge.id;
    const charged = await call("GET", `/v1/charges/${id}`, {
      params: { expand: ["balance_transaction"] },
      label: `${scenario.name}-charge`,
    });
    const transaction = charged.body?.balance_transaction;
    if (charged.ok && transaction && typeof transaction === "object") {
      run.find(
        `${scenario.name}: what fee did Stripe take, and from whom?`,
        `amount=${transaction.amount}, fee=${transaction.fee}, net=${transaction.net}, ` +
          `status=${transaction.status}, fee_details=${JSON.stringify(transaction.fee_details)} ` +
          `(${charged.file})`,
      );
    } else {
      run.find(
        `${scenario.name}: what fee did Stripe take?`,
        `No balance transaction yet (charge status ${charged.body?.status}) (${charged.file})`,
      );
    }
  }
  return { operation, params, intent };
}

async function idempotency(context, first) {
  const { call, run } = context;
  if (!first.intent) return;
  const replay = await call("POST", "/v1/payment_intents", {
    params: first.params,
    idempotencyKey: first.operation,
    label: "idempotent-replay",
  });
  run.find(
    "Does the same idempotency key with the same request return the first result?",
    replay.ok && replay.body.id === first.intent.id
      ? `Yes, the same payment intent (${replay.file})`
      : `No: ${describeError(replay)} (${replay.file})`,
  );
  const changed = await call("POST", "/v1/payment_intents", {
    params: { ...first.params, amount: first.params.amount + 100 },
    idempotencyKey: first.operation,
    label: "idempotent-changed",
  });
  run.find(
    "What does the same idempotency key with a different amount return?",
    `${describeError(changed)} (${changed.file})`,
  );
}

async function recovery(context, first, startedSeconds) {
  const { call, run } = context;
  if (!first.intent) return;
  const listed = await call("GET", "/v1/payment_intents", {
    params: { created: { gte: startedSeconds - 60 }, limit: 100 },
    label: "recovery-list",
  });
  const match = (listed.body?.data ?? []).filter(
    (intent) => intent.metadata?.leasebook_operation === first.operation,
  );
  run.find(
    "Can a payment be found again by listing the creation window and matching our operation id?",
    listed.ok
      ? `${match.length} match(es) among ${listed.body.data.length} listed, has_more=${listed.body.has_more} (${listed.file})`
      : `No: ${describeError(listed)} (${listed.file})`,
  );
  const searched = await call("GET", "/v1/payment_intents/search", {
    params: { query: `metadata['leasebook_operation']:'${first.operation}'` },
    label: "recovery-search",
  });
  run.find(
    "Does the Search API work on a connected account? (The adapter does not use it.)",
    searched.ok
      ? `Yes, ${searched.body.data?.length ?? 0} result(s) so soon after creation (${searched.file})`
      : `No: ${describeError(searched)} (${searched.file})`,
  );
}

async function dispute(context, charged) {
  const { call, run } = context;
  if (!charged.intent) return;
  const started = Date.now();
  const deadline = started + context.waitMinutes * 60_000;
  let found = null;
  let file = "";
  for (;;) {
    const listed = await call("GET", "/v1/disputes", {
      params: { payment_intent: charged.intent.id, limit: 10 },
      label: "ach-dispute-poll",
    });
    file = listed.file;
    found = listed.body?.data?.[0] ?? null;
    if (found || Date.now() >= deadline) break;
    await sleep(20_000);
  }
  run.find(
    "How does an ACH return after success appear?",
    found
      ? `A dispute after ${Math.round((Date.now() - started) / 1000)}s: reason=${found.reason}, ` +
          `status=${found.status}, amount=${found.amount} (${file})`
      : `No dispute within ${context.waitMinutes} minute(s). Rerun with a longer --wait-minutes.`,
  );
}

export async function payouts(context) {
  const { call, run } = context;
  const balance = await call("GET", "/v1/balance", { label: "balance" });
  if (balance.ok) {
    run.find(
      "What is the connected account's balance?",
      `available=${JSON.stringify(balance.body.available)}, pending=${JSON.stringify(balance.body.pending)} (${balance.file})`,
    );
  }
  const listed = await call("GET", "/v1/payouts", {
    params: { limit: 20 },
    label: "payouts",
  });
  const all = listed.body?.data ?? [];
  const automatic = all.filter((payout) => payout.automatic);
  run.find(
    "Does the sandbox produce automatic payouts?",
    listed.ok
      ? `${automatic.length} automatic of ${all.length} payout(s): ` +
          (all
            .map(
              (p) =>
                `${p.status}/${p.automatic ? "automatic" : "manual"}/${p.amount}`,
            )
            .join(", ") || "none yet. Rerun `payouts` on a later day.") +
          ` (${listed.file})`
      : `Could not list payouts: ${describeError(listed)} (${listed.file})`,
  );
  for (const payout of automatic.slice(0, 3)) {
    const lines = [];
    let after;
    let pages = 0;
    let last;
    do {
      last = await call("GET", "/v1/balance_transactions", {
        params: {
          payout: payout.id,
          limit: 100,
          starting_after: after,
          expand: ["data.source"],
        },
        label: "payout-lines",
      });
      if (!last.ok) break;
      lines.push(...last.body.data);
      after = last.body.data.at(-1)?.id;
      pages += 1;
    } while (last.body.has_more && pages < 20);
    const net = lines.reduce(
      (sum, line) => sum + (line.type === "payout" ? 0 : line.net),
      0,
    );
    run.find(
      "Does Stripe list everything an automatic payout covers, with gross, fee and net?",
      last.ok
        ? `${lines.length} line(s) of types ${[...new Set(lines.map((l) => l.type))].join(", ")}; ` +
            `their net sums to ${net} against a payout of ${payout.amount}; ` +
            `status=${payout.status}, reconciliation_status=${payout.reconciliation_status} (${last.file})`
        : `No: ${describeError(last)} (${last.file})`,
    );
  }
}

async function events(context, startedSeconds) {
  const { call, run } = context;
  const all = [];
  let after;
  let last;
  do {
    last = await call("GET", "/v1/events", {
      params: {
        created: { gte: startedSeconds - 60 },
        limit: 100,
        starting_after: after,
      },
      label: "events-api",
    });
    if (!last.ok) break;
    all.push(...last.body.data);
    after = last.body.data.at(-1)?.id;
  } while (last.body.has_more && all.length < 1000);
  run.find(
    "Which events did the account produce, oldest first?",
    all.length
      ? all
          .reverse()
          .map((event) => event.type)
          .join(", ")
      : `None listed (${last.ok ? "empty" : describeError(last)})`,
  );
  if (all.length) {
    run.find(
      "Do events fetched from the API name the connected account and the API version?",
      `account present on ${all.filter((e) => e.account).length} of ${all.length}; ` +
        `api_version values: ${[...new Set(all.map((e) => e.api_version))].join(", ")}`,
    );
  }
}

async function startListener(context, port) {
  const { run } = context;
  const secret = spawnSync("stripe", ["listen", "--print-secret"], {
    encoding: "utf8",
  });
  const signing =
    secret.status === 0 ? secret.stdout.trim().split(/\s+/).at(-1) : null;
  if (!signing?.startsWith("whsec_")) {
    run.find(
      "Were webhook deliveries captured?",
      "No: the Stripe CLI is not installed or not signed in, so `stripe listen` could not start.",
    );
    return null;
  }
  const deliveries = [];
  const server = createServer((request, response) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      const signature = request.headers["stripe-signature"] ?? "";
      deliveries.push({
        route: request.url,
        verified: verifySignature(
          body,
          signature,
          signing,
          Math.floor(Date.now() / 1000),
        ),
        signed: Boolean(signature),
        body,
      });
      response.writeHead(200).end();
    });
  });
  await new Promise((ready) => server.listen(port, "127.0.0.1", ready));
  const child = spawn(
    "stripe",
    [
      "listen",
      "--latest",
      "--forward-to",
      `localhost:${port}/account`,
      "--forward-connect-to",
      `localhost:${port}/connect`,
    ],
    { stdio: "ignore" },
  );
  await sleep(5000);
  return {
    async stop() {
      await sleep(10_000);
      child.kill();
      await new Promise((closed) => server.close(closed));
      const parsed = [];
      for (const delivery of deliveries) {
        let event;
        try {
          event = JSON.parse(delivery.body);
        } catch {
          event = { unparsed: true };
        }
        if (saysLive(event))
          throw new Error("A live-mode event was delivered. Stopping.");
        parsed.push(event);
        run.record(`webhook${delivery.route.replace(/\W+/g, "-")}`, {
          route: delivery.route,
          signed: delivery.signed,
          verified: delivery.verified,
          event,
        });
      }
      const by = (route) =>
        deliveries.filter((delivery) => delivery.route === route);
      const summary = (route) =>
        `${by(route).filter((d) => d.verified).length} of ${by(route).length} verified`;
      run.find(
        "Are Connect and account deliveries signed with the one secret `stripe listen` prints?",
        `connect route: ${summary("/connect")}; account route: ${summary("/account")}`,
      );
      if (parsed.length) {
        run.find(
          "Do delivered events name the connected account, the mode and the API version?",
          `account present on ${parsed.filter((e) => e.account).length} of ${parsed.length}; ` +
            `livemode values: ${[...new Set(parsed.map((e) => e.livemode))].join(", ")}; ` +
            `api_version values: ${[...new Set(parsed.map((e) => e.api_version))].join(", ")}`,
        );
        run.find(
          "In what order were events delivered?",
          parsed.map((event) => event.type).join(", "),
        );
      }
    },
  };
}

function writeFindings(run) {
  const { text } = createScrubber();
  const lines = [
    "# Stripe sandbox probe findings",
    "",
    `Run: ${basename(run.directory)}. API version requested: ${API_VERSION}.`,
    "Identifiers below are synthetic. The files named are in this run's directory, which is not committed.",
    "",
    ...run.findings.flatMap(({ question, answer }) => [
      `## ${question}`,
      "",
      text(answer),
      "",
    ]),
  ];
  writeFileSync(join(run.directory, "findings.md"), lines.join("\n"));
}

function readEnvironment(environment) {
  const key = environment.STRIPE_SANDBOX_SECRET_KEY;
  const account = environment.STRIPE_SANDBOX_CONNECTED_ACCOUNT;
  assertTestKey(key);
  if (!/^acct_[A-Za-z0-9]+$/.test(account ?? "")) {
    throw new Error(
      "STRIPE_SANDBOX_CONNECTED_ACCOUNT must be a connected account id (acct_...).",
    );
  }
  return { key, account };
}

async function runProbe(root, options, environment) {
  const { key, account } = readEnvironment(environment);
  const run = createRun(root);
  const context = {
    run,
    account,
    call: createClient({ key, account, run }),
    waitMinutes: options.waitMinutes,
    generation: randomUUID(),
  };
  const startedSeconds = Math.floor(Date.now() / 1000);
  console.log(`Recording to ${run.directory}\n`);
  try {
    await preflight(context);
    const listener = options.listen
      ? await startListener(context, options.port)
      : null;
    const card = await charge(context, {
      name: "card",
      type: "card",
      amount: 51524,
      paymentMethod: "pm_card_visa",
    });
    await charge(context, {
      name: "card-available-now",
      type: "card",
      amount: 20000,
      paymentMethod: "pm_card_bypassPending",
    });
    await charge(context, {
      name: "card-declined",
      type: "card",
      amount: 20000,
      paymentMethod: "pm_card_visa_chargeDeclined",
    });
    await charge(context, {
      name: "ach",
      type: "us_bank_account",
      amount: 50403,
      paymentMethod: "pm_usBankAccount_success",
    });
    await charge(context, {
      name: "ach-insufficient-funds",
      type: "us_bank_account",
      amount: 20000,
      paymentMethod: "pm_usBankAccount_insufficientFunds",
    });
    const disputed = await charge(context, {
      name: "ach-dispute",
      type: "us_bank_account",
      amount: 20000,
      paymentMethod: "pm_usBankAccount_dispute",
    });
    await idempotency(context, card);
    await recovery(context, card, startedSeconds);
    await dispute(context, disputed);
    await payouts(context);
    await events(context, startedSeconds);
    if (listener) await listener.stop();
  } finally {
    writeFindings(run);
  }
  console.log(`\nFindings: ${join(run.directory, "findings.md")}`);
  return 0;
}

async function runPayouts(directory, environment) {
  const { key, account } = readEnvironment(environment);
  const run = openRun(directory);
  await payouts({ run, account, call: createClient({ key, account, run }) });
  const { text } = createScrubber();
  const added = run.findings.flatMap(({ question, answer }) => [
    `## ${question}`,
    "",
    text(answer),
    "",
  ]);
  const path = join(directory, "findings.md");
  const before = existsSync(path)
    ? readFileSync(path, "utf8")
    : "# Stripe sandbox probe findings\n";
  writeFileSync(path, `${before}\n${added.join("\n")}`);
  return 0;
}

/** Copies a run to the fixture directory, scrubbed. Throws if anything real is left. */
export function scrubRun(directory, fixtureRoot, check = unsynthetic) {
  const { scrub } = createScrubber();
  const target = join(fixtureRoot, basename(directory));
  const written = [];
  for (const file of jsonFiles(directory)) {
    const scrubbed = `${JSON.stringify(scrub(JSON.parse(readFileSync(file, "utf8"))), null, 2)}\n`;
    const left = check(scrubbed);
    if (left.length) {
      throw new Error(
        `${basename(file)} still holds ${left.join(", ")} after scrubbing. Nothing was written.`,
      );
    }
    written.push([join(target, basename(file)), scrubbed]);
  }
  mkdirSync(target, { recursive: true });
  for (const [path, content] of written) writeFileSync(path, content);
  return written.map(([path]) => path);
}

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  const options = {
    command,
    waitMinutes: 10,
    listen: true,
    port: 4242,
    directory: null,
  };
  for (const argument of rest) {
    if (argument.startsWith("--wait-minutes="))
      options.waitMinutes = Number(argument.split("=")[1]);
    else if (argument === "--no-listen") options.listen = false;
    else if (argument.startsWith("--port="))
      options.port = Number(argument.split("=")[1]);
    else if (!argument.startsWith("--") && !options.directory)
      options.directory = argument;
    else throw new Error(`Unknown argument: ${argument}`);
  }
  const needsDirectory = command === "payouts" || command === "scrub";
  if (
    !["run", "payouts", "scrub"].includes(command) ||
    (needsDirectory && !options.directory) ||
    (!needsDirectory && options.directory) ||
    !(options.waitMinutes >= 0 && options.waitMinutes <= 120) ||
    !Number.isInteger(options.port)
  ) {
    throw new Error(
      "Usage: stripe-probe.mjs run [--wait-minutes=10] [--no-listen] [--port=4242]\n" +
        "       stripe-probe.mjs payouts <run-directory>\n" +
        "       stripe-probe.mjs scrub <run-directory>",
    );
  }
  return options;
}

export async function main(argv, { environment = process.env, root } = {}) {
  const repository =
    root ?? resolve(dirname(fileURLToPath(import.meta.url)), "..");
  const options = parseArgs(argv);
  if (options.command === "run")
    return runProbe(repository, options, environment);
  const directory = resolve(options.directory);
  if (!existsSync(directory))
    throw new Error(`No such run directory: ${directory}`);
  if (options.command === "payouts") return runPayouts(directory, environment);
  const written = scrubRun(directory, join(repository, FIXTURE_ROOT));
  console.log(
    `Wrote ${written.length} scrubbed file(s). Read the diff before committing it.`,
  );
  return 0;
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (error) => {
      console.error(`stripe-probe: ${error.message}`);
      process.exit(1);
    },
  );
}
