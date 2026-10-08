#!/usr/bin/env node

/**
 * Probe a Stripe sandbox for the facts the payment adapter depends on, and
 * record what Stripe actually sends.
 *
 * Usage (from the repository root):
 *
 *   node scripts/stripe-probe.mjs run [--only=card,ach] [--wait-minutes=10] [--no-listen] [--port=4242]
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

/** Waiting goes through here so that the tests do not wait. */
export const timing = {
  sleep: (ms) => new Promise((done) => setTimeout(done, ms)),
};

/** A reason to stop the whole run, as opposed to one step of it. */
class Stop extends Error {}

// --- Pure helpers, exported for the tests -------------------------------------------------

export function assertTestKey(key) {
  if (typeof key !== "string" || !/^(sk|rk)_test_.+/.test(key)) {
    throw new Stop(
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
  const supplied = Buffer.from(fields.v1, "hex");
  return (
    supplied.length === expected.length && timingSafeEqual(supplied, expected)
  );
}

// An identifier is a short prefix, an underscore, and at least eight letters and digits that are
// not all lowercase letters. Enumeration values such as `verify_with_microdeposits` are all
// lowercase, so they are left alone; a real identifier that long and all lowercase is rarer than
// one in a million. It is found wherever it sits in a word, so `pi_x_secret_y` and `group_pi_x`
// are caught as well.
const IDENTIFIER =
  /(?<![A-Za-z0-9])([A-Za-z][A-Za-z0-9]{1,15})_(?:test_)?([A-Za-z0-9]{8,})(?![A-Za-z0-9])/g;
const WORD = /[A-Za-z0-9_]+/g;
// The payment methods Stripe documents for testing. They name behaviour, not an account.
const DOCUMENTED_TEST_TOKEN = /^pm_(card|usBankAccount)_[A-Za-z_]+$/;
const SYNTHETIC = /^[a-z][a-z0-9]{1,15}_synthetic\d{6}$/;
const EMAIL = /[^\s@"<>]+@[^\s@"<>]+\.[^\s@"<>]+/g;
const URL_LIKE = /https?:\/\/[^\s"<>]*/gi;
const IPV4 = /\b\d{1,3}(?:\.\d{1,3}){3}\b/g;
// Anything shaped like a key or a signing secret, including the masked form Stripe echoes back.
const KEY_LIKE = /\b(?:sk|rk|pk|whsec)_[^\s"',]+/g;

// Values removed whole, whatever they hold: they identify a person, a business, a bank account or
// a device, or they grant access.
const REDACTED_KEYS = new Set([
  "account_holder_name",
  "account_number",
  "address",
  "authorization_code",
  "bank_name",
  "billing_address",
  "billing_details",
  "business_name",
  "business_profile",
  "calculated_statement_descriptor",
  "client_secret",
  "company",
  "customer_email_address",
  "customer_name",
  "customer_purchase_ip",
  "description",
  "display_name",
  "dob",
  "dynamic_last4",
  "email",
  "evidence",
  "external_accounts",
  "fingerprint",
  "first_name",
  "iin",
  "individual",
  "ip",
  "ip_address",
  "issuer",
  "last4",
  "last_name",
  "name",
  "network_transaction_id",
  "payment_reference",
  "phone",
  "receipt_email",
  "receipt_number",
  "receipt_url",
  "reference",
  "reference_number",
  "routing_number",
  "shipping",
  "shipping_address",
  "ssn_last_4",
  "statement_descriptor",
  "statement_descriptor_kana",
  "statement_descriptor_kanji",
  "statement_descriptor_prefix",
  "statement_descriptor_prefix_kana",
  "statement_descriptor_prefix_kanji",
  "statement_descriptor_suffix",
  "support_address",
  "support_email",
  "support_phone",
  "support_url",
  "timezone",
  "tos_acceptance",
  "trace_id",
  "uncategorized_text",
  "url",
  "user_agent",
]);

function isIdentifier(tail) {
  return !/^[a-z]+$/.test(tail);
}

// An account object describes a business. A fixture needs only what the adapter reads from one.
function accountOnly(account) {
  return {
    object: "account",
    id: account.id,
    charges_enabled: account.charges_enabled ?? null,
    payouts_enabled: account.payouts_enabled ?? null,
    capabilities: account.capabilities ?? null,
    controller: account.controller
      ? { fees: account.controller.fees ?? null }
      : null,
    settings: {
      payouts: { schedule: account.settings?.payouts?.schedule ?? null },
    },
  };
}

function ourMetadata(metadata) {
  return Object.fromEntries(
    Object.entries(metadata).filter(([name]) => name.startsWith("leasebook_")),
  );
}

/**
 * Replaces every identifier with a synthetic one, the same one each time it recurs, and removes
 * the values that identify a person, a business, a bank account or a device.
 */
export function createScrubber() {
  const known = new Map();
  const counters = new Map();

  function synthetic(token, prefix) {
    if (!known.has(token)) {
      const kind = prefix.toLowerCase();
      const next = (counters.get(kind) ?? 0) + 1;
      counters.set(kind, next);
      known.set(token, `${kind}_synthetic${String(next).padStart(6, "0")}`);
    }
    return known.get(token);
  }

  // Keys and addresses of any kind go first, so that an identifier inside one is never kept.
  function text(value) {
    return value
      .replace(KEY_LIKE, "[scrubbed]")
      .replace(URL_LIKE, "[scrubbed]")
      .replace(EMAIL, "[scrubbed]")
      .replace(IPV4, "[scrubbed]")
      .replace(WORD, (word) =>
        DOCUMENTED_TEST_TOKEN.test(word)
          ? word
          : word.replace(IDENTIFIER, (token, prefix, tail) =>
              isIdentifier(tail) ? synthetic(token, prefix) : token,
            ),
      );
  }

  function scrub(value, key = "") {
    if (REDACTED_KEYS.has(key)) return value === null ? null : "[scrubbed]";
    if (typeof value === "string") return text(value);
    if (Array.isArray(value)) return value.map((item) => scrub(item));
    if (value && typeof value === "object") {
      const kept = value.object === "account" ? accountOnly(value) : value;
      return Object.fromEntries(
        Object.entries(kept).map(([name, inner]) => [
          text(name),
          name === "metadata" && inner && typeof inner === "object"
            ? scrub(ourMetadata(inner))
            : scrub(inner, name),
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
  for (const word of text.match(WORD) ?? []) {
    if (DOCUMENTED_TEST_TOKEN.test(word)) continue;
    for (const [token, , tail] of word.matchAll(IDENTIFIER)) {
      if (isIdentifier(tail) && !SYNTHETIC.test(token)) found.push(token);
    }
  }
  for (const [name, pattern] of [
    ["a key or signing secret", KEY_LIKE],
    ["an email address", EMAIL],
    ["a URL", URL_LIKE],
    ["an IP address", IPV4],
  ]) {
    if (text.search(pattern) !== -1) found.push(name);
  }
  return [...new Set(found)];
}

/**
 * The exact strings a raw run holds that must not appear in its scrubbed copy: every value shaped
 * like an identifier, and every name, descriptor and address on an account. It works from the
 * recorded values and not from a pattern for what an identifier looks like, so it is a second
 * opinion on the scrubber and not the same opinion twice.
 */
export function realLiterals(
  value,
  found = new Set(),
  inAccount = false,
  key = "",
) {
  if (typeof value === "string") {
    const segments = value.split("_");
    const identifier =
      /^[A-Za-z0-9_]+$/.test(value) &&
      segments.length > 1 &&
      segments.some(
        (segment) => segment.length >= 8 && /[0-9A-Z]/.test(segment),
      ) &&
      !DOCUMENTED_TEST_TOKEN.test(value);
    const describesAccount =
      inAccount &&
      value.length >= 4 &&
      /name|descriptor|prefix|email|url|phone|line|city|postal|timezone/.test(
        key,
      );
    // Whatever sits where Stripe puts an identifier is one, whatever it looks like.
    const named =
      value.length >= 6 &&
      ["id", "account", "destination", "application"].includes(key);
    if (identifier || describesAccount || named) found.add(value);
  } else if (Array.isArray(value)) {
    for (const item of value) realLiterals(item, found, inAccount, key);
  } else if (value && typeof value === "object") {
    const account = inAccount || value.object === "account";
    for (const [name, inner] of Object.entries(value))
      realLiterals(inner, found, account, name);
  }
  return found;
}

/** Every `.json` file under a directory. A missing directory holds none. */
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

/** What the committed fixtures under a directory hold that they must not, one line per finding. */
export function fixtureOffenders(directory) {
  return jsonFiles(directory).flatMap((file) =>
    unsynthetic(readFileSync(file, "utf8")).map((found) => `${file}: ${found}`),
  );
}

/** Copies a run to the fixture directory, scrubbed. Throws, writing nothing, if anything real is left. */
export function scrubRun(directory, fixtureRoot) {
  const { scrub } = createScrubber();
  const target = join(fixtureRoot, basename(directory));
  const raw = jsonFiles(directory).map((file) => [
    file,
    JSON.parse(readFileSync(file, "utf8")),
  ]);
  const literals = [...realLiterals(raw.map(([, value]) => value))];
  const written = [];
  for (const [file, value] of raw) {
    const scrubbed = `${JSON.stringify(scrub(value), null, 2)}\n`;
    const left = [
      ...unsynthetic(scrubbed),
      ...literals.filter((literal) => scrubbed.includes(literal)),
    ];
    if (left.length) {
      throw new Stop(
        `${basename(file)} still holds ${left.length} real value(s) after scrubbing; the first ` +
          `begins "${left[0].slice(0, 4)}". Nothing was written. Report this on the issue.`,
      );
    }
    written.push([join(target, basename(file)), scrubbed]);
  }
  mkdirSync(target, { recursive: true });
  for (const [path, content] of written) writeFileSync(path, content);
  return written.map(([path]) => path);
}

// --- The probe ----------------------------------------------------------------------------

function openRun(directory) {
  mkdirSync(directory, { recursive: true });
  let sequence = jsonFiles(directory).length;
  const findings = [];
  const { text } = createScrubber();
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
    // Printed and kept in the same scrubbed form, so that neither the terminal nor the file holds
    // an identifier.
    find(question, answer) {
      const entry = { question: text(question), answer: text(answer) };
      findings.push(entry);
      console.log(`- ${entry.question}\n    ${entry.answer}`);
    },
    write() {
      const path = join(directory, "findings.md");
      const before = existsSync(path)
        ? readFileSync(path, "utf8")
        : [
            "# Stripe sandbox probe findings",
            "",
            `Run: ${basename(directory)}. API version requested: ${API_VERSION}.`,
            "Identifiers are synthetic. The files named are in the run's directory, which is not committed.",
            "",
          ].join("\n");
      const added = findings
        .splice(0)
        .flatMap(({ question, answer }) => [`## ${question}`, "", answer, ""]);
      writeFileSync(path, `${before}\n${added.join("\n")}`);
      return path;
    },
  };
}

function createClient({ key, account, run }) {
  return async function call(method, path, options = {}) {
    const {
      params,
      platform = false,
      idempotencyKey,
      label = "call",
      record = true,
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
    let response;
    // Every write carries an idempotency key or changes nothing twice, so a retry is safe.
    for (let attempt = 1; ; attempt += 1) {
      try {
        response = await fetch(url, { method, headers, body });
        break;
      } catch (error) {
        if (attempt === 4) {
          throw new Error(
            `no answer from Stripe for ${method} ${path} (${error.cause?.code ?? error.name})`,
          );
        }
        await timing.sleep(attempt * 2000);
      }
    }
    const raw = await response.text();
    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch {
      parsed = { unparsed: raw.slice(0, 2000) };
    }
    if (saysLive(parsed)) {
      throw new Stop(
        `Stripe returned a live-mode object for ${method} ${path}. Stopping.`,
      );
    }
    const result = {
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
    };
    const file = record ? run.record(label, result) : null;
    return {
      ok: response.ok,
      status: response.status,
      body: parsed,
      file,
      result,
    };
  };
}

function describeError(result) {
  const error = result.body?.error;
  return error
    ? `HTTP ${result.status}, ${error.type ?? "error"} ${error.code ?? ""}: ${error.message ?? ""}`.trim()
    : `HTTP ${result.status}`;
}

// One step failing is a finding. Only a Stop ends the run.
async function step(run, name, work) {
  try {
    return await work();
  } catch (error) {
    if (error instanceof Stop) throw error;
    run.find(`${name}: did the step finish?`, `No: ${error.message}`);
    return null;
  }
}

async function preflight(context) {
  const { call, run, account } = context;
  const platform = await call("GET", "/v1/account", {
    platform: true,
    label: "platform-account",
  });
  if (!platform.ok) {
    const error = platform.body?.error;
    throw new Stop(
      `Stripe refused the key (HTTP ${platform.status}, ${error?.code ?? error?.type ?? "no code"}). ` +
        `If the status is 400 and the cause is the API version, ${API_VERSION} is wrong for this ` +
        `account; say so on the issue.`,
    );
  }
  // A connected account with its own dashboard has its own keys, and one of them can read that
  // account too. It is not the platform's key: it cannot say who pays the fees, and charges made
  // with it are not direct charges through a platform.
  if (platform.body?.id === account) {
    throw new Stop(
      "The key belongs to the connected account, not to the platform. Use the secret key of the " +
        "sandbox that the connected account was created in.",
    );
  }
  const connected = await call("GET", `/v1/accounts/${account}`, {
    platform: true,
    label: "connected-account",
  });
  if (!connected.ok) {
    throw new Stop(
      `The connected account was not found from this key (HTTP ${connected.status}).`,
    );
  }
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

export const SCENARIOS = [
  { name: "card", type: "card", amount: 51524, paymentMethod: "pm_card_visa" },
  {
    name: "card-available-now",
    type: "card",
    amount: 20000,
    paymentMethod: "pm_card_bypassPending",
  },
  {
    name: "card-declined",
    type: "card",
    amount: 20000,
    paymentMethod: "pm_card_visa_chargeDeclined",
  },
  {
    name: "ach",
    type: "us_bank_account",
    amount: 50403,
    paymentMethod: "pm_usBankAccount_success",
  },
  {
    name: "ach-insufficient-funds",
    type: "us_bank_account",
    amount: 20000,
    paymentMethod: "pm_usBankAccount_insufficientFunds",
  },
  {
    name: "ach-dispute",
    type: "us_bank_account",
    amount: 20000,
    paymentMethod: "pm_usBankAccount_dispute",
  },
];

async function charge(context, scenario) {
  const { call, run } = context;
  const operation = randomUUID();
  const params = {
    amount: scenario.amount,
    currency: "usd",
    payment_method: scenario.paymentMethod,
    // Stripe refuses `payment_method_types` at this API version: the connected account's
    // payment method settings decide what it accepts. Confirming without a return URL is
    // allowed only with redirects ruled out.
    automatic_payment_methods: { enabled: true, allow_redirects: "never" },
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
  // Stripe may ask for a test bank account to be verified first. Its documented test code does that.
  if (
    intent.status === "requires_action" &&
    intent.next_action?.type === "verify_with_microdeposits"
  ) {
    const verified = await call(
      "POST",
      `/v1/payment_intents/${intent.id}/verify_microdeposits`,
      {
        params: { descriptor_code: "SM11AA" },
        label: `${scenario.name}-verify`,
      },
    );
    run.find(
      `${scenario.name}: does ${scenario.paymentMethod} need its bank account verified first?`,
      verified.ok
        ? `Yes. The documented test code was accepted (${verified.file})`
        : `Yes, and the documented test code was refused: ${describeError(verified)} (${verified.file})`,
    );
    if (verified.ok) {
      intent = verified.body;
      seen.push(intent.status);
    }
  }
  const deadline = Date.now() + context.waitMinutes * 60_000;
  let last = null;
  while (intent.status === "processing" && Date.now() < deadline) {
    await timing.sleep(15_000);
    const polled = await call("GET", `/v1/payment_intents/${intent.id}`, {
      record: false,
    });
    if (!polled.ok) break;
    last = polled.result;
    intent = polled.body;
    if (seen.at(-1) !== intent.status) seen.push(intent.status);
  }
  // Only the last answer is kept: the ones before it differ only in when they were asked.
  const final = last
    ? run.record(`${scenario.name}-final`, last)
    : created.file;
  const seconds = Math.round((Date.now() - started) / 1000);
  run.find(
    `${scenario.name}: how does a ${scenario.paymentMethod} payment progress?`,
    `${seen.join(" -> ")} after ${seconds}s` +
      (intent.next_action ? `; next_action=${intent.next_action.type}` : "") +
      (intent.last_payment_error
        ? `; error=${intent.last_payment_error.code}`
        : "") +
      ` (${final})`,
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
    run.find(
      `${scenario.name}: what fee did Stripe take?`,
      charged.ok && transaction && typeof transaction === "object"
        ? `amount=${transaction.amount}, fee=${transaction.fee}, net=${transaction.net}, ` +
            `status=${transaction.status}, fee_details=${JSON.stringify(transaction.fee_details)} (${charged.file})`
        : `No balance transaction yet (charge status ${charged.body?.status}) (${charged.file})`,
    );
  }
  return { operation, params, intent };
}

async function idempotency(context, first) {
  const { call, run } = context;
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
    changed.ok
      ? `A payment intent, which would be a second charge: report this (${changed.file})`
      : `${describeError(changed)} (${changed.file})`,
  );
}

async function recovery(context, first, startedSeconds) {
  const { call, run } = context;
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
      ? `Yes, ${searched.body.data?.length ?? 0} result(s) this soon after creation (${searched.file})`
      : `No: ${describeError(searched)} (${searched.file})`,
  );
}

async function dispute(context, charged) {
  const { call, run } = context;
  if (charged.intent.status !== "succeeded") {
    run.find(
      "How does an ACH return after success appear?",
      `Not reached: the payment ended as ${charged.intent.status}, and a return follows only a success.`,
    );
    return;
  }
  const started = Date.now();
  const deadline = started + context.waitMinutes * 60_000;
  let found = null;
  let last = null;
  for (;;) {
    const listed = await call("GET", "/v1/disputes", {
      params: { payment_intent: charged.intent.id, limit: 10 },
      record: false,
    });
    last = listed.result;
    found = listed.body?.data?.[0] ?? null;
    if (found || Date.now() >= deadline) break;
    await timing.sleep(20_000);
  }
  const file = run.record("ach-dispute-final", last);
  run.find(
    "How does an ACH return after success appear?",
    found
      ? `A dispute after ${Math.round((Date.now() - started) / 1000)}s: reason=${found.reason}, ` +
          `status=${found.status}, amount=${found.amount} (${file})`
      : `No dispute within ${context.waitMinutes} minute(s). Run again with --only=ach-dispute and ` +
          `a longer --wait-minutes. (${file})`,
  );
}

async function payouts(context) {
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
            .join(", ") || "none yet. Run `payouts` on a later day.") +
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

// An npm-installed CLI is a `.cmd` shim on Windows, which only a shell can run.
const shell = process.platform === "win32";

function stopChild(child) {
  if (!child?.pid) return;
  if (shell)
    spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], {
      stdio: "ignore",
    });
  else child.kill();
}

async function startListener(context, port) {
  const { run } = context;
  const missed = (why) => {
    run.find("Were webhook deliveries captured?", `No: ${why}`);
    return null;
  };
  const secret = spawnSync("stripe", ["listen", "--print-secret"], {
    encoding: "utf8",
    shell,
    timeout: 30_000,
  });
  const signing =
    secret.status === 0 ? secret.stdout.trim().split(/\s+/).at(-1) : null;
  if (!signing?.startsWith("whsec_")) {
    return missed(
      "the Stripe CLI is not installed or not signed in, so `stripe listen` could not start.",
    );
  }
  const seen = [];
  let live = false;
  const server = createServer((request, response) => {
    const chunks = [];
    request.on("data", (chunk) => chunks.push(chunk));
    request.on("end", () => {
      const body = Buffer.concat(chunks).toString("utf8");
      const signature = request.headers["stripe-signature"] ?? "";
      const delivery = {
        route: request.url,
        signed: Boolean(signature),
        verified: verifySignature(
          body,
          signature,
          signing,
          Math.floor(Date.now() / 1000),
        ),
      };
      try {
        delivery.event = JSON.parse(body);
      } catch {
        delivery.event = { unparsed: true };
      }
      // Kept on disk as it arrives, so that a run cut short still has what was delivered.
      if (saysLive(delivery.event)) live = true;
      else {
        seen.push(delivery);
        run.record(`webhook${delivery.route.replace(/\W+/g, "-")}`, delivery);
      }
      response.writeHead(200).end();
    });
  });
  try {
    await new Promise((ready, failed) => {
      server.once("error", failed);
      server.listen(port, "127.0.0.1", ready);
    });
  } catch (error) {
    return missed(
      `port ${port} could not be opened (${error.code}). Choose another with --port.`,
    );
  }
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
    { stdio: "ignore", shell },
  );
  child.on("error", () => {});
  await timing.sleep(5000);
  return {
    child,
    async stop() {
      await timing.sleep(10_000);
      stopChild(child);
      await new Promise((closed) => server.close(closed));
      if (live)
        throw new Stop(
          "A live-mode event was delivered. It was not recorded. Stopping.",
        );
      const by = (route) => seen.filter((delivery) => delivery.route === route);
      const summary = (route) =>
        `${by(route).filter((d) => d.verified).length} of ${by(route).length} verified`;
      run.find(
        "Are Connect and account deliveries signed with the one secret `stripe listen` prints?",
        `connect route: ${summary("/connect")}; account route: ${summary("/account")}` +
          (seen.length
            ? ""
            : ". Nothing was delivered: check the CLI is signed in to the same sandbox as the key."),
      );
      const delivered = seen.map((delivery) => delivery.event);
      if (delivered.length) {
        run.find(
          "Do delivered events name the connected account, the mode and the API version?",
          `account present on ${delivered.filter((e) => e.account).length} of ${delivered.length}; ` +
            `livemode values: ${[...new Set(delivered.map((e) => e.livemode))].join(", ")}; ` +
            `api_version values: ${[...new Set(delivered.map((e) => e.api_version))].join(", ")}`,
        );
        run.find(
          "In what order were events delivered?",
          delivered.map((event) => event.type).join(", "),
        );
      }
    },
  };
}

function readEnvironment(environment) {
  const key = environment.STRIPE_SANDBOX_SECRET_KEY;
  const account = environment.STRIPE_SANDBOX_CONNECTED_ACCOUNT;
  assertTestKey(key);
  if (!/^acct_[A-Za-z0-9]+$/.test(account ?? "")) {
    throw new Stop(
      "STRIPE_SANDBOX_CONNECTED_ACCOUNT must be a connected account id (acct_...).",
    );
  }
  return { key, account };
}

async function runProbe(root, options, environment) {
  const { key, account } = readEnvironment(environment);
  const id = new Date().toISOString().replace(/[:.]/g, "-");
  const run = openRun(join(root, RAW_ROOT, id));
  const context = {
    run,
    account,
    call: createClient({ key, account, run }),
    waitMinutes: options.waitMinutes,
    generation: randomUUID(),
  };
  const startedSeconds = Math.floor(Date.now() / 1000);
  let listener = null;
  // Interrupted, the run still leaves its findings and no forwarder behind.
  const interrupted = () => {
    stopChild(listener?.child);
    run.write();
    process.exit(130);
  };
  process.once("SIGINT", interrupted);
  console.log(`Recording to ${run.directory}\n`);
  try {
    await preflight(context);
    listener = options.listen
      ? await startListener(context, options.port)
      : null;
    const done = {};
    for (const scenario of SCENARIOS.filter((s) =>
      options.only.includes(s.name),
    )) {
      done[scenario.name] = await step(run, scenario.name, () =>
        charge(context, scenario),
      );
    }
    if (done.card?.intent) {
      await step(run, "idempotency", () => idempotency(context, done.card));
      await step(run, "recovery", () =>
        recovery(context, done.card, startedSeconds),
      );
    }
    if (done["ach-dispute"]?.intent) {
      await step(run, "ach-dispute", () =>
        dispute(context, done["ach-dispute"]),
      );
    }
    await step(run, "payouts", () => payouts(context));
    await step(run, "events", () => events(context, startedSeconds));
    if (listener) await listener.stop();
  } finally {
    process.removeListener("SIGINT", interrupted);
    stopChild(listener?.child);
    console.log(`\nFindings: ${run.write()}`);
  }
  return 0;
}

async function runPayouts(directory, environment) {
  const { key, account } = readEnvironment(environment);
  const run = openRun(directory);
  try {
    await payouts({ run, account, call: createClient({ key, account, run }) });
  } finally {
    run.write();
  }
  return 0;
}

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  const names = SCENARIOS.map((scenario) => scenario.name);
  const options = {
    command,
    only: names,
    waitMinutes: 10,
    listen: true,
    port: 4242,
    directory: null,
  };
  let valid = ["run", "payouts", "scrub"].includes(command);
  for (const argument of rest) {
    if (argument.startsWith("--only="))
      options.only = argument.slice(7).split(",");
    else if (argument.startsWith("--wait-minutes="))
      options.waitMinutes = Number(argument.slice(15));
    else if (argument === "--no-listen") options.listen = false;
    else if (argument.startsWith("--port="))
      options.port = Number(argument.slice(7));
    else if (!argument.startsWith("--") && !options.directory)
      options.directory = argument;
    else valid = false;
  }
  const needsDirectory = command === "payouts" || command === "scrub";
  if (
    !valid ||
    needsDirectory !== Boolean(options.directory) ||
    options.only.some((name) => !names.includes(name)) ||
    !(options.waitMinutes >= 0 && options.waitMinutes <= 120) ||
    !(
      Number.isInteger(options.port) &&
      options.port >= 1 &&
      options.port <= 65535
    )
  ) {
    throw new Stop(
      "Usage: stripe-probe.mjs run [--only=<charge>,...] [--wait-minutes=10] [--no-listen] [--port=4242]\n" +
        "       stripe-probe.mjs payouts <run-directory>\n" +
        "       stripe-probe.mjs scrub <run-directory>\n" +
        `Charges for --only: ${names.join(", ")}`,
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
    throw new Stop(`No such run directory: ${directory}`);
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
