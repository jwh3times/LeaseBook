#!/usr/bin/env node

/**
 * A local stand-in for Stripe, for browser tests that must make no call to Stripe (ADR-054, #545).
 *
 * A host in `StripeSandbox` mode is pointed at it with `Payments:Stripe:ApiBase`. It answers the
 * few requests the adapter makes, from the payloads recorded under `tests/fixtures/stripe/`, and
 * sends signed deliveries to the host's callback route as Stripe does. It holds what it is told in
 * memory and forgets it when it stops.
 *
 * Usage (from the repository root), in a terminal that holds the host's own payment variables:
 *
 *   node scripts/stripe-stand-in.mjs [--callback=http://127.0.0.1:5080/callbacks/payments/stripe]
 *
 * It reads three of the variables the host reads, so the two cannot disagree:
 *
 *   Payments__Stripe__ApiBase        where it listens: http, a loopback host and a port
 *   Payments__Stripe__WebhookSecret  the secret it signs deliveries with (whsec_...)
 *   Payments__ManifestPath           the fixture manifest, for the connected accounts it holds
 *
 * It listens on loopback only and sends deliveries to loopback only. Every request but the health
 * check must carry the stand-in's own key, which Stripe issues to nobody. A request it does not
 * know is answered with an error, logged, and kept on a list a test reads, so that an unexpected
 * request fails a test and is not silently satisfied.
 *
 * Two surfaces:
 *
 *   /v1/...        what the adapter asks Stripe
 *   /stand-in/...  what a test tells the stand-in to do: move a payment along a recorded timeline,
 *                  form a payout from named payments, and read what has happened
 *
 * docs/runbooks/payment-simulation.md owns the procedure, and says which answers are recorded
 * shapes with values substituted and which are built.
 */

import { createHmac, randomBytes } from "node:crypto";
import { readFileSync } from "node:fs";
import { createServer } from "node:http";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import { API_VERSION, FIXTURE_ROOT } from "./stripe-probe.mjs";

/** The one key a host pointed at a stand-in holds (`StripeSandboxSettings.StandInSecretKey`). */
export const STAND_IN_KEY = "sk_test_leasebook_stand_in";
export const DEFAULT_ADDRESS = "http://127.0.0.1:4010";
export const DEFAULT_CALLBACK =
  "http://127.0.0.1:5080/callbacks/payments/stripe";

const CARD_RUN = "2026-10-08T21-56-17-265Z";
const PAYOUT_RUN = "2026-10-09T18-44-03-363Z";
const LOOPBACK = ["127.0.0.1", "localhost", "[::1]"];
const MAX_BODY = 64 * 1024;

/** A reason to stop before listening. */
class Stop extends Error {}

/** A request this stand-in does not know. Answered as an error and kept for a test to read. */
class Unknown extends Error {
  constructor(message, status = 501) {
    super(message);
    this.status = status;
  }
}

// --- Pure helpers, exported for the tests -------------------------------------------------

/**
 * An address on this machine and nothing else, as the host admits one: http, then `127.0.0.1`,
 * `localhost` or `[::1]`, then a port. With `path` true a path may follow, for the callback route.
 */
export function loopbackAddress(text, { path = false } = {}) {
  let url;
  try {
    url = new URL(String(text));
  } catch {
    return null;
  }
  if (url.protocol !== "http:" || !LOOPBACK.includes(url.hostname)) return null;
  if (!url.port || url.username || url.password || url.search || url.hash)
    return null;
  if (!path && url.pathname !== "/") return null;
  const address = `http://${url.host}`;
  const given = String(text);
  if (path) return given === address + url.pathname ? url : null;
  return given === address || given === `${address}/` ? url : null;
}

/** Stripe's signature header for a body: `t=<unix seconds>,v1=<hex HMAC-SHA256 of "<t>.<body>">`. */
export function signatureHeader(secret, body, seconds) {
  const digest = createHmac("sha256", secret)
    .update(`${seconds}.${body}`)
    .digest("hex");
  return `t=${seconds},v1=${digest}`;
}

/**
 * The fee Stripe kept in the recorded sandbox runs (ADR-054): 2.9% plus 30 cents of a card charge,
 * 0.8% of an ACH debit, to the nearest cent. Worked out here, in cents, and not recorded: no
 * recording holds a charge of the amount a test pays.
 */
export function stripeFee(method, amount) {
  if (method === "card") return Math.round((amount * 29) / 1000) + 30;
  if (method === "ach") return Math.round((amount * 8) / 1000);
  throw new Error(`No recorded fee for the method ${method}.`);
}

/** A per-run identifier in Stripe's shape: a prefix, an underscore, then letters and digits. */
export function newId(prefix) {
  return `${prefix}_standin${randomBytes(9).toString("hex")}`;
}

/**
 * A copy of a recorded object with values substituted. An identifier is replaced wherever it is a
 * whole value. A number is replaced only under a name that holds an amount, and a time only under a
 * name that holds a time, so that a count or a year that happens to be equal is left alone.
 */
export function substitute(recorded, { ids = {}, amounts = {}, time } = {}) {
  const walk = (value, key) => {
    if (Array.isArray(value)) return value.map((item) => walk(item, key));
    if (value && typeof value === "object") {
      return Object.fromEntries(
        Object.entries(value).map(([name, inner]) => [name, walk(inner, name)]),
      );
    }
    if (typeof value === "string") return ids[value] ?? value;
    if (typeof value === "number") {
      if (/amount|^net$|^fee$/.test(key ?? "") && value in amounts)
        return amounts[value];
      if (time && /^(created|available_on)$/.test(key ?? ""))
        return time(value);
    }
    return value;
  };
  return walk(structuredClone(recorded), null);
}

// --- Recorded payloads ---------------------------------------------------------------------

function loadRecorded(root) {
  const read = (run, file) =>
    JSON.parse(readFileSync(join(root, FIXTURE_ROOT, run, file), "utf8"));
  const body = (run, file) => read(run, file).body;
  const delivered = (file) => read(CARD_RUN, file).event;
  const lines = body(PAYOUT_RUN, "023-payout-lines.json");
  const line = (id) => lines.data.find((item) => item.id === id);
  const payoutEvents = body(PAYOUT_RUN, "029-payout-events.json");
  const payoutEvent = (type) =>
    payoutEvents.data.find((item) => item.type === type);
  return {
    platformAccount: body(CARD_RUN, "001-platform-account.json"),
    connectedAccount: body(CARD_RUN, "002-connected-account.json"),
    card: {
      // What Stripe answered the charge with, and the four events it then sent, in the order sent.
      intent: body(CARD_RUN, "003-card-create.json"),
      events: [
        delivered("004-webhook-connect.json"),
        delivered("005-webhook-connect.json"),
        delivered("006-webhook-connect.json"),
        delivered("007-webhook-connect.json"),
      ],
      // The balance line of a card charge in the one recorded payout.
      line: line("txn_synthetic000012"),
    },
    idempotencyChanged: read(CARD_RUN, "054-idempotent-changed.json"),
    list: {
      object: lines.object,
      url: lines.url,
    },
    payout: {
      // The four events about the one recorded payout, in the order Stripe made them.
      events: [
        payoutEvent("payout.created"),
        payoutEvent("payout.reconciliation_completed"),
        payoutEvent("payout.paid"),
        payoutEvent("payout.updated"),
      ],
      ownLine: line("txn_synthetic000005"),
    },
  };
}

/** The identifiers and values a recorded card payment is known by, to be replaced. */
const RECORDED_CARD = {
  intent: "pi_synthetic000001",
  charge: "ch_synthetic000001",
  line: "txn_synthetic000001",
  account: "acct_synthetic000002",
  amount: 51524,
  created: 1791496583,
};
// The recorded payout's line for a card charge. The charge inside it is replaced whole.
const RECORDED_CARD_LINE = {
  line: "txn_synthetic000012",
  amount: 20000,
  fee: 610,
  net: 19390,
};
const RECORDED_PAYOUT = {
  payout: "po_synthetic000001",
  line: "txn_synthetic000005",
  account: "acct_synthetic000002",
  amount: 147060,
  paid: 1791506838,
};

/**
 * What a test payment method does. A method not listed is refused as unknown. Later timelines are
 * added here and under `TIMELINES`; nothing else names a method.
 */
const PAYMENT_METHODS = {
  pm_card_visa: { method: "card", status: "succeeded" },
};

// --- The stand-in ----------------------------------------------------------------------------

/**
 * A stand-in over the recorded payloads. `accounts` are the connected accounts it holds, `deliver`
 * sends one signed body to the host and resolves to the HTTP status, and `now` is the clock, in
 * seconds. It returns the request handler, and what a test reads.
 */
export function createStandIn({
  accounts,
  webhookSecret,
  deliver,
  root = resolve(dirname(fileURLToPath(import.meta.url)), ".."),
  now = () => Math.floor(Date.now() / 1000),
  log = () => {},
}) {
  const recorded = loadRecorded(root);
  const held = new Map(
    accounts.map((account) => [
      account,
      {
        payments: new Map(),
        payouts: new Map(),
        events: [],
        idempotency: new Map(),
      },
    ]),
  );
  const unexpected = [];
  const deliveries = [];
  let requests = 0;

  const list = (data, hasMore) => ({
    object: recorded.list.object,
    data,
    has_more: hasMore,
    url: recorded.list.url,
  });
  const missing = (kind, id) => ({
    status: 404,
    body: {
      error: {
        code: "resource_missing",
        message: `No such ${kind}: '${id}'`,
        param: "id",
        type: "invalid_request_error",
      },
    },
  });

  // One page of a list as Stripe pages one: after the item named, at most `limit` of them.
  function page(items, query) {
    const limit = Number(query.get("limit") ?? 10);
    if (!Number.isInteger(limit) || limit < 1 || limit > 100)
      throw new Unknown("A list limit is a whole number from 1 to 100.", 400);
    const after = query.get("starting_after");
    let start = 0;
    if (after !== null) {
      const at = items.findIndex((item) => item.id === after);
      if (at < 0)
        throw new Unknown(`No item ${after} to start a page after.`, 400);
      start = at + 1;
    }
    return list(
      items.slice(start, start + limit),
      items.length > start + limit,
    );
  }

  // --- /v1: what the adapter asks ------------------------------------------------------------

  function createPayment(account, book, form, idempotencyKey) {
    const known = new Set([
      "amount",
      "currency",
      "payment_method",
      "confirm",
      "automatic_payment_methods[enabled]",
      "automatic_payment_methods[allow_redirects]",
      "metadata[leasebook_operation]",
      "metadata[leasebook_generation]",
    ]);
    const stranger = [...form.keys()].find((key) => !known.has(key));
    if (stranger)
      throw new Unknown(`A charge carried the unknown field ${stranger}.`);
    if (!idempotencyKey)
      throw new Unknown("A charge carried no idempotency key.");
    const fingerprint = JSON.stringify(
      [...form.entries()].sort(([a], [b]) => a.localeCompare(b)),
    );
    const first = book.idempotency.get(idempotencyKey);
    if (first) {
      // The same key with the same parameters answers as it first did. With others it is refused,
      // in the words Stripe was recorded refusing one.
      if (first.fingerprint === fingerprint)
        return {
          status: 200,
          body: book.payments.get(first.id).intent,
          headers: { "Idempotent-Replayed": "true" },
        };
      const refusal = structuredClone(recorded.idempotencyChanged);
      refusal.body.error.message = refusal.body.error.message.replace(
        refusal.request.idempotencyKey,
        idempotencyKey,
      );
      return { status: refusal.status, body: refusal.body };
    }
    const amount = Number(form.get("amount"));
    const how = PAYMENT_METHODS[form.get("payment_method")];
    const operation = form.get("metadata[leasebook_operation]");
    const generation = form.get("metadata[leasebook_generation]");
    if (!how)
      throw new Unknown(
        `No recorded timeline for the payment method ${form.get("payment_method")}.`,
      );
    if (
      !Number.isInteger(amount) ||
      amount <= 0 ||
      form.get("currency") !== "usd" ||
      form.get("confirm") !== "true" ||
      form.get("automatic_payment_methods[enabled]") !== "true" ||
      form.get("automatic_payment_methods[allow_redirects]") !== "never" ||
      !operation ||
      !generation
    )
      throw new Unknown("A charge was not made as the adapter makes one.");
    const created = now();
    const payment = {
      id: newId("pi"),
      charge: newId("ch"),
      line: newId("txn"),
      account,
      operation,
      generation,
      method: how.method,
      amount,
      fee: stripeFee(how.method, amount),
      created,
      status: how.status,
      idempotencyKey,
      // Whether Stripe has said it was paid, which is what a payout may then hold.
      paid: false,
      payout: null,
      timelines: [],
    };
    payment.intent = asPayment(recorded.card.intent, payment);
    // The charge as its last recorded event left it, with its balance line attached.
    payment.chargeObject = asPayment(
      recorded.card.events.at(-1).data.object,
      payment,
    );
    book.payments.set(payment.id, payment);
    book.idempotency.set(idempotencyKey, { fingerprint, id: payment.id });
    return { status: 200, body: payment.intent };
  }

  // A recorded object of the card payment, made this payment's: its ids, amount, times and the two
  // ids of LeaseBook's it carries. A time is moved by as much as the payment's own was, and never
  // past this moment.
  function asPayment(object, payment) {
    const copy = substitute(object, {
      ids: {
        [RECORDED_CARD.intent]: payment.id,
        [RECORDED_CARD.charge]: payment.charge,
        [RECORDED_CARD.line]: payment.line,
        [RECORDED_CARD.account]: payment.account,
      },
      amounts: { [RECORDED_CARD.amount]: payment.amount },
      time: (at) =>
        Math.min(now(), at + payment.created - RECORDED_CARD.created),
    });
    const stamp = (value) => {
      if (Array.isArray(value)) return value.forEach(stamp);
      if (!value || typeof value !== "object") return;
      if (
        value.metadata &&
        typeof value.metadata === "object" &&
        "leasebook_operation" in value.metadata
      )
        value.metadata = {
          leasebook_generation: payment.generation,
          leasebook_operation: payment.operation,
        };
      Object.values(value).forEach(stamp);
    };
    stamp(copy);
    return copy;
  }

  function answer(method, path, query, headers, form) {
    if (headers["stripe-version"] !== API_VERSION)
      throw new Unknown(
        `The payloads here were recorded at API version ${API_VERSION}, and the request named another.`,
        400,
      );
    const account = headers["stripe-account"];
    if (method === "GET" && path === "/v1/account" && !account)
      return { status: 200, body: recorded.platformAccount };
    const named = /^\/v1\/accounts\/([A-Za-z0-9_]+)$/.exec(path);
    if (method === "GET" && named && !account) {
      if (!held.has(named[1])) return missing("account", named[1]);
      return {
        status: 200,
        body: { ...recorded.connectedAccount, id: named[1] },
      };
    }
    const book = held.get(account);
    if (!book)
      throw new Unknown(
        `${method} ${path} named a connected account this stand-in does not hold.`,
        403,
      );

    if (path === "/v1/payment_intents") {
      if (method === "POST")
        return createPayment(account, book, form, headers["idempotency-key"]);
      if (method === "GET") {
        // Newest first, as Stripe lists. The adapter asks by creation time and nothing else.
        const since = Number(query.get("created[gte]") ?? 0);
        const found = [...book.payments.values()]
          .filter((payment) => payment.created >= since)
          .reverse()
          .map((payment) => payment.intent);
        return { status: 200, body: page(found, query) };
      }
    }
    const intent = /^\/v1\/payment_intents\/([A-Za-z0-9_]+)$/.exec(path);
    if (method === "GET" && intent) {
      const payment = book.payments.get(intent[1]);
      return payment
        ? { status: 200, body: payment.intent }
        : missing("payment_intent", intent[1]);
    }
    const charge = /^\/v1\/charges\/([A-Za-z0-9_]+)$/.exec(path);
    if (method === "GET" && charge) {
      const payment = [...book.payments.values()].find(
        (item) => item.charge === charge[1],
      );
      return payment
        ? { status: 200, body: payment.chargeObject }
        : missing("charge", charge[1]);
    }
    if (method === "GET" && path === "/v1/events") {
      const since = Number(query.get("created[gte]") ?? 0);
      const types = [...query.entries()]
        .filter(([key]) => /^types\[\d*\]$/.test(key))
        .map(([, value]) => value);
      const found = book.events
        .filter(
          (event) =>
            event.created >= since &&
            (types.length === 0 || types.includes(event.type)),
        )
        .reverse();
      return { status: 200, body: page(found, query) };
    }
    const payout = /^\/v1\/payouts\/([A-Za-z0-9_]+)$/.exec(path);
    if (method === "GET" && payout) {
      const made = book.payouts.get(payout[1]);
      return made
        ? { status: 200, body: made.object }
        : missing("payout", payout[1]);
    }
    if (method === "GET" && path === "/v1/balance_transactions") {
      const made = book.payouts.get(query.get("payout"));
      if (!made)
        throw new Unknown(
          "Balance lines were asked for without a payout this stand-in made.",
        );
      // Expanded, a line carries the object it came from; otherwise that object's id.
      const expanded = query.getAll("expand[]").includes("data.source");
      const lines = made.lines.map((line) =>
        expanded ? line : { ...line, source: line.source.id },
      );
      return { status: 200, body: page(lines, query) };
    }
    throw new Unknown(
      `${method} ${path} is not a request this stand-in knows.`,
    );
  }

  // --- Deliveries ----------------------------------------------------------------------------

  // An event exists at Stripe whether or not it is delivered: it is listed from here on either way.
  async function happen(book, event, send) {
    book.events.push(event);
    if (!send) return { id: event.id, type: event.type, status: null };
    const body = JSON.stringify(event, null, 2);
    let status;
    try {
      status = await deliver(body, signatureHeader(webhookSecret, body, now()));
    } catch (error) {
      status = 0;
      log(`delivery of ${event.type} failed: ${error.message}`);
    }
    const sent = { id: event.id, type: event.type, status };
    deliveries.push(sent);
    log(`delivered ${event.type} ${status}`);
    return sent;
  }

  /**
   * The recorded timelines a payment can be moved along, by name. Each sends, in the order Stripe
   * sent them, the events recorded for a payment of that kind. A later timeline is one more entry.
   */
  const TIMELINES = {
    // A card payment that succeeds: `charge.succeeded`, `payment_intent.succeeded`,
    // `payment_intent.created`, and `charge.updated` once the balance line exists.
    "card-succeeds": {
      method: "card",
      paid: true,
      events: (payment) =>
        recorded.card.events.map((event) => {
          const copy = asPayment(event, payment);
          copy.id = newId("evt");
          if (copy.request?.idempotency_key)
            copy.request.idempotency_key = payment.idempotencyKey;
          return copy;
        }),
    },
  };

  async function runTimeline(operation, name, send) {
    const timeline = TIMELINES[name];
    if (!timeline)
      throw new Unknown(`No recorded timeline named ${name}.`, 400);
    const payment = paymentFor(operation);
    if (payment.method !== timeline.method || payment.timelines.includes(name))
      throw new Unknown(
        `The timeline ${name} does not apply to payment ${operation}, or has already run.`,
        409,
      );
    payment.timelines.push(name);
    const book = held.get(payment.account);
    const sent = [];
    for (const event of timeline.events(payment))
      sent.push(await happen(book, event, send));
    if (timeline.paid) payment.paid = true;
    return { payment: payment.id, deliveries: sent };
  }

  function paymentFor(operation) {
    for (const book of held.values())
      for (const payment of book.payments.values())
        if (payment.operation === operation) return payment;
    throw new Unknown(`No payment was made for operation ${operation}.`, 404);
  }

  /**
   * Forms one automatic payout from the payments named and sends its events. Stripe decides what a
   * payout covers; here the test does, standing in for Stripe. The payout's own line and its events
   * are the recorded ones, and each payment's line is the recorded line of a charge of its kind,
   * all with this payout's ids, amounts and times. No recorded payout holds only good payments, so
   * the whole is built from those shapes.
   */
  async function formPayout(operations, send) {
    if (!Array.isArray(operations) || operations.length === 0)
      throw new Unknown("A payout is formed from at least one payment.", 400);
    const payments = operations.map(paymentFor);
    const account = payments[0].account;
    if (
      new Set(operations).size !== operations.length ||
      payments.some(
        (payment) =>
          payment.account !== account ||
          payment.payout !== null ||
          !payment.paid,
      )
    )
      throw new Unknown(
        "A payout holds payments of one account that Stripe has said were paid and that are in no other payout.",
        409,
      );
    const book = held.get(account);
    const id = newId("po");
    const ownLine = newId("txn");
    const amount = payments.reduce(
      (sum, payment) => sum + payment.amount - payment.fee,
      0,
    );
    const paid = now();
    const midnight = paid - (paid % 86400);
    const asPayout = (object) => {
      const copy = substitute(object, {
        ids: {
          [RECORDED_PAYOUT.payout]: id,
          [RECORDED_PAYOUT.line]: ownLine,
          [RECORDED_PAYOUT.account]: account,
        },
        amounts: {
          [RECORDED_PAYOUT.amount]: amount,
          [-RECORDED_PAYOUT.amount]: -amount,
        },
        time: (at) => Math.min(paid, at + paid - RECORDED_PAYOUT.paid),
      });
      const day = (value) => {
        if (!value || typeof value !== "object") return;
        if ("arrival_date" in value) value.arrival_date = midnight;
        Object.values(value).forEach(day);
      };
      day(copy);
      return copy;
    };
    const events = recorded.payout.events.map((event) => {
      const copy = asPayout(event);
      copy.id = newId("evt");
      return copy;
    });
    const lines = [
      asPayout(recorded.payout.ownLine),
      ...payments.map((payment) => {
        const line = substitute(recorded.card.line, {
          ids: { [RECORDED_CARD_LINE.line]: payment.line },
          amounts: {
            [RECORDED_CARD_LINE.amount]: payment.amount,
            [RECORDED_CARD_LINE.fee]: payment.fee,
            [RECORDED_CARD_LINE.net]: payment.amount - payment.fee,
          },
          time: () => payment.created,
        });
        // The charge the line came from is this payment's own, as the stand-in holds it.
        line.source = payment.chargeObject;
        return line;
      }),
    ];
    const made = {
      id,
      account,
      amount,
      status: "paid",
      // The payout as its last event left it.
      object: events.at(-1).data.object,
      lines,
      payments: payments.map((payment) => payment.operation),
    };
    book.payouts.set(id, made);
    for (const payment of payments) payment.payout = id;
    const sent = [];
    for (const event of events) sent.push(await happen(book, event, send));
    return {
      id,
      account,
      amount,
      net: (amount / 100).toFixed(2),
      deliveries: sent,
    };
  }

  // --- /stand-in: what a test tells it ---------------------------------------------------------

  function state() {
    return {
      accounts: [...held.keys()],
      payments: [...held.values()].flatMap((book) =>
        [...book.payments.values()].map((payment) => ({
          operation: payment.operation,
          id: payment.id,
          account: payment.account,
          method: payment.method,
          amount: payment.amount,
          fee: payment.fee,
          status: payment.status,
          paid: payment.paid,
          timelines: payment.timelines,
          payout: payment.payout,
        })),
      ),
      payouts: [...held.values()].flatMap((book) =>
        [...book.payouts.values()].map((payout) => ({
          id: payout.id,
          account: payout.account,
          amount: payout.amount,
          status: payout.status,
          payments: payout.payments,
        })),
      ),
      deliveries,
      requests,
      unexpected,
    };
  }

  async function control(method, path, body) {
    if (method === "GET" && path === "/stand-in/state")
      return { status: 200, body: state() };
    const timeline = /^\/stand-in\/payments\/([0-9a-f-]{36})\/timeline$/.exec(
      path,
    );
    if (method === "POST" && timeline)
      return {
        status: 200,
        body: await runTimeline(
          timeline[1],
          body.timeline,
          body.deliver !== false,
        ),
      };
    if (method === "POST" && path === "/stand-in/payouts")
      return {
        status: 200,
        body: await formPayout(body.payments, body.deliver !== false),
      };
    throw new Unknown(
      `${method} ${path} is not a request this stand-in knows.`,
    );
  }

  /** Answers one request: `{ status, body, headers }`. Never throws for a request it refuses. */
  async function handle({ method, url, headers, body }) {
    const { pathname, searchParams } = new URL(url, "http://stand-in");
    if (method === "GET" && pathname === "/stand-in/health")
      return { status: 200, body: { ok: true } };
    requests++;
    try {
      if (headers.authorization !== `Bearer ${STAND_IN_KEY}`)
        throw new Unknown(
          `${method} ${pathname} did not carry the stand-in's key.`,
          401,
        );
      if (pathname.startsWith("/stand-in/")) {
        let given = {};
        if (method === "POST") {
          if (!/^application\/json\b/.test(headers["content-type"] ?? ""))
            throw new Unknown("A control request is JSON.", 415);
          given = JSON.parse(body || "{}");
        }
        return await control(method, pathname, given);
      }
      const answered = answer(
        method,
        pathname,
        searchParams,
        headers,
        new URLSearchParams(method === "POST" ? body : ""),
      );
      // The path and the status, and never a header: the log is uploaded with a test report.
      log(`${method} ${pathname} ${answered.status}`);
      return answered;
    } catch (error) {
      if (!(error instanceof Unknown) && !(error instanceof SyntaxError))
        throw error;
      const status = error instanceof Unknown ? error.status : 400;
      unexpected.push({ method, path: pathname, status, why: error.message });
      log(`UNEXPECTED ${method} ${pathname} ${status}: ${error.message}`);
      return {
        status,
        body: {
          error: {
            type: "invalid_request_error",
            code: "stand_in_unknown_request",
            message: error.message,
          },
        },
      };
    }
  }

  return { handle, state };
}

// --- The process -----------------------------------------------------------------------------

/** The connected accounts a sandbox fixture manifest binds. Nothing else in it is read. */
export function manifestAccounts(path) {
  const manifest = JSON.parse(readFileSync(path, "utf8")).Payments;
  const accounts = (manifest?.Fixtures ?? []).map((fixture) => fixture.Account);
  if (
    manifest?.Mode !== "StripeSandbox" ||
    accounts.length === 0 ||
    accounts.some((account) => !/^acct_[A-Za-z0-9]+$/.test(account ?? ""))
  )
    throw new Stop(
      "Payments__ManifestPath must name a StripeSandbox fixture manifest.",
    );
  return accounts;
}

export function parseArgs(argv) {
  const options = { callback: DEFAULT_CALLBACK };
  for (const argument of argv) {
    const [name, value] = argument.split("=", 2);
    if (name === "--callback" && value) options.callback = value;
    else
      throw new Stop(
        `Unknown argument: ${argument}. Usage: stripe-stand-in.mjs [--callback=<url>]`,
      );
  }
  return options;
}

/** Reads a request body, up to what a delivery or a charge can be. */
function readBody(request) {
  return new Promise((done, failed) => {
    const chunks = [];
    let size = 0;
    request.on("data", (chunk) => {
      size += chunk.length;
      if (size > MAX_BODY) {
        failed(new Error("too large"));
        request.destroy();
      } else chunks.push(chunk);
    });
    request.on("end", () => done(Buffer.concat(chunks).toString("utf8")));
    request.on("error", failed);
  });
}

/** Serves a stand-in over HTTP. The addresses are loopback, or it does not listen. */
export function serve(standIn, { hostname, port }) {
  const server = createServer(async (request, response) => {
    let answer;
    try {
      answer = await standIn.handle({
        method: request.method,
        url: request.url,
        headers: request.headers,
        body: await readBody(request),
      });
    } catch (error) {
      console.error(`stripe-stand-in: ${error.stack ?? error.message}`);
      answer = {
        status: 500,
        body: { error: { type: "api_error", message: "The stand-in failed." } },
      };
    }
    response.writeHead(answer.status, {
      "Content-Type": "application/json",
      "Stripe-Version": API_VERSION,
      "Request-Id": newId("req"),
      ...answer.headers,
    });
    response.end(JSON.stringify(answer.body));
  });
  return new Promise((done, failed) => {
    server.once("error", failed);
    server.listen(port, hostname, () => done(server));
  });
}

export async function main(argv, { environment = process.env } = {}) {
  const options = parseArgs(argv);
  const address = loopbackAddress(
    environment.Payments__Stripe__ApiBase ?? DEFAULT_ADDRESS,
  );
  if (!address)
    throw new Stop(
      "Payments__Stripe__ApiBase must be http, then 127.0.0.1, localhost or [::1], then a port.",
    );
  const callback = loopbackAddress(options.callback, { path: true });
  if (!callback)
    throw new Stop(
      "--callback must be an http address on this machine, with a port.",
    );
  const webhookSecret = environment.Payments__Stripe__WebhookSecret ?? "";
  if (!/^whsec_[\x21-\x7E]+$/.test(webhookSecret))
    throw new Stop(
      "Payments__Stripe__WebhookSecret must hold the signing secret the host was given (whsec_...).",
    );
  if (!environment.Payments__ManifestPath)
    throw new Stop(
      "Payments__ManifestPath must name the stand-in fixture's manifest.",
    );
  const accounts = manifestAccounts(environment.Payments__ManifestPath);
  const standIn = createStandIn({
    accounts,
    webhookSecret,
    log: (line) => console.log(`stripe-stand-in: ${line}`),
    deliver: async (body, signature) => {
      const response = await fetch(callback, {
        method: "POST",
        body,
        headers: {
          "Content-Type": "application/json; charset=utf-8",
          "Stripe-Signature": signature,
        },
        redirect: "error",
        signal: AbortSignal.timeout(60_000),
      });
      return response.status;
    },
  });
  // `localhost` is both loopback addresses, and a client may try either first.
  const port = Number(address.port);
  const named = address.hostname.replace(/^\[|\]$/g, "");
  const servers = [
    await serve(standIn, {
      hostname: named === "localhost" ? "127.0.0.1" : named,
      port,
    }),
  ];
  if (named === "localhost") {
    // A machine without IPv6 still has the other loopback address.
    try {
      servers.push(await serve(standIn, { hostname: "::1", port }));
    } catch (error) {
      if (error.code !== "EADDRNOTAVAIL") throw error;
    }
  }
  console.log(
    `stripe-stand-in: listening on ${address.origin} for ${accounts.length} connected account(s); deliveries go to ${callback.origin}${callback.pathname}`,
  );
  return new Promise((done) => {
    const stop = () => {
      for (const server of servers) server.close();
      done(0);
    };
    process.once("SIGINT", stop);
    process.once("SIGTERM", stop);
  });
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  main(process.argv.slice(2)).then(
    (code) => process.exit(code),
    (error) => {
      console.error(`stripe-stand-in: ${error.message}`);
      process.exit(1);
    },
  );
}
