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
 *   /stand-in/...  what a test tells the stand-in to do: move a payment along a recorded timeline
 *                  (paid, disputed, refunded), form a payout from named payments, serve the one
 *                  recorded payout, fail a payout, and read what has happened
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

// The three recorded runs. Each numbers its synthetic identifiers from one, so an identifier means
// something only beside the run it came from.
const CARD_RUN = "2026-10-08T21-56-17-265Z";
const PAYOUT_RUN = "2026-10-09T18-44-03-363Z";
const DISPUTE_RUN = "2026-10-09T18-45-07-940Z";
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
  const delivered = (file, run = CARD_RUN) => read(run, file).event;
  const lines = body(PAYOUT_RUN, "023-payout-lines.json");
  const line = (id) => lines.data.find((item) => item.id === id);
  const payoutEvents = body(PAYOUT_RUN, "029-payout-events.json");
  const payoutEvent = (type) =>
    payoutEvents.data.find((item) => item.type === type);
  // An event Stripe listed and the probe was not sent: the refund events beyond the first two.
  const listed = (run, file, id) =>
    body(run, file).data.find((item) => item.id === id);
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
      // A card payment Stripe disputed: its two dispute events, and its charge as Stripe then held it.
      dispute: {
        events: [
          delivered("007-webhook-connect.json", DISPUTE_RUN),
          delivered("008-webhook-connect.json", DISPUTE_RUN),
        ],
        charge: body(DISPUTE_RUN, "009-card-dispute-charge.json"),
      },
      // A refund in full and a refund of part. The first two events of each are in the refund run,
      // where only `charge.refunded` of the partial one was delivered; the last two were listed in
      // the dispute run, under that run's own identifiers.
      refund: {
        full: [
          delivered("016-webhook-connect.json", PAYOUT_RUN),
          delivered("017-webhook-connect.json", PAYOUT_RUN),
        ],
        fullLater: [
          listed(DISPUTE_RUN, "014-events-api.json", "evt_synthetic000008"),
          listed(DISPUTE_RUN, "014-events-api.json", "evt_synthetic000007"),
        ],
        part: [
          listed(PAYOUT_RUN, "024-events-api.json", "evt_synthetic000010"),
          delivered("021-webhook-connect.json", PAYOUT_RUN),
        ],
        partLater: [
          listed(DISPUTE_RUN, "014-events-api.json", "evt_synthetic000010"),
          listed(DISPUTE_RUN, "014-events-api.json", "evt_synthetic000009"),
        ],
      },
    },
    ach: {
      // What Stripe answered an ACH debit with, the eight events it then sent, in the order sent,
      // and the payment and its charge as Stripe held them once the debit had succeeded.
      intent: body(CARD_RUN, "019-ach-create.json"),
      events: [
        delivered("020-webhook-connect.json"),
        delivered("021-webhook-connect.json"),
        delivered("022-webhook-connect.json"),
        delivered("023-webhook-connect.json"),
        delivered("024-webhook-connect.json"),
        delivered("025-webhook-connect.json"),
        delivered("026-webhook-connect.json"),
        delivered("027-webhook-connect.json"),
      ],
      final: body(CARD_RUN, "028-ach-final.json"),
      charge: body(CARD_RUN, "029-ach-charge.json"),
      // The balance line of that same debit in the one recorded payout: $504.03, of which $4.03 kept.
      line: line("txn_synthetic000011"),
      // An ACH debit Stripe disputed after it succeeded: the three dispute events, the charge Stripe
      // held for it, and the dispute's balance line in the one recorded payout.
      dispute: {
        events: [
          delivered("058-webhook-connect.json"),
          delivered("059-webhook-connect.json"),
          delivered("060-webhook-connect.json"),
        ],
        charge: body(CARD_RUN, "052-ach-dispute-charge.json"),
        line: line("txn_synthetic000007"),
      },
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
      // Every balance line Stripe listed for it, the payout's own first.
      lines: lines.data,
    },
  };
}

/**
 * The identifiers and values a recorded payment is known by in one group of recorded objects, to
 * be replaced with a payment's own. `created` is when the recorded payment was made. `at`, where
 * there is one, is when the first event of a later timeline was made: a time from then on is moved
 * to this moment, and an earlier one by as much as the payment's own creation was.
 */
const RECORDED_ACCOUNT = "acct_synthetic000002";
const RECORDED_CARD = {
  intent: "pi_synthetic000001",
  charge: "ch_synthetic000001",
  line: "txn_synthetic000001",
  amount: 51524,
  created: 1791496583,
};
const RECORDED_ACH = {
  intent: "pi_synthetic000004",
  charge: "py_synthetic000001",
  line: "txn_synthetic000003",
  amount: 50403,
  fee: 403,
  net: 50000,
  created: 1791496593,
};
// The card payment of the dispute run, which Stripe disputed in the second it succeeded.
const RECORDED_CARD_DISPUTE = {
  intent: "pi_synthetic000001",
  charge: "ch_synthetic000001",
  line: "txn_synthetic000002",
  dispute: "du_synthetic000001",
  disputeLine: "txn_synthetic000001",
  amount: 20000,
  fee: 610,
  net: 19390,
  taken: 21500,
  created: 1791571514,
  at: 1791571526,
};
// The ACH debit of the first run that Stripe disputed about ten minutes after it succeeded.
const RECORDED_ACH_DISPUTE = {
  intent: "pi_synthetic000006",
  charge: "py_synthetic000003",
  line: "txn_synthetic000006",
  dispute: "du_synthetic000001",
  disputeLine: "txn_synthetic000007",
  amount: 20000,
  fee: 160,
  net: 19840,
  taken: 21500,
  created: 1791496659,
  at: 1791497247,
};
// The two refunded card payments, as the refund run names them and as the dispute run does.
const RECORDED_REFUND = {
  full: {
    intent: "pi_synthetic000002",
    charge: "ch_synthetic000001",
    line: "txn_synthetic000001",
    refund: "re_synthetic000001",
    refundLine: "txn_synthetic000003",
    amount: 20000,
    created: 1791571478,
    at: 1791571487,
  },
  fullLater: {
    intent: "pi_synthetic000011",
    charge: "ch_synthetic000004",
    line: "txn_synthetic000020",
    refund: "re_synthetic000001",
    refundLine: "txn_synthetic000017",
    amount: 20000,
    created: 1791571478,
    at: 1791571487,
  },
  part: {
    intent: "pi_synthetic000003",
    charge: "ch_synthetic000003",
    line: "txn_synthetic000002",
    refund: "re_synthetic000002",
    refundLine: "txn_synthetic000004",
    amount: 20000,
    refunded: 5000,
    created: 1791571483,
    at: 1791571488,
  },
  partLater: {
    intent: "pi_synthetic000012",
    charge: "ch_synthetic000005",
    line: "txn_synthetic000019",
    refund: "re_synthetic000002",
    refundLine: "txn_synthetic000018",
    amount: 20000,
    refunded: 5000,
    created: 1791571483,
    at: 1791571488,
  },
};
// The recorded payout's line for a charge of each kind. The charge inside it is replaced whole.
const RECORDED_LINE = {
  card: { line: "txn_synthetic000012", amount: 20000, fee: 610, net: 19390 },
  ach: { line: "txn_synthetic000011", amount: 50403, fee: 403, net: 50000 },
};
// The recorded payout's line for a disputed ACH debit: the whole charge taken back, and a fee.
const RECORDED_DISPUTE_LINE = {
  line: "txn_synthetic000007",
  dispute: "du_synthetic000001",
  charge: "py_synthetic000002",
  intent: "pi_synthetic000005",
  amount: 20000,
  taken: 21500,
};
/** What Stripe took for a dispute beside the amount, in every recording: $15.00. */
export const DISPUTE_FEE = 1500;
const RECORDED_PAYOUT = {
  payout: "po_synthetic000001",
  line: "txn_synthetic000005",
  account: RECORDED_ACCOUNT,
  amount: 147060,
  paid: 1791506838,
  // The charges behind its two dispute lines, and the payment each names. Stripe is asked for
  // them, and no recording holds either: the tests that replay this payout build them from the
  // recorded charge of the disputed ACH debit, and so does this.
  disputed: [
    ["py_synthetic000002", "pi_synthetic000005"],
    ["py_synthetic000005", "pi_synthetic000009"],
  ],
};

/**
 * What a test payment method does. A method not listed is refused as unknown. A payment's later
 * life is told to the stand-in through `TIMELINES`; nothing else names a method. A fixture binds
 * one card method and one ACH method, so a dispute or a refund is a timeline a test starts, and
 * not a method of its own as it is at Stripe.
 */
const PAYMENT_METHODS = {
  pm_card_visa: { method: "card", status: "succeeded" },
  pm_usBankAccount_success: { method: "ach", status: "processing" },
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
        // Charges Stripe holds that are no payment's of this run: those the recorded payout names.
        charges: new Map(),
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
      "mandate_data[customer_acceptance][type]",
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
      !generation ||
      // An ACH debit carries a mandate accepted offline, and a card charge carries none.
      (form.get("mandate_data[customer_acceptance][type]") ?? null) !==
        (how.method === "ach" ? "offline" : null)
    )
      throw new Unknown("A charge was not made as the adapter makes one.");
    const created = now();
    const payment = {
      id: newId("pi"),
      // Stripe names a card charge `ch_` and an ACH debit `py_`.
      charge: newId(how.method === "ach" ? "py" : "ch"),
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
      // The dispute and the refund Stripe holds for it, once a timeline has made one, and the
      // payout that carries an ACH dispute's line.
      dispute: null,
      disputeLine: null,
      disputedAt: null,
      returnPayout: null,
      refund: null,
      refundLine: null,
      refunded: null,
      timelines: [],
    };
    if (how.method === "ach") {
      // Processing, with its charge pending, until the debit succeeds.
      payment.intent = asPayment(recorded.ach.intent, payment, RECORDED_ACH);
      payment.chargeObject = asPayment(
        recorded.ach.events[2].data.object,
        payment,
        RECORDED_ACH,
      );
    } else {
      payment.intent = asPayment(recorded.card.intent, payment, RECORDED_CARD);
      // The charge as its last recorded event left it, with its balance line attached.
      payment.chargeObject = asPayment(
        recorded.card.events.at(-1).data.object,
        payment,
        RECORDED_CARD,
      );
    }
    book.payments.set(payment.id, payment);
    book.idempotency.set(idempotencyKey, { fingerprint, id: payment.id });
    return { status: 200, body: payment.intent };
  }

  // A recorded object about a payment, made this payment's: its ids, amounts, times and the two
  // ids of LeaseBook's it carries. `was` says what the recorded payment was known by. A time is
  // moved by as much as the payment's own creation was, or to this moment when it belongs to a
  // later timeline, and never past this moment.
  function asPayment(object, payment, was) {
    const net = payment.amount - payment.fee;
    const ids = {
      [was.intent]: payment.id,
      [was.charge]: payment.charge,
      [was.line]: payment.line,
      [RECORDED_ACCOUNT]: payment.account,
    };
    const amounts = { [was.amount]: payment.amount };
    if (was.fee !== undefined) amounts[was.fee] = payment.fee;
    if (was.net !== undefined) amounts[was.net] = net;
    if (was.dispute !== undefined) {
      ids[was.dispute] = payment.dispute;
      ids[was.disputeLine] = payment.disputeLine;
      amounts[-was.amount] = -payment.amount;
      amounts[-was.taken] = -(payment.amount + DISPUTE_FEE);
    }
    if (was.refund !== undefined) {
      ids[was.refund] = payment.refund;
      ids[was.refundLine] = payment.refundLine;
      if (was.refunded !== undefined) amounts[was.refunded] = payment.refunded;
    }
    const moment = now();
    const copy = substitute(object, {
      ids,
      amounts,
      time: (at) =>
        Math.min(
          moment,
          was.at !== undefined && at >= was.at
            ? at + moment - was.at
            : at + payment.created - was.created,
        ),
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
      const held = payment?.chargeObject ?? book.charges.get(charge[1]);
      return held ? { status: 200, body: held } : missing("charge", charge[1]);
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

  // The events of a timeline as this payment's: each a recorded event under an id of its own.
  // `own` says the request that caused the recorded event was the charge itself, whose idempotency
  // key is then this payment's.
  const asEvents = (events, payment, was, { own = false } = {}) =>
    events.map((event) => {
      const copy = asPayment(event, payment, was);
      copy.id = newId("evt");
      if (own && copy.request?.idempotency_key)
        copy.request.idempotency_key = payment.idempotencyKey;
      return copy;
    });

  /**
   * The recorded timelines a payment can be moved along, by name. Each sends, in the order Stripe
   * sent them, the events recorded for a payment of that kind. `after` names what must have
   * happened to the payment first, and `then` what the payment is at Stripe afterwards. A later
   * timeline is one more entry.
   */
  const TIMELINES = {
    // A card payment that succeeds: `charge.succeeded`, `payment_intent.succeeded`,
    // `payment_intent.created`, and `charge.updated` once the balance line exists.
    "card-succeeds": {
      method: "card",
      events: (payment) =>
        asEvents(recorded.card.events, payment, RECORDED_CARD, { own: true }),
      then: (payment) => {
        payment.paid = true;
      },
    },
    // An ACH debit that succeeds: `payment_intent.processing`, `payment_intent.created` and
    // `charge.pending` at once, then `charge.updated` with the balance line, two `mandate.updated`,
    // `payment_intent.succeeded` and `charge.succeeded`. Stripe took about twenty seconds over it.
    "ach-succeeds": {
      method: "ach",
      events: (payment) =>
        asEvents(recorded.ach.events, payment, RECORDED_ACH, { own: true }),
      then: (payment) => {
        payment.paid = true;
        payment.status = "succeeded";
        payment.intent = asPayment(recorded.ach.final, payment, RECORDED_ACH);
        payment.chargeObject = asPayment(
          recorded.ach.charge,
          payment,
          RECORDED_ACH,
        );
      },
    },
    // A card payment Stripe says is disputed: `charge.dispute.created` and
    // `charge.dispute.funds_withdrawn`, the dispute open and needing a response.
    "card-disputed": {
      method: "card",
      after: ["card-succeeds"],
      begin: (payment) => {
        payment.dispute = newId("du");
        payment.disputeLine = newId("txn");
        payment.disputedAt = now();
      },
      events: (payment) =>
        asEvents(recorded.card.dispute.events, payment, RECORDED_CARD_DISPUTE, {
          own: true,
        }),
      then: (payment) => {
        payment.chargeObject = asPayment(
          recorded.card.dispute.charge,
          payment,
          RECORDED_CARD_DISPUTE,
        );
      },
    },
    // An ACH debit the bank took back, which Stripe reports as a dispute already lost:
    // `charge.dispute.created`, `charge.dispute.funds_withdrawn` and `charge.dispute.closed`.
    "ach-disputed": {
      method: "ach",
      after: ["ach-succeeds"],
      begin: (payment) => {
        payment.dispute = newId("du");
        payment.disputeLine = newId("txn");
        payment.disputedAt = now();
      },
      events: (payment) =>
        asEvents(recorded.ach.dispute.events, payment, RECORDED_ACH_DISPUTE),
      then: (payment) => {
        payment.chargeObject = asPayment(
          recorded.ach.dispute.charge,
          payment,
          RECORDED_ACH_DISPUTE,
        );
      },
    },
    // A card payment refunded in full at Stripe: `refund.created` and `charge.refunded`, then
    // `refund.updated` and `charge.refund.updated`.
    "card-refunded": {
      method: "card",
      after: ["card-succeeds"],
      never: ["card-refunded-in-part"],
      begin: (payment) => {
        payment.refund = newId("re");
        payment.refundLine = newId("txn");
        payment.refunded = payment.amount;
      },
      events: (payment) => [
        ...asEvents(recorded.card.refund.full, payment, RECORDED_REFUND.full),
        ...asEvents(
          recorded.card.refund.fullLater,
          payment,
          RECORDED_REFUND.fullLater,
        ),
      ],
      then: (payment) => {
        payment.chargeObject = asPayment(
          recorded.card.refund.full[1].data.object,
          payment,
          RECORDED_REFUND.full,
        );
      },
    },
    // A card payment of which a part was refunded: the same four events, with the amount refunded
    // so far on the charge. The test names the part, in cents.
    "card-refunded-in-part": {
      method: "card",
      after: ["card-succeeds"],
      never: ["card-refunded"],
      begin: (payment, given) => {
        const part = given.refund;
        if (!Number.isInteger(part) || part <= 0 || part >= payment.amount)
          throw new Unknown(
            "A refund of part names `refund`, in cents: more than nothing and less than the charge.",
            400,
          );
        payment.refund = newId("re");
        payment.refundLine = newId("txn");
        payment.refunded = part;
      },
      events: (payment) => [
        ...asEvents(recorded.card.refund.part, payment, RECORDED_REFUND.part),
        ...asEvents(
          recorded.card.refund.partLater,
          payment,
          RECORDED_REFUND.partLater,
        ),
      ],
      then: (payment) => {
        payment.chargeObject = asPayment(
          recorded.card.refund.part[1].data.object,
          payment,
          RECORDED_REFUND.part,
        );
      },
    },
  };

  async function runTimeline(operation, given, send) {
    const name = given.timeline;
    const timeline = Object.hasOwn(TIMELINES, name) ? TIMELINES[name] : null;
    if (!timeline)
      throw new Unknown(`No recorded timeline named ${name}.`, 400);
    const payment = paymentFor(operation);
    if (
      payment.method !== timeline.method ||
      payment.timelines.includes(name) ||
      (timeline.after ?? []).some(
        (first) => !payment.timelines.includes(first),
      ) ||
      (timeline.never ?? []).some((other) => payment.timelines.includes(other))
    )
      throw new Unknown(
        `The timeline ${name} does not apply to payment ${operation} as it stands, or has already run.`,
        409,
      );
    timeline.begin?.(payment, given);
    payment.timelines.push(name);
    const book = held.get(payment.account);
    // What Stripe holds changes first, and its events say so afterwards.
    const events = timeline.events(payment);
    timeline.then?.(payment);
    const sent = [];
    for (const event of events) sent.push(await happen(book, event, send));
    return { payment: payment.id, deliveries: sent };
  }

  function paymentFor(operation) {
    for (const book of held.values())
      for (const payment of book.payments.values())
        if (payment.operation === operation) return payment;
    throw new Unknown(`No payment was made for operation ${operation}.`, 404);
  }

  /**
   * Forms one automatic payout and sends its events. Stripe decides what a payout covers; here the
   * test does, standing in for Stripe: the payments it names, and under `returns` the payments
   * whose ACH dispute the payout also carries. The payout's own line and its events are the
   * recorded ones, each payment's line is the recorded line of a charge of its kind, and each
   * dispute's line is the recorded line of a disputed ACH debit, all with this payout's ids,
   * amounts and times. No recorded payout holds only good payments, so the whole is built from
   * those shapes.
   */
  async function formPayout(given, send) {
    const operations = given.payments;
    const returned = given.returns ?? [];
    if (
      !Array.isArray(operations) ||
      !Array.isArray(returned) ||
      operations.length === 0
    )
      throw new Unknown("A payout is formed from at least one payment.", 400);
    const payments = operations.map(paymentFor);
    const disputes = returned.map(paymentFor);
    const account = payments[0].account;
    if (
      new Set(operations).size !== operations.length ||
      new Set(returned).size !== returned.length ||
      payments.some(
        (payment) =>
          payment.account !== account ||
          payment.payout !== null ||
          !payment.paid,
      ) ||
      disputes.some(
        (payment) =>
          payment.account !== account ||
          payment.returnPayout !== null ||
          !payment.timelines.includes("ach-disputed"),
      )
    )
      throw new Unknown(
        "A payout holds payments of one account that Stripe has said were paid and that are in no other payout, and the disputes of ACH debits that are in no other payout.",
        409,
      );
    const amount =
      payments.reduce((sum, payment) => sum + payment.amount - payment.fee, 0) -
      disputes.reduce((sum, payment) => sum + payment.amount + DISPUTE_FEE, 0);
    if (amount <= 0)
      throw new Unknown(
        "What the payout holds comes to nothing or less, and Stripe pays out only a balance.",
        409,
      );
    const book = held.get(account);
    const id = newId("po");
    const ownLine = newId("txn");
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
        const was = RECORDED_LINE[payment.method];
        const line = substitute(recorded[payment.method].line, {
          ids: { [was.line]: payment.line },
          amounts: {
            [was.amount]: payment.amount,
            [was.fee]: payment.fee,
            [was.net]: payment.amount - payment.fee,
          },
          time: () => payment.created,
        });
        // The charge the line came from is this payment's own, as the stand-in holds it.
        line.source = payment.chargeObject;
        return line;
      }),
      // The whole charge taken back and Stripe's fee with it, with the dispute it came from.
      ...disputes.map((payment) =>
        substitute(recorded.ach.dispute.line, {
          ids: {
            [RECORDED_DISPUTE_LINE.line]: payment.disputeLine,
            [RECORDED_DISPUTE_LINE.dispute]: payment.dispute,
            [RECORDED_DISPUTE_LINE.charge]: payment.charge,
            [RECORDED_DISPUTE_LINE.intent]: payment.id,
          },
          amounts: {
            [RECORDED_DISPUTE_LINE.amount]: payment.amount,
            [-RECORDED_DISPUTE_LINE.amount]: -payment.amount,
            [-RECORDED_DISPUTE_LINE.taken]: -(payment.amount + DISPUTE_FEE),
          },
          time: () => payment.disputedAt,
        }),
      ),
    ];
    const made = {
      id,
      account,
      amount,
      status: "paid",
      // The payout as its last event left it.
      object: events.at(-1).data.object,
      paidEvent: events.find((event) => event.type === "payout.paid"),
      lines,
      payments: payments.map((payment) => payment.operation),
      returns: disputes.map((payment) => payment.operation),
      recorded: false,
    };
    book.payouts.set(id, made);
    for (const payment of payments) payment.payout = id;
    for (const payment of disputes) payment.returnPayout = id;
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

  /**
   * Holds the one payout a sandbox has really made, and sends its four events: $1,470.60, with the
   * fourteen balance lines Stripe listed for it. Everything is as recorded but two things. The
   * connected account is the fixture's. And the payout's own id is new each time, because a
   * fixture's database is kept from one run to the next and a payout is known there by its id; the
   * lines keep their recorded ids, amounts and times, and name the payments of the three fixture
   * generations that made them, none of which is this fixture's. The charges behind its two
   * dispute lines are built, as `RECORDED_PAYOUT.disputed` says.
   */
  async function recordedPayout(given, send) {
    const account = given.account ?? [...held.keys()][0];
    const book = held.get(account);
    if (!book)
      throw new Unknown(
        "The recorded payout was asked for on an account this stand-in does not hold.",
        404,
      );
    const id = newId("po");
    const asHeld = (object) =>
      substitute(object, {
        ids: {
          [RECORDED_PAYOUT.payout]: id,
          [RECORDED_PAYOUT.account]: account,
        },
      });
    const events = recorded.payout.events.map((event) => {
      const copy = asHeld(event);
      copy.id = newId("evt");
      return copy;
    });
    for (const [charge, payment] of RECORDED_PAYOUT.disputed)
      book.charges.set(charge, {
        ...structuredClone(recorded.ach.dispute.charge),
        id: charge,
        payment_intent: payment,
      });
    book.payouts.set(id, {
      id,
      account,
      amount: RECORDED_PAYOUT.amount,
      status: "paid",
      object: events.at(-1).data.object,
      paidEvent: events.find((event) => event.type === "payout.paid"),
      lines: recorded.payout.lines.map(asHeld),
      payments: [],
      returns: [],
      recorded: true,
    });
    const sent = [];
    for (const event of events) sent.push(await happen(book, event, send));
    return {
      id,
      account,
      amount: RECORDED_PAYOUT.amount,
      net: (RECORDED_PAYOUT.amount / 100).toFixed(2),
      lines: recorded.payout.lines.length,
      deliveries: sent,
    };
  }

  /**
   * Says a payout failed, as Stripe can of one it marked paid. No payout has failed in a sandbox,
   * so `payout.failed` is not recorded: it is built as the adapter's tests build it, from the
   * payout's own `payout.paid` event with the status `failed` and a failure code.
   */
  async function failPayout(id, send) {
    const book = [...held.values()].find((item) => item.payouts.has(id));
    if (!book) throw new Unknown(`No payout ${id} to fail.`, 404);
    const made = book.payouts.get(id);
    if (made.status === "failed")
      throw new Unknown(`The payout ${id} has already failed.`, 409);
    const failed = structuredClone(made.paidEvent);
    failed.id = newId("evt");
    failed.type = "payout.failed";
    failed.created = Math.max(now(), failed.created);
    Object.assign(failed.data.object, {
      status: "failed",
      failure_code: "account_closed",
      failure_message: "[scrubbed]",
    });
    made.status = "failed";
    made.object = failed.data.object;
    return { id, deliveries: [await happen(book, failed, send)] };
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
          dispute: payment.dispute,
          returnPayout: payment.returnPayout,
          refunded: payment.refunded,
        })),
      ),
      payouts: [...held.values()].flatMap((book) =>
        [...book.payouts.values()].map((payout) => ({
          id: payout.id,
          account: payout.account,
          amount: payout.amount,
          status: payout.status,
          payments: payout.payments,
          returns: payout.returns,
          recorded: payout.recorded,
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
        body: await runTimeline(timeline[1], body, body.deliver !== false),
      };
    if (method === "POST" && path === "/stand-in/payouts")
      return {
        status: 200,
        body: await formPayout(body, body.deliver !== false),
      };
    if (method === "POST" && path === "/stand-in/payouts/recorded")
      return {
        status: 200,
        body: await recordedPayout(body, body.deliver !== false),
      };
    const failing = /^\/stand-in\/payouts\/(po_[A-Za-z0-9]+)\/fail$/.exec(path);
    if (method === "POST" && failing)
      return {
        status: 200,
        body: await failPayout(failing[1], body.deliver !== false),
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
