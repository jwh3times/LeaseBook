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
  assertTestKey,
  createScrubber,
  encodeForm,
  jsonFiles,
  main,
  parseArgs,
  saysLive,
  scrubRun,
  unsynthetic,
  verifySignature,
} from "./stripe-probe.mjs";

const repository = resolve(dirname(fileURLToPath(import.meta.url)), "..");

// Shaped like Stripe's identifiers, and not one of them: built here so that no real-looking
// identifier is committed in this file either.
const real = (prefix) => `${prefix}_${"1Qa2Ws3Ed4Rf5Tg6Yh7Uj8Ik"}`;

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
  const account = real("acct");
  const scrubbed = scrub({
    id: intent,
    account,
    latest_charge: real("ch"),
    again: intent,
    status: "requires_payment_method",
    next_action: { type: "verify_with_microdeposits" },
    payment_method: "pm_usBankAccount_success",
    token: "tok_bypassPending",
    card: "pm_card_visa_chargeDeclined",
    amount: 51524,
    paid: true,
    nothing: null,
    metadata: { leasebook_operation: "6f1c2f0e-8a57-4c0f-9a51-3a2f3b6f0a11" },
  });
  assert.deepEqual(scrubbed, {
    id: "pi_synthetic000001",
    account: "acct_synthetic000001",
    latest_charge: "ch_synthetic000001",
    again: "pi_synthetic000001",
    status: "requires_payment_method",
    next_action: { type: "verify_with_microdeposits" },
    payment_method: "pm_usBankAccount_success",
    token: "tok_bypassPending",
    card: "pm_card_visa_chargeDeclined",
    amount: 51524,
    paid: true,
    nothing: null,
    metadata: { leasebook_operation: "6f1c2f0e-8a57-4c0f-9a51-3a2f3b6f0a11" },
  });
  assert.deepEqual(unsynthetic(JSON.stringify(scrubbed)), []);
});

test("what identifies a person, a bank account or a device is removed", () => {
  const { scrub } = createScrubber();
  const scrubbed = scrub({
    email: "tenant@example.com",
    billing_details: { name: "A Tenant", address: { line1: "1 Main St" } },
    last4: "6789",
    routing_number: "110000000",
    client_secret: `${real("pi")}_secret_abc`,
    receipt_url: `https://pay.stripe.com/receipts/${real("ch")}`,
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
    name: null,
    message:
      "See [scrubbed] or write to [scrubbed] from [scrubbed] about req_synthetic000001.",
    ba_synthetic000001: "an identifier used as a key",
  });
  assert.deepEqual(unsynthetic(JSON.stringify(scrubbed)), []);
});

test("anything real that is left is named", () => {
  assert.deepEqual(unsynthetic(`{"id":"${real("pi")}"}`), [real("pi")]);
  assert.deepEqual(unsynthetic('{"id":"pi_synthetic000001x"}'), [
    "pi_synthetic000001x",
  ]);
  assert.deepEqual(unsynthetic('{"a":"x@example.com"}'), ["an email address"]);
  assert.deepEqual(unsynthetic('{"a":"https://example.com/x"}'), ["a URL"]);
  assert.deepEqual(unsynthetic('{"a":"203.0.113.9"}'), ["an IP address"]);
  assert.deepEqual(
    unsynthetic(
      '{"id":"pi_synthetic000001","type":"payment_intent.succeeded"}',
    ),
    [],
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

    // The scrubber and the check share their patterns, so the check can only disagree if one of
    // them is wrong. When it does, the whole run is left unwritten, the clean files included.
    const second = join(root, "run-2");
    mkdirSync(second);
    writeFileSync(join(second, "001-a.json"), JSON.stringify({ ok: true }));
    writeFileSync(
      join(second, "002-b.json"),
      JSON.stringify({ note: "flagged" }),
    );
    const check = (text) =>
      text.includes("flagged") ? ["something real"] : [];
    assert.throws(
      () => scrubRun(second, fixtures, check),
      /002-b\.json still holds something real/,
    );
    assert.deepEqual(jsonFiles(join(fixtures, "run-2")), []);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("arguments are read strictly", () => {
  assert.deepEqual(
    parseArgs(["run", "--wait-minutes=3", "--no-listen", "--port=5000"]),
    {
      command: "run",
      waitMinutes: 3,
      listen: false,
      port: 5000,
      directory: null,
    },
  );
  assert.equal(parseArgs(["scrub", "some/run"]).directory, "some/run");
  for (const argv of [
    [],
    ["charge"],
    ["scrub"],
    ["payouts"],
    ["run", "extra"],
    ["run", "--live"],
  ]) {
    assert.throws(() => parseArgs(argv));
  }
});

// The guard on what is committed. It reads every recorded payload in the repository.
test("no committed Stripe fixture holds a real identifier, an address or a URL", () => {
  const offenders = jsonFiles(join(repository, FIXTURE_ROOT)).flatMap((file) =>
    unsynthetic(readFileSync(file, "utf8")).map((found) => `${file}: ${found}`),
  );
  assert.deepEqual(offenders, []);
});

// A stand-in for Stripe, so that the whole run is exercised before anyone spends a sandbox on it.
function fakeStripe({ live = false } = {}) {
  let intents = 0;
  const json = (status, body) => new Response(JSON.stringify(body), { status });
  return async (url, { method, body }) => {
    const { pathname, searchParams } = new URL(url);
    const form = new URLSearchParams(body ?? "");
    if (pathname === "/v1/account") return json(200, { id: real("acct") });
    if (pathname.startsWith("/v1/accounts/")) {
      return json(200, {
        charges_enabled: true,
        payouts_enabled: true,
        email: "owner@example.com",
        capabilities: {
          card_payments: "active",
          us_bank_account_ach_payments: "active",
        },
        controller: { fees: { payer: "account" } },
        settings: { payouts: { schedule: { interval: "daily" } } },
      });
    }
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
      intents += 1;
      return json(200, {
        id: `${real("pi")}${intents}`,
        livemode: live,
        status:
          form.get("payment_method_types[]") === "card"
            ? "succeeded"
            : "processing",
        latest_charge: `${real("ch")}${intents}`,
        metadata: {
          leasebook_operation: form.get("metadata[leasebook_operation]"),
        },
      });
    }
    if (pathname === "/v1/payment_intents/search") {
      return json(400, {
        error: {
          type: "invalid_request_error",
          message: "Not on this account.",
        },
      });
    }
    if (pathname === "/v1/payment_intents")
      return json(200, { data: [], has_more: false });
    if (pathname.startsWith("/v1/charges/")) {
      return json(200, {
        status: "succeeded",
        balance_transaction: {
          amount: 51524,
          fee: 1524,
          net: 50000,
          status: "pending",
          fee_details: [],
        },
      });
    }
    if (pathname === "/v1/disputes") return json(200, { data: [] });
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
          { id: `${real("txn")}2`, type: "payout", net: -50000 },
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
}

async function probe(fetchStub) {
  const root = mkdtempSync(join(tmpdir(), "stripe-probe-run-"));
  const original = { fetch: globalThis.fetch, log: console.log };
  globalThis.fetch = fetchStub;
  console.log = () => {};
  const environment = {
    STRIPE_SANDBOX_SECRET_KEY: "sk_test_never-written-anywhere",
    STRIPE_SANDBOX_CONNECTED_ACCOUNT: real("acct"),
  };
  try {
    return {
      root,
      outcome: await main(["run", "--no-listen", "--wait-minutes=0"], {
        environment,
        root,
      }),
    };
  } catch (error) {
    return { root, error };
  } finally {
    globalThis.fetch = original.fetch;
    console.log = original.log;
  }
}

function everything(directory) {
  return readdirSync(directory, { recursive: true, withFileTypes: true })
    .filter((entry) => entry.isFile())
    .map((entry) => readFileSync(join(entry.parentPath, entry.name), "utf8"))
    .join("\n");
}

test("a whole run records every answer and never writes the key", async () => {
  const { root, outcome, error } = await probe(fakeStripe());
  try {
    assert.equal(error, undefined);
    assert.equal(outcome, 0);
    const [run] = readdirSync(join(root, "stripe-probe.local"));
    const directory = join(root, "stripe-probe.local", run);
    const findings = readFileSync(join(directory, "findings.md"), "utf8");
    for (const expected of [
      "fee payer=account",
      "card: how does a pm_card_visa payment progress?",
      "fee=1524, net=50000",
      "card-declined: is pm_card_visa_chargeDeclined accepted",
      "card_declined",
      "ach: how does a pm_usBankAccount_success payment progress?",
      "Does the same idempotency key with the same request return the first result?",
      "Does the Search API work on a connected account?",
      "No dispute within 0 minute(s)",
      "1 automatic of 1 payout(s)",
      "their net sums to 50000 against a payout of 50000",
      "payment_intent.succeeded",
    ]) {
      assert.ok(
        findings.includes(expected),
        `findings.md lacks: ${expected}\n${findings}`,
      );
    }
    // The findings are meant to be pasted into an issue, so they hold nothing real.
    assert.deepEqual(unsynthetic(findings), []);
    assert.ok(!everything(root).includes("never-written-anywhere"));
    // The raw run scrubs clean, the connected account's email included.
    const written = scrubRun(directory, join(root, "fixtures"));
    assert.ok(written.length > 15);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("the first live-mode object stops the run", async () => {
  const { root, error } = await probe(fakeStripe({ live: true }));
  try {
    assert.match(error?.message ?? "", /live-mode object/);
    const text = everything(root);
    assert.ok(
      !text.includes('"livemode": true'),
      "the live object itself must not be recorded",
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
