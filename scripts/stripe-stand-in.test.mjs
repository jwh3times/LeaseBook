import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { createServer } from "node:http";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

import { API_VERSION, verifySignature } from "./stripe-probe.mjs";
import {
  STAND_IN_KEY,
  createStandIn,
  loopbackAddress,
  main,
  manifestAccounts,
  parseArgs,
  serve,
  signatureHeader,
  stripeFee,
  substitute,
} from "./stripe-stand-in.mjs";

const ACCOUNT = "acct_standin000001";
const OTHER = "acct_standin000002";
// Made here for the run and never committed: shaped like a signing secret, and not one of Stripe's.
const SECRET = `whsec_${randomUUID().replaceAll("-", "")}`;
const GENERATION = "0199e2a0-0000-7000-8000-000000000001";

/** A stand-in whose deliveries are kept and answered 204, with a clock a test can move. */
function standIn({ status = 204 } = {}) {
  const sent = [];
  const clock = { now: 1_800_000_000 };
  const made = createStandIn({
    accounts: [ACCOUNT, OTHER],
    webhookSecret: SECRET,
    now: () => clock.now,
    deliver: async (body, signature) => {
      sent.push({ body, signature, event: JSON.parse(body) });
      return typeof status === "function" ? status(sent.length) : status;
    },
  });
  const ask = (method, url, { account = ACCOUNT, key, form, json } = {}) =>
    made.handle({
      method,
      url,
      headers: {
        authorization: `Bearer ${STAND_IN_KEY}`,
        "stripe-version": API_VERSION,
        ...(account ? { "stripe-account": account } : {}),
        ...(key ? { "idempotency-key": key } : {}),
        ...(json ? { "content-type": "application/json" } : {}),
      },
      body: json ? JSON.stringify(json) : (form ?? ""),
    });
  return { ...made, ask, sent, clock };
}

/** The form the adapter sends for a card charge. */
function charge(operation, amount = 10330, method = "pm_card_visa") {
  return new URLSearchParams({
    amount: String(amount),
    currency: "usd",
    payment_method: method,
    confirm: "true",
    "automatic_payment_methods[enabled]": "true",
    "automatic_payment_methods[allow_redirects]": "never",
    "metadata[leasebook_operation]": operation,
    "metadata[leasebook_generation]": GENERATION,
  }).toString();
}

async function paid(h, amount = 10330) {
  const operation = randomUUID();
  const made = await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation, amount),
  });
  assert.equal(made.status, 200);
  const moved = await h.ask(
    "POST",
    `/stand-in/payments/${operation}/timeline`,
    { account: null, json: { timeline: "card-succeeds" } },
  );
  assert.equal(moved.status, 200);
  return { operation, intent: made.body, deliveries: moved.body.deliveries };
}

test("only an address on this machine is an address", () => {
  for (const good of [
    "http://127.0.0.1:4010",
    "http://localhost:4010/",
    "http://[::1]:4010",
  ])
    assert.ok(loopbackAddress(good), good);
  for (const bad of [
    "https://127.0.0.1:4010",
    "http://127.0.0.1",
    "http://api.stripe.com:80",
    "http://localhost.example:4010",
    "http://0.0.0.0:4010",
    "http://127.1:4010",
    "http://user@127.0.0.1:4010",
    "http://127.0.0.1:4010/v1",
    "http://127.0.0.1:4010?x=1",
    "http://127.0.0.1:4010 ",
    "",
  ])
    assert.equal(loopbackAddress(bad), null, bad);
  // A callback has a path, and is held to the same machine.
  assert.ok(
    loopbackAddress("http://127.0.0.1:5080/callbacks/payments/stripe", {
      path: true,
    }),
  );
  assert.equal(
    loopbackAddress("http://example.test:5080/callbacks/payments/stripe", {
      path: true,
    }),
    null,
  );
});

test("the fee is the recorded rate, to the nearest cent", () => {
  // The three charges the recordings hold a fee for.
  assert.equal(stripeFee("card", 51524), 1524);
  assert.equal(stripeFee("card", 20000), 610);
  assert.equal(stripeFee("ach", 50403), 403);
  assert.throws(() => stripeFee("wire", 100));
});

test("a substitution replaces whole identifiers, amounts under an amount's name and times under a time's", () => {
  const copy = substitute(
    {
      id: "pi_old",
      note: "about pi_old",
      amount: 100,
      count: 100,
      created: 5,
      inner: [{ payment_intent: "pi_old", amount_received: 100, net: 100 }],
    },
    { ids: { pi_old: "pi_new" }, amounts: { 100: 250 }, time: (at) => at + 1 },
  );
  assert.deepEqual(copy, {
    id: "pi_new",
    note: "about pi_old",
    amount: 250,
    count: 100,
    created: 6,
    inner: [{ payment_intent: "pi_new", amount_received: 250, net: 250 }],
  });
});

test("a request without the stand-in's key is refused and kept as unexpected", async () => {
  const h = standIn();
  for (const authorization of [
    undefined,
    "Bearer sk_test_another",
    STAND_IN_KEY,
  ]) {
    const refused = await h.handle({
      method: "GET",
      url: "/v1/account",
      headers: { authorization, "stripe-version": API_VERSION },
      body: "",
    });
    assert.equal(refused.status, 401);
  }
  // The control surface is held to the same key. Only the health check is open, and it says nothing.
  const control = await h.handle({
    method: "GET",
    url: "/stand-in/state",
    headers: {},
    body: "",
  });
  assert.equal(control.status, 401);
  const health = await h.handle({
    method: "GET",
    url: "/stand-in/health",
    headers: {},
    body: "",
  });
  assert.deepEqual([health.status, health.body], [200, { ok: true }]);
  assert.equal(h.state().unexpected.length, 4);
});

test("the key's own account and a fixture's account are answered from the recordings", async () => {
  const h = standIn();
  const own = await h.ask("GET", "/v1/account", { account: null });
  assert.equal(own.status, 200);
  assert.equal(own.body.object, "account");
  // The platform's account is not one of the fixtures', which is what the host's barrier asks.
  assert.ok(![ACCOUNT, OTHER].includes(own.body.id));
  const connected = await h.ask("GET", `/v1/accounts/${OTHER}`, {
    account: null,
  });
  assert.equal(connected.body.id, OTHER);
  assert.equal(connected.body.controller.fees.payer, "account");
  const stranger = await h.ask("GET", "/v1/accounts/acct_standin000009", {
    account: null,
  });
  assert.equal(stranger.status, 404);
  assert.equal(stranger.body.error.code, "resource_missing");
  assert.deepEqual(h.state().unexpected, []);
});

test("a charge is answered in the recorded shape, and found again by its id and in the list", async () => {
  const h = standIn();
  const operation = randomUUID();
  const made = await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation),
  });
  assert.equal(made.status, 200);
  const intent = made.body;
  assert.match(intent.id, /^pi_standin[0-9a-f]+$/);
  assert.match(intent.latest_charge, /^ch_standin[0-9a-f]+$/);
  assert.deepEqual(
    [
      intent.object,
      intent.status,
      intent.amount,
      intent.amount_received,
      intent.currency,
      intent.livemode,
      intent.created,
    ],
    ["payment_intent", "succeeded", 10330, 10330, "usd", false, h.clock.now],
  );
  assert.deepEqual(intent.metadata, {
    leasebook_generation: GENERATION,
    leasebook_operation: operation,
  });
  // Nothing of the recording's own is left where a value of this payment's belongs.
  assert.doesNotMatch(
    JSON.stringify(intent),
    /pi_synthetic|ch_synthetic|51524/,
  );

  const again = await h.ask("GET", `/v1/payment_intents/${intent.id}`);
  assert.deepEqual(again.body, intent);
  const listed = await h.ask(
    "GET",
    `/v1/payment_intents?created[gte]=${h.clock.now - 300}&limit=100`,
  );
  assert.deepEqual(
    [listed.body.object, listed.body.has_more, listed.body.data],
    ["list", false, [intent]],
  );
  // Another fixture's account holds none of it.
  const elsewhere = await h.ask("GET", "/v1/payment_intents?limit=100", {
    account: OTHER,
  });
  assert.deepEqual(elsewhere.body.data, []);
  assert.equal(
    (await h.ask("GET", `/v1/payment_intents/${intent.id}`, { account: OTHER }))
      .status,
    404,
  );
  const held = await h.ask("GET", `/v1/charges/${intent.latest_charge}`);
  assert.deepEqual(
    [
      held.body.object,
      held.body.payment_intent,
      held.body.payment_method_details.type,
    ],
    ["charge", intent.id, "card"],
  );
  assert.deepEqual(h.state().unexpected, []);
});

test("the same idempotency key answers the same payment, and with other parameters is refused as recorded", async () => {
  const h = standIn();
  const operation = randomUUID();
  const first = await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation),
  });
  const repeat = await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation),
  });
  assert.deepEqual(repeat.body, first.body);
  assert.equal(repeat.headers["Idempotent-Replayed"], "true");
  const changed = await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation, 10430),
  });
  assert.equal(changed.status, 400);
  assert.equal(changed.body.error.type, "idempotency_error");
  assert.ok(changed.body.error.message.includes(operation));
  assert.equal(h.state().payments.length, 1);
});

test("a charge the adapter would not make is refused and kept as unexpected", async () => {
  const h = standIn();
  const operation = randomUUID();
  const cases = [
    // A payment method with no recorded timeline here.
    { key: operation, form: charge(operation, 10330, "pm_card_unknown") },
    // A field the adapter does not send.
    {
      key: operation,
      form: `${charge(operation)}&payment_method_types[]=card`,
    },
    // No idempotency key.
    { form: charge(operation) },
    // An account this stand-in does not hold.
    { key: operation, form: charge(operation), account: "acct_standin000009" },
  ];
  for (const given of cases) {
    const refused = await h.ask("POST", "/v1/payment_intents", given);
    assert.ok(refused.status >= 400, JSON.stringify(given));
    assert.equal(refused.body.error.code, "stand_in_unknown_request");
  }
  assert.equal(h.state().unexpected.length, cases.length);
  assert.equal(h.state().payments.length, 0);
});

test("a request that is not one the adapter makes is refused and kept as unexpected", async () => {
  const h = standIn();
  for (const [method, url] of [
    ["POST", "/v1/refunds"],
    ["GET", "/v1/balance"],
    ["DELETE", "/v1/payment_intents/pi_x"],
    ["GET", "/v1/balance_transactions?payout=po_unknown"],
    ["POST", "/stand-in/unknown"],
  ]) {
    const refused = await h.ask(method, url, { json: {} });
    assert.ok(refused.status >= 400, url);
  }
  // Recorded at one API version, and answered at no other.
  const other = await h.handle({
    method: "GET",
    url: "/v1/account",
    headers: {
      authorization: `Bearer ${STAND_IN_KEY}`,
      "stripe-version": "2020-08-27",
    },
    body: "",
  });
  assert.equal(other.status, 400);
  assert.equal(h.state().unexpected.length, 6);
});

test("the card timeline sends the four recorded events, signed, in the order Stripe sent them", async () => {
  const h = standIn();
  const { operation, intent, deliveries } = await paid(h);
  assert.deepEqual(
    deliveries.map((sent) => [sent.type, sent.status]),
    [
      ["charge.succeeded", 204],
      ["payment_intent.succeeded", 204],
      ["payment_intent.created", 204],
      ["charge.updated", 204],
    ],
  );
  assert.equal(new Set(deliveries.map((sent) => sent.id)).size, 4);
  for (const sent of h.sent) {
    // Stripe's scheme, as the probe verifies a real delivery.
    assert.ok(
      verifySignature(sent.body, sent.signature, SECRET, h.clock.now),
      sent.event.type,
    );
    assert.equal(
      verifySignature(sent.body, sent.signature, `${SECRET}x`, h.clock.now),
      false,
    );
    const event = sent.event;
    assert.deepEqual(
      [event.object, event.account, event.livemode, event.api_version],
      ["event", ACCOUNT, false, API_VERSION],
    );
    assert.ok(event.created <= h.clock.now);
    assert.deepEqual(event.data.object.metadata, {
      leasebook_generation: GENERATION,
      leasebook_operation: operation,
    });
    assert.doesNotMatch(
      sent.body,
      /pi_synthetic|ch_synthetic|evt_synthetic|txn_synthetic|acct_synthetic/,
    );
  }
  const succeeded = h.sent[1].event.data.object;
  assert.deepEqual(
    [succeeded.id, succeeded.amount, succeeded.status],
    [intent.id, 10330, "succeeded"],
  );
  // The same timeline does not run twice, and no other payment is moved by it.
  const twice = await h.ask(
    "POST",
    `/stand-in/payments/${operation}/timeline`,
    { account: null, json: { timeline: "card-succeeds" } },
  );
  assert.equal(twice.status, 409);
  const nobody = await h.ask(
    "POST",
    `/stand-in/payments/${randomUUID()}/timeline`,
    { account: null, json: { timeline: "card-succeeds" } },
  );
  assert.equal(nobody.status, 404);
  const unnamed = await h.ask(
    "POST",
    `/stand-in/payments/${operation}/timeline`,
    { account: null, json: { timeline: "card-vanishes" } },
  );
  assert.equal(unnamed.status, 400);
  assert.equal(h.sent.length, 4);
});

test("events are listed newest first, by type and time, a page at a time, whether or not they were delivered", async () => {
  const h = standIn();
  const operation = randomUUID();
  await h.ask("POST", "/v1/payment_intents", {
    key: operation,
    form: charge(operation),
  });
  // Made and not delivered: a lost event, which only a sweep finds.
  const quiet = await h.ask(
    "POST",
    `/stand-in/payments/${operation}/timeline`,
    { account: null, json: { timeline: "card-succeeds", deliver: false } },
  );
  assert.deepEqual(
    quiet.body.deliveries.map((sent) => sent.status),
    [null, null, null, null],
  );
  assert.equal(h.sent.length, 0);

  const all = await h.ask("GET", "/v1/events?limit=100");
  assert.equal(all.body.data.length, 4);
  assert.equal(all.body.data[0].type, "charge.updated");
  const wanted = await h.ask(
    "GET",
    `/v1/events?created[gte]=${h.clock.now - 900}&limit=100&types[0]=payment_intent.succeeded&types[1]=payout.paid`,
  );
  assert.deepEqual(
    wanted.body.data.map((event) => event.type),
    ["payment_intent.succeeded"],
  );
  const first = await h.ask("GET", "/v1/events?limit=3");
  assert.deepEqual([first.body.data.length, first.body.has_more], [3, true]);
  const rest = await h.ask(
    "GET",
    `/v1/events?limit=3&starting_after=${first.body.data[2].id}`,
  );
  assert.deepEqual([rest.body.data.length, rest.body.has_more], [1, false]);
  const later = await h.ask(
    "GET",
    `/v1/events?created[gte]=${h.clock.now + 1}&limit=100`,
  );
  assert.deepEqual(later.body.data, []);
  assert.deepEqual(
    (await h.ask("GET", "/v1/events?limit=100", { account: OTHER })).body.data,
    [],
  );
  assert.deepEqual(h.state().unexpected, []);
});

test("a payout formed from paid payments comes to their net, with Stripe's lines and its four events", async () => {
  const h = standIn();
  const first = await paid(h, 10330);
  const second = await paid(h, 51524);
  h.sent.length = 0;
  h.clock.now += 3600;

  const formed = await h.ask("POST", "/stand-in/payouts", {
    account: null,
    json: { payments: [first.operation, second.operation] },
  });
  assert.equal(formed.status, 200);
  const payout = formed.body;
  // 103.30 less 3.30, and 515.24 less 15.24.
  assert.deepEqual(
    [payout.amount, payout.net, payout.account],
    [60000, "600.00", ACCOUNT],
  );
  assert.match(payout.id, /^po_standin[0-9a-f]+$/);
  assert.deepEqual(
    payout.deliveries.map((sent) => [sent.type, sent.status]),
    [
      ["payout.created", 204],
      ["payout.reconciliation_completed", 204],
      ["payout.paid", 204],
      ["payout.updated", 204],
    ],
  );
  const paidEvent = h.sent[2].event;
  assert.ok(
    verifySignature(h.sent[2].body, h.sent[2].signature, SECRET, h.clock.now),
  );
  assert.deepEqual(
    [
      paidEvent.account,
      paidEvent.livemode,
      paidEvent.created,
      paidEvent.data.object.id,
      paidEvent.data.object.amount,
      paidEvent.data.object.status,
      paidEvent.data.object.automatic,
      paidEvent.data.object.method,
      paidEvent.data.object.reconciliation_status,
    ],
    [
      ACCOUNT,
      false,
      h.clock.now,
      payout.id,
      60000,
      "paid",
      true,
      "standard",
      "completed",
    ],
  );
  // In transit when it was made, as recorded.
  assert.equal(h.sent[0].event.data.object.status, "in_transit");
  assert.ok(h.sent[0].event.created < paidEvent.created);

  const lines = await h.ask(
    "GET",
    `/v1/balance_transactions?payout=${payout.id}&limit=100&expand[]=data.source`,
  );
  assert.deepEqual(
    lines.body.data.map((line) => [
      line.type,
      line.amount,
      line.fee,
      line.net,
      line.source.object,
    ]),
    [
      ["payout", -60000, 0, -60000, "payout"],
      ["charge", 10330, 330, 10000, "charge"],
      ["charge", 51524, 1524, 50000, "charge"],
    ],
  );
  // Stripe's own lines come to nothing: what the charges brought in is what the payout took out.
  assert.equal(
    lines.body.data.reduce((sum, line) => sum + line.net, 0),
    0,
  );
  const [own, one] = lines.body.data;
  assert.equal(own.source.id, payout.id);
  assert.deepEqual(
    [one.source.payment_intent, one.source.metadata, one.fee_details[0].amount],
    [
      first.intent.id,
      {
        leasebook_generation: GENERATION,
        leasebook_operation: first.operation,
      },
      330,
    ],
  );
  assert.equal(new Set(lines.body.data.map((line) => line.id)).size, 3);
  assert.doesNotMatch(
    JSON.stringify(lines.body),
    /_synthetic0000(05|12)|147060/,
  );
  // Not expanded, a line names its source by id; and the list pages.
  const bare = await h.ask(
    "GET",
    `/v1/balance_transactions?payout=${payout.id}&limit=2`,
  );
  assert.deepEqual(
    [bare.body.data.map((line) => line.source), bare.body.has_more],
    [[payout.id, first.intent.latest_charge], true],
  );
  const held = await h.ask("GET", `/v1/payouts/${payout.id}`);
  assert.deepEqual(
    [held.body.id, held.body.status, held.body.reconciliation_status],
    [payout.id, "paid", "completed"],
  );
  assert.deepEqual(h.state().unexpected, []);

  // A payment is in one payout, and a payout holds only payments Stripe has said were paid.
  const twice = await h.ask("POST", "/stand-in/payouts", {
    account: null,
    json: { payments: [first.operation] },
  });
  assert.equal(twice.status, 409);
  const unpaid = randomUUID();
  await h.ask("POST", "/v1/payment_intents", {
    key: unpaid,
    form: charge(unpaid),
  });
  const early = await h.ask("POST", "/stand-in/payouts", {
    account: null,
    json: { payments: [unpaid] },
  });
  assert.equal(early.status, 409);
  const none = await h.ask("POST", "/stand-in/payouts", {
    account: null,
    json: { payments: [] },
  });
  assert.equal(none.status, 400);
  assert.equal(h.state().payouts.length, 1);
});

test("a delivery the host refuses is reported with the host's status and still listed", async () => {
  const h = standIn({ status: (n) => (n === 2 ? 503 : 204) });
  const { deliveries } = await paid(h);
  assert.deepEqual(
    deliveries.map((sent) => sent.status),
    [204, 503, 204, 204],
  );
  assert.equal(
    (await h.ask("GET", "/v1/events?limit=100")).body.data.length,
    4,
  );
  assert.deepEqual(
    h.state().deliveries.map((sent) => sent.status),
    [204, 503, 204, 204],
  );
});

test("the signature header is Stripe's scheme", () => {
  const header = signatureHeader("whsec_x", '{"id":"evt_1"}', 1_800_000_000);
  assert.match(header, /^t=1800000000,v1=[0-9a-f]{64}$/);
  assert.ok(
    verifySignature('{"id":"evt_1"}', header, "whsec_x", 1_800_000_000),
  );
});

test("only a sandbox fixture manifest names the accounts", () => {
  const directory = mkdtempSync(join(tmpdir(), "stand-in-"));
  try {
    const write = (name, payments) => {
      const path = join(directory, name);
      writeFileSync(path, JSON.stringify({ Payments: payments }));
      return path;
    };
    assert.deepEqual(
      manifestAccounts(
        write("good.local", {
          Mode: "StripeSandbox",
          Fixtures: [{ Account: ACCOUNT }, { Account: OTHER }],
        }),
      ),
      [ACCOUNT, OTHER],
    );
    assert.throws(() =>
      manifestAccounts(
        write("simulation.local", {
          Mode: "Simulation",
          Fixtures: [{ Account: "sim_1" }],
        }),
      ),
    );
    assert.throws(() =>
      manifestAccounts(
        write("empty.local", { Mode: "StripeSandbox", Fixtures: [] }),
      ),
    );
  } finally {
    rmSync(directory, { recursive: true, force: true });
  }
});

test("the process refuses to start without a loopback address, a loopback callback, a signing secret and a manifest", async () => {
  const good = {
    Payments__Stripe__ApiBase: "http://127.0.0.1:4010",
    Payments__Stripe__WebhookSecret: SECRET,
    Payments__ManifestPath: "/nonexistent/payment-fixture-standin.local",
  };
  assert.throws(() => parseArgs(["--listen=0.0.0.0"]), /Unknown argument/);
  await assert.rejects(
    main([], {
      environment: {
        ...good,
        Payments__Stripe__ApiBase: "http://0.0.0.0:4010",
      },
    }),
    /Payments__Stripe__ApiBase/,
  );
  await assert.rejects(
    main(["--callback=http://example.test:5080/callbacks/payments/stripe"], {
      environment: good,
    }),
    /--callback/,
  );
  for (const secret of [undefined, "", "secret", "whsec_with space"])
    await assert.rejects(
      main([], {
        environment: { ...good, Payments__Stripe__WebhookSecret: secret },
      }),
      /Payments__Stripe__WebhookSecret/,
    );
  await assert.rejects(
    main([], { environment: { ...good, Payments__ManifestPath: "" } }),
    /Payments__ManifestPath/,
  );
});

test("over HTTP on loopback it answers a request and its bodies are JSON", async () => {
  const received = [];
  const callback = createServer((request, response) => {
    let body = "";
    request.on("data", (chunk) => (body += chunk));
    request.on("end", () => {
      received.push({ body, signature: request.headers["stripe-signature"] });
      response.writeHead(204).end();
    });
  });
  await new Promise((done) => callback.listen(0, "127.0.0.1", done));
  const target = `http://127.0.0.1:${callback.address().port}/callbacks/payments/stripe`;
  const server = await serve(
    createStandIn({
      accounts: [ACCOUNT],
      webhookSecret: SECRET,
      deliver: async (body, signature) =>
        (
          await fetch(target, {
            method: "POST",
            body,
            headers: { "Stripe-Signature": signature },
          })
        ).status,
    }),
    { hostname: "127.0.0.1", port: 0 },
  );
  try {
    assert.equal(server.address().address, "127.0.0.1");
    const base = `http://127.0.0.1:${server.address().port}`;
    const headers = {
      Authorization: `Bearer ${STAND_IN_KEY}`,
      "Stripe-Version": API_VERSION,
      "Stripe-Account": ACCOUNT,
    };
    const operation = randomUUID();
    const made = await fetch(`${base}/v1/payment_intents`, {
      method: "POST",
      headers: {
        ...headers,
        "Idempotency-Key": operation,
        "Content-Type": "application/x-www-form-urlencoded",
      },
      body: charge(operation),
    });
    assert.equal(made.status, 200);
    assert.equal(made.headers.get("stripe-version"), API_VERSION);
    assert.equal((await made.json()).metadata.leasebook_operation, operation);
    const moved = await fetch(
      `${base}/stand-in/payments/${operation}/timeline`,
      {
        method: "POST",
        headers: { ...headers, "Content-Type": "application/json" },
        body: JSON.stringify({ timeline: "card-succeeds" }),
      },
    );
    assert.equal(moved.status, 200);
    assert.equal(received.length, 4);
    const now = Math.floor(Date.now() / 1000);
    for (const sent of received)
      assert.ok(verifySignature(sent.body, sent.signature, SECRET, now));
    const refused = await fetch(`${base}/v1/account`);
    assert.equal(refused.status, 401);
    assert.equal((await refused.json()).error.code, "stand_in_unknown_request");
  } finally {
    server.close();
    callback.close();
  }
});
