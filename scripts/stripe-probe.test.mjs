import assert from "node:assert/strict";
import { createHmac } from "node:crypto";
import {
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { test } from "node:test";
import { fileURLToPath } from "node:url";

import {
  FIXTURE_ROOT,
  SCENARIOS,
  assertTestKey,
  createScrubber,
  encodeForm,
  fixtureOffenders,
  jsonFiles,
  main,
  parseArgs,
  realLiterals,
  saysLive,
  scrubRun,
  timing,
  unsynthetic,
  verifySignature,
} from "./stripe-probe.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");

// Shaped like Stripe's identifiers, and not one of them: built here so that no real-looking
// identifier is committed in this file either.
const real = (prefix, suffix = "") =>
  `${prefix}_${"1Qa2Ws3Ed4Rf5Tg6Yh7Uj8Ik"}${suffix}`;

// No test waits. The clock still moves, so loops bounded by a deadline still end.
timing.sleep = async () => {};

test("only a test-mode secret key is accepted", () => {
  assertTestKey("sk_test_abc");
  assertTestKey("rk_test_abc");
  for (const key of [
    "sk_live_abc",
    "rk_live_abc",
    "pk_test_abc",
    "sk_test_",
    "",
    undefined,
    null,
  ]) {
    assert.throws(() => assertTestKey(key), /test-mode/);
  }
});

test("the probe refuses to start without a test key or a connected account", async () => {
  const run = (environment) =>
    main(["run", "--no-listen"], { environment, root: tmpdir() });
  await assert.rejects(
    run({ STRIPE_SANDBOX_SECRET_KEY: "sk_live_abc" }),
    /test-mode/,
  );
  await assert.rejects(
    run({
      STRIPE_SANDBOX_SECRET_KEY: "sk_test_abc",
      STRIPE_SANDBOX_CONNECTED_ACCOUNT: "acme",
    }),
    /connected account/,
  );
});

test("forms are encoded the way Stripe reads them", () => {
  assert.equal(
    decodeURIComponent(
      encodeForm({
        amount: 51524,
        confirm: true,
        payment_method_types: ["card"],
        metadata: { leasebook_operation: "op" },
        mandate_data: { customer_acceptance: { type: "offline" } },
        created: { gte: 10 },
        starting_after: undefined,
        skipped: null,
      }),
    ),
    "amount=51524&confirm=true&payment_method_types[]=card&metadata[leasebook_operation]=op" +
      "&mandate_data[customer_acceptance][type]=offline&created[gte]=10",
  );
  assert.equal(encodeForm(undefined), "");
  assert.equal(encodeForm({ query: "a b&c" }), "query=a%20b%26c");
});

test("a live-mode object is seen at any depth", () => {
  assert.equal(
    saysLive({ object: "list", data: [{ id: "x", livemode: true }] }),
    true,
  );
  assert.equal(saysLive({ data: { object: { livemode: true } } }), true);
  assert.equal(
    saysLive({ object: "list", data: [{ id: "x", livemode: false }] }),
    false,
  );
  assert.equal(saysLive({ livemode: "true" }), false);
  assert.equal(saysLive(null), false);
});

test("a webhook signature verifies only with the right secret, body and time", () => {
  const body = '{"id":"evt"}';
  const sign = (secret, timestamp, content = body) =>
    `t=${timestamp},v1=${createHmac("sha256", secret).update(`${timestamp}.${content}`).digest("hex")}`;
  assert.equal(
    verifySignature(body, sign("secret", 1000), "secret", 1100),
    true,
  );
  assert.equal(
    verifySignature(body, sign("other", 1000), "secret", 1100),
    false,
  );
  assert.equal(
    verifySignature(body, sign("secret", 1000, "{}"), "secret", 1100),
    false,
  );
  assert.equal(
    verifySignature(body, sign("secret", 1000), "secret", 1301),
    false,
  );
  assert.equal(verifySignature(body, "", "secret", 1000), false);
  assert.equal(verifySignature(body, "t=1000,v1=zz", "secret", 1000), false);
});

test("identifiers become synthetic, the same one each time, and everything else survives", () => {
  const { scrub } = createScrubber();
  const intent = real("pi");
  const scrubbed = scrub({
    id: intent,
    account: real("acct"),
    latest_charge: real("ch"),
    again: intent,
    status: "requires_payment_method",
    next_action: { type: "verify_with_microdeposits" },
    payment_method: "pm_usBankAccount_success",
    card: "pm_card_visa_chargeDeclined",
    amount: 51524,
    paid: true,
    nothing: null,
    metadata: {
      leasebook_operation: "6f1c2f0e-8a57-4c0f-9a51-3a2f3b6f0a11",
      theirs: "Acme",
    },
  });
  assert.deepEqual(scrubbed, {
    id: "pi_synthetic000001",
    account: "acct_synthetic000001",
    latest_charge: "ch_synthetic000001",
    again: "pi_synthetic000001",
    status: "requires_payment_method",
    next_action: { type: "verify_with_microdeposits" },
    payment_method: "pm_usBankAccount_success",
    card: "pm_card_visa_chargeDeclined",
    amount: 51524,
    paid: true,
    nothing: null,
    metadata: { leasebook_operation: "6f1c2f0e-8a57-4c0f-9a51-3a2f3b6f0a11" },
  });
  assert.deepEqual(unsynthetic(JSON.stringify(scrubbed)), []);
});

test("an identifier is caught wherever it sits and whatever its prefix", () => {
  const { scrub } = createScrubber();
  const scrubbed = scrub({
    joined: `${real("pi")}_secret_${"9Zx8Cv7Bn6Mq"}`,
    grouped: `group_${real("pi")}`,
    digits: real("v2evt"),
    upper: real("ACCT"),
    short: "ch_1Ab2Cd3E",
    token: "tok_AbCdEfGhIjKlMnOpQrStUvWx",
    echoed: "Invalid API Key provided: sk_test_****************abcd",
    secret: "whsec_Ab12/Cd34+Ef56==",
  });
  assert.deepEqual(scrubbed, {
    joined: "pi_synthetic000001_secret_synthetic000001",
    grouped: "group_pi_synthetic000001",
    digits: "v2evt_synthetic000001",
    upper: "acct_synthetic000001",
    short: "ch_synthetic000001",
    token: "tok_synthetic000001",
    echoed: "Invalid API Key provided: [scrubbed]",
    secret: "[scrubbed]",
  });
  assert.deepEqual(unsynthetic(JSON.stringify(scrubbed)), []);
});

test("what identifies a person, a business, a bank account or a device is removed", () => {
  const { scrub } = createScrubber();
  const scrubbed = scrub({
    email: "tenant@example.com",
    billing_details: { name: "A Tenant", address: { line1: "1 Main St" } },
    last4: "6789",
    routing_number: "110000000",
    client_secret: `${real("pi")}_secret_abc`,
    receipt_url: `https://pay.stripe.com/receipts/${real("ch")}`,
    evidence: { customer_name: "A Tenant", uncategorized_text: "free text" },
    name: null,
    message: `See https://stripe.com/docs/error or write to help@example.com from 203.0.113.9 about ${real("req")}.`,
    [real("ba")]: "an identifier used as a key",
  });
  assert.deepEqual(scrubbed, {
    email: "[scrubbed]",
    billing_details: "[scrubbed]",
    last4: "[scrubbed]",
    routing_number: "[scrubbed]",
    client_secret: "[scrubbed]",
    receipt_url: "[scrubbed]",
    evidence: "[scrubbed]",
    name: null,
    message:
      "See [scrubbed] or write to [scrubbed] from [scrubbed] about req_synthetic000001.",
    ba_synthetic000001: "an identifier used as a key",
  });
  assert.deepEqual(unsynthetic(JSON.stringify(scrubbed)), []);
});

const account = () => ({
  object: "account",
  id: real("acct"),
  email: "owner@example.com",
  country: "US",
  charges_enabled: true,
  payouts_enabled: true,
  capabilities: {
    card_payments: "active",
    us_bank_account_ach_payments: "active",
  },
  controller: { fees: { payer: "account" }, type: "account" },
  metadata: { internal: "Acme Rentals LLC" },
  settings: {
    dashboard: { display_name: "Acme Rentals", timezone: "America/New_York" },
    card_payments: { statement_descriptor_prefix: "ACMERENT" },
    payouts: {
      schedule: { interval: "daily", delay_days: 2 },
      statement_descriptor: "ACME",
    },
  },
});

test("an account is reduced to what the adapter reads from one", () => {
  const { scrub } = createScrubber();
  assert.deepEqual(
    scrub({ type: "account.updated", data: { object: account() } }),
    {
      type: "account.updated",
      data: {
        object: {
          object: "account",
          id: "acct_synthetic000001",
          charges_enabled: true,
          payouts_enabled: true,
          capabilities: {
            card_payments: "active",
            us_bank_account_ach_payments: "active",
          },
          controller: { fees: { payer: "account" } },
          settings: {
            payouts: { schedule: { interval: "daily", delay_days: 2 } },
          },
        },
      },
    },
  );
});

test("anything real that is left is named", () => {
  assert.deepEqual(unsynthetic(`{"id":"${real("pi")}"}`), [real("pi")]);
  assert.deepEqual(unsynthetic(`{"x":"group_${real("pi")}_secret_abc"}`), [
    real("pi"),
  ]);
  assert.deepEqual(unsynthetic('{"id":"pi_synthetic000001x"}'), [
    "pi_synthetic000001x",
  ]);
  assert.deepEqual(unsynthetic('{"a":"x@example.com"}'), ["an email address"]);
  assert.deepEqual(unsynthetic('{"a":"https://example.com/x"}'), ["a URL"]);
  assert.deepEqual(unsynthetic('{"a":"203.0.113.9"}'), ["an IP address"]);
  assert.deepEqual(unsynthetic('{"a":"sk_test_****abcd"}'), [
    "a key or signing secret",
  ]);
  assert.deepEqual(
    unsynthetic(
      '{"id":"pi_synthetic000001","type":"payment_intent.succeeded","m":"pm_usBankAccount_success"}',
    ),
    [],
  );
});

test("the second check works from the recorded values, not from a pattern", () => {
  const literals = realLiterals({
    body: account(),
    intent: {
      id: real("pi"),
      status: "requires_payment_method",
      payment_method: "pm_card_visa",
    },
    odd: { id: "odd-shaped.identifier" },
    metadata: { leasebook_operation: "6f1c2f0e-8a57-4c0f-9a51-3a2f3b6f0a11" },
  });
  assert.deepEqual(
    [...literals].sort(),
    [
      "ACME",
      "ACMERENT",
      "Acme Rentals",
      "America/New_York",
      "odd-shaped.identifier",
      "owner@example.com",
      real("acct"),
      real("pi"),
    ].sort(),
  );
});

test("a run is scrubbed into the fixture directory, or not written at all", () => {
  const root = mkdtempSync(join(tmpdir(), "stripe-probe-"));
  try {
    const run = join(root, "run-1");
    mkdirSync(run);
    writeFileSync(
      join(run, "001-card-create.json"),
      JSON.stringify({ body: { id: real("pi") } }),
    );
    writeFileSync(join(run, "findings.md"), `about ${real("pi")}`);
    const fixtures = join(root, "fixtures");
    const written = scrubRun(run, fixtures);
    assert.deepEqual(written, [
      join(fixtures, "run-1", "001-card-create.json"),
    ]);
    assert.deepEqual(JSON.parse(readFileSync(written[0], "utf8")), {
      body: { id: "pi_synthetic000001" },
    });

    // A business name the patterns cannot know, repeated where no field name marks it. The second
    // check knows it from the account it was recorded on, and the whole run is left unwritten.
    const second = join(root, "run-2");
    mkdirSync(second);
    writeFileSync(
      join(second, "001-account.json"),
      JSON.stringify({ body: account() }),
    );
    writeFileSync(
      join(second, "002-note.json"),
      JSON.stringify({ note: "Paid to Acme Rentals" }),
    );
    assert.throws(
      () => scrubRun(second, fixtures),
      /002-note\.json still holds 1 real value/,
    );
    assert.deepEqual(jsonFiles(join(fixtures, "run-2")), []);

    // So is an identifier of a shape the patterns do not recognise.
    const third = join(root, "run-3");
    mkdirSync(third);
    writeFileSync(
      join(third, "001-odd.json"),
      JSON.stringify({
        id: "odd-shaped.identifier",
        again: "see odd-shaped.identifier",
      }),
    );
    assert.throws(() => scrubRun(third, fixtures), /001-odd\.json still holds/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("arguments are read strictly", () => {
  const names = SCENARIOS.map((scenario) => scenario.name);
  assert.deepEqual(
    parseArgs(["run", "--wait-minutes=3", "--no-listen", "--port=5000"]),
    {
      command: "run",
      only: names,
      waitMinutes: 3,
      listen: false,
      port: 5000,
      directory: null,
    },
  );
  assert.deepEqual(parseArgs(["run", "--only=ach,ach-dispute"]).only, [
    "ach",
    "ach-dispute",
  ]);
  assert.equal(parseArgs(["scrub", "some/run"]).directory, "some/run");
  for (const argv of [
    [],
    ["charge"],
    ["scrub"],
    ["payouts"],
    ["run", "extra"],
    ["run", "--live"],
    ["run", "--only=wire"],
    ["run", "--only="],
    ["run", "--port=0"],
    ["run", "--port=99999"],
    ["run", "--wait-minutes=many"],
  ]) {
    assert.throws(() => parseArgs(argv), /Usage/);
  }
});

// The guard on what is committed. It reads every recorded payload in the repository.
test("no committed Stripe fixture holds a real identifier, an address or a URL", () => {
  assert.deepEqual(fixtureOffenders(join(repository, FIXTURE_ROOT)), []);
});

test("the guard on committed fixtures fails on one that holds something real", () => {
  const root = mkdtempSync(join(tmpdir(), "stripe-fixtures-"));
  try {
    assert.deepEqual(fixtureOffenders(root), []);
    mkdirSync(join(root, "run", "nested"), { recursive: true });
    writeFileSync(
      join(root, "run", "001-clean.json"),
      '{"id":"pi_synthetic000001"}',
    );
    assert.deepEqual(fixtureOffenders(root), []);
    const planted = join(root, "run", "nested", "002-real.json");
    writeFileSync(planted, JSON.stringify({ id: real("pi") }));
    assert.deepEqual(fixtureOffenders(root), [`${planted}: ${real("pi")}`]);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

// A stand-in for Stripe, so that the whole run is exercised before anyone spends a sandbox on it.
function fakeStripe({
  live = false,
  verifyFirst = false,
  dropFirst = 0,
  ownKey = false,
} = {}) {
  const intents = new Map();
  let dropped = 0;
  const calls = [];
  const json = (status, body) => new Response(JSON.stringify(body), { status });
  const keys = new Map();
  const stripe = async (url, { method, body, headers }) => {
    const { pathname, searchParams } = new URL(url);
    const form = new URLSearchParams(body ?? "");
    if (dropped < dropFirst) {
      dropped += 1;
      throw Object.assign(new TypeError("fetch failed"), {
        cause: { code: "ECONNRESET" },
      });
    }
    calls.push(`${method} ${pathname}`);
    // A connected account with its own dashboard has its own keys. Asked who it is, such a key
    // names the connected account itself.
    if (pathname === "/v1/account")
      return json(
        200,
        ownKey ? accountBody() : { ...accountBody(), id: real("acct", "P") },
      );
    if (pathname.startsWith("/v1/accounts/")) return json(200, accountBody());
    if (pathname === "/v1/payment_intents" && method === "POST") {
      if (form.get("payment_method") === "pm_card_visa_chargeDeclined") {
        return json(402, {
          error: {
            type: "card_error",
            code: "card_declined",
            message: "Declined.",
          },
        });
      }
      const key = headers["Idempotency-Key"];
      if (keys.has(key)) {
        const first = keys.get(key);
        return first.amount === form.get("amount")
          ? json(200, first.intent)
          : json(400, {
              error: { type: "idempotency_error", message: "Keys differ." },
            });
      }
      // What the sandbox answered on the first real run, 2026-10-08.
      if (form.has("payment_method_types[]")) {
        return json(400, {
          error: {
            type: "invalid_request_error",
            message:
              "The `payment_method_types` parameter is no longer supported.",
          },
        });
      }
      // Confirming without a return URL is allowed only when redirects are ruled out.
      assert.equal(form.get("automatic_payment_methods[enabled]"), "true");
      assert.equal(
        form.get("automatic_payment_methods[allow_redirects]"),
        "never",
      );
      const ach = form.get("payment_method").startsWith("pm_usBankAccount_");
      const intent = {
        id: real("pi", String(intents.size)),
        livemode: live,
        status: !ach
          ? "succeeded"
          : verifyFirst
            ? "requires_action"
            : "processing",
        next_action:
          ach && verifyFirst ? { type: "verify_with_microdeposits" } : null,
        latest_charge: real("ch", String(intents.size)),
        metadata: {
          leasebook_operation: form.get("metadata[leasebook_operation]"),
        },
        final:
          form.get("payment_method") === "pm_usBankAccount_insufficientFunds"
            ? "requires_payment_method"
            : "succeeded",
      };
      intents.set(intent.id, intent);
      keys.set(key, { amount: form.get("amount"), intent });
      return json(200, intent);
    }
    if (pathname === "/v1/payment_intents/search") {
      return json(400, {
        error: {
          type: "invalid_request_error",
          message: "Not on this account.",
        },
      });
    }
    if (pathname === "/v1/payment_intents") {
      return json(200, { data: [...intents.values()], has_more: false });
    }
    if (pathname.endsWith("/verify_microdeposits")) {
      assert.equal(form.get("descriptor_code"), "SM11AA");
      const intent = intents.get(pathname.split("/")[3]);
      Object.assign(intent, { status: "processing", next_action: null });
      return json(200, intent);
    }
    if (pathname.startsWith("/v1/payment_intents/")) {
      const intent = intents.get(pathname.split("/")[3]);
      intent.status = intent.final;
      return json(200, intent);
    }
    if (pathname.startsWith("/v1/charges/")) {
      return json(200, {
        status: "succeeded",
        receipt_url: `https://pay.stripe.com/receipts/${real("ch")}`,
        balance_transaction: {
          amount: 51524,
          fee: 1524,
          net: 50000,
          status: "pending",
          fee_details: [],
        },
      });
    }
    if (pathname === "/v1/disputes") {
      return json(200, {
        data: [
          {
            id: real("du"),
            reason: "bank_cannot_process",
            status: "lost",
            amount: 20000,
          },
        ],
      });
    }
    if (pathname === "/v1/balance")
      return json(200, { available: [], pending: [] });
    if (pathname === "/v1/payouts") {
      return json(200, {
        data: [
          { id: real("po"), automatic: true, amount: 50000, status: "paid" },
        ],
      });
    }
    if (pathname === "/v1/balance_transactions") {
      assert.equal(searchParams.get("payout"), real("po"));
      return json(200, {
        has_more: false,
        data: [
          { id: real("txn"), type: "charge", net: 50000 },
          { id: real("txn", "2"), type: "payout", net: -50000 },
        ],
      });
    }
    if (pathname === "/v1/events") {
      return json(200, {
        has_more: false,
        data: [
          {
            id: real("evt"),
            type: "payment_intent.succeeded",
            account: real("acct"),
            api_version: "v",
          },
        ],
      });
    }
    return json(404, {
      error: {
        type: "invalid_request_error",
        message: `No fake for ${pathname}`,
      },
    });
  };
  return Object.assign(stripe, { calls });
}

const accountBody = account;

async function probe(fetchStub, ...extra) {
  const root = mkdtempSync(join(tmpdir(), "stripe-probe-run-"));
  const original = { fetch: globalThis.fetch, log: console.log };
  const printed = [];
  globalThis.fetch = fetchStub;
  console.log = (line) => printed.push(String(line));
  const environment = {
    STRIPE_SANDBOX_SECRET_KEY: "sk_test_never-written-anywhere",
    STRIPE_SANDBOX_CONNECTED_ACCOUNT: real("acct"),
  };
  const result = { root, printed };
  try {
    result.outcome = await main(
      ["run", "--no-listen", "--wait-minutes=1", ...extra],
      {
        environment,
        root,
      },
    );
  } catch (error) {
    result.error = error;
  } finally {
    globalThis.fetch = original.fetch;
    console.log = original.log;
  }
  const runs = join(root, "stripe-probe.local");
  result.directory = join(runs, readdirSync(runs)[0]);
  result.findings = readFileSync(join(result.directory, "findings.md"), "utf8");
  return result;
}

function everything(directory) {
  return readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => readFileSync(join(entry.parentPath, entry.name), "utf8"))
    .join("\n");
}

function includesAll(findings, expected) {
  for (const line of expected) {
    assert.ok(
      findings.includes(line),
      `findings.md lacks: ${line}\n${findings}`,
    );
  }
}

test("a whole run answers every question, holds nothing real where it is read, and scrubs clean", async () => {
  const stripe = fakeStripe();
  const run = await probe(stripe);
  try {
    assert.equal(run.error, undefined);
    assert.equal(run.outcome, 0);
    includesAll(run.findings, [
      "fee payer=account",
      "card: how does a pm_card_visa payment progress?",
      "fee=1524, net=50000",
      "card-declined: is pm_card_visa_chargeDeclined accepted",
      "card_declined",
      "ach: how does a pm_usBankAccount_success payment progress?",
      "processing -> succeeded",
      "processing -> requires_payment_method",
      "Yes, the same payment intent",
      "HTTP 400, idempotency_error",
      "1 match(es) among 5 listed",
      "Does the Search API work on a connected account?",
      "A dispute after 0s: reason=bank_cannot_process",
      "1 automatic of 1 payout(s)",
      "their net sums to 50000 against a payout of 50000",
      "payment_intent.succeeded",
    ]);
    // The findings are pasted into an issue and the terminal may be too: neither holds anything real.
    assert.deepEqual(unsynthetic(run.findings), []);
    assert.deepEqual(
      unsynthetic(
        run.printed.filter((line) => line.startsWith("- ")).join("\n"),
      ),
      [],
    );
    assert.ok(!everything(run.root).includes("never-written-anywhere"));
    // Waiting for a payment is one kept answer, not one file for every time it was asked.
    const files = readdirSync(run.directory);
    assert.equal(files.filter((name) => name.includes("ach-final")).length, 1);
    assert.equal(files.filter((name) => name.includes("poll")).length, 0);
    // The raw run scrubs clean under both checks, the account's own names included.
    const written = scrubRun(run.directory, join(run.root, "fixtures"));
    assert.equal(written.length, jsonFiles(run.directory).length);
    const scrubbed = written
      .map((path) => readFileSync(path, "utf8"))
      .join("\n");
    for (const name of [
      "Acme",
      "ACMERENT",
      "owner@example.com",
      "America/New_York",
    ]) {
      assert.ok(!scrubbed.includes(name), `${name} survived scrubbing`);
    }
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});

test("a bank account that must be verified first is verified with the documented test code", async () => {
  const stripe = fakeStripe({ verifyFirst: true });
  const run = await probe(stripe, "--only=ach");
  try {
    assert.equal(run.error, undefined);
    includesAll(run.findings, [
      "ach: does pm_usBankAccount_success need its bank account verified first?",
      "Yes. The documented test code was accepted",
      "requires_action -> processing -> succeeded",
    ]);
    // --only made one charge and none of the others.
    assert.equal(
      stripe.calls.filter((call) => call === "POST /v1/payment_intents").length,
      1,
    );
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});

test("a dropped connection is retried, and a step that still fails is a finding, not the end", async () => {
  const retried = await probe(fakeStripe({ dropFirst: 3 }), "--only=card");
  try {
    assert.equal(retried.error, undefined);
    includesAll(retried.findings, [
      "fee payer=account",
      "card: how does a pm_card_visa payment",
    ]);
  } finally {
    rmSync(retried.root, { recursive: true, force: true });
  }

  // Stripe answers the two account calls, then nothing more: every later step fails by itself.
  const inner = fakeStripe();
  let answered = 0;
  const failing = async (url, options) => {
    answered += 1;
    if (answered > 2)
      throw Object.assign(new TypeError("fetch failed"), {
        cause: { code: "ETIMEDOUT" },
      });
    return inner(url, options);
  };
  const run = await probe(failing, "--only=card,ach");
  try {
    assert.equal(run.error, undefined);
    includesAll(run.findings, [
      "fee payer=account",
      "card: did the step finish?",
      "No: no answer from Stripe for POST /v1/payment_intents (ETIMEDOUT)",
      "ach: did the step finish?",
      "payouts: did the step finish?",
      "events: did the step finish?",
    ]);
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});

test("the first live-mode object stops the run and is not recorded", async () => {
  const run = await probe(fakeStripe({ live: true }));
  try {
    assert.match(run.error?.message ?? "", /live-mode object/);
    assert.ok(!everything(run.root).includes('"livemode": true'));
    // What was learned before it is still written.
    includesAll(run.findings, ["fee payer=account"]);
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});

test("a key that belongs to the connected account stops the run before any charge", async () => {
  const stripe = fakeStripe({ ownKey: true });
  const run = await probe(stripe);
  try {
    assert.match(
      run.error?.message ?? "",
      /key belongs to the connected account/,
    );
    assert.ok(!stripe.calls.includes("POST /v1/payment_intents"));
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});

test("a refused key stops the run without repeating what Stripe said about it", async () => {
  const refuse = async () =>
    new Response(
      JSON.stringify({
        error: {
          type: "invalid_request_error",
          message: "Invalid API Key provided: sk_test_****abcd",
        },
      }),
      { status: 401 },
    );
  const run = await probe(refuse);
  try {
    assert.match(run.error?.message ?? "", /Stripe refused the key \(HTTP 401/);
    assert.ok(!run.error.message.includes("abcd"));
  } finally {
    rmSync(run.root, { recursive: true, force: true });
  }
});
