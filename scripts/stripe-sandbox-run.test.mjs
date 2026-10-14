import assert from "node:assert/strict";
import { describe, it } from "node:test";

import {
  FIXTURE_DATABASE,
  HOST_URL,
  Interrupted,
  Refusal,
  STAND_IN_DATABASE,
  assertRunDatabase,
  assertTestKey,
  chargeMayExist,
  childEnvironment,
  commentSql,
  connectionString,
  copySql,
  createMask,
  discard,
  dropSql,
  hostEnvironment,
  main,
  oneCliAtATime,
  quietRefusal,
  readFixture,
  realDependencies,
  resume,
  resumeDecision,
  runName,
  start,
  unpaidBalance,
} from "./stripe-sandbox-run.mjs";

// Shaped like Stripe's keys and identifiers, and none of them: built here so that nothing
// key-shaped is committed in this file.
const shaped = (...parts) => parts.join("_");
const KEY = shaped("sk", "test", "4eC39HqLyjWDarjtT1zdp7dc");
const LIVE_KEY = shaped("sk", "live", "4eC39HqLyjWDarjtT1zdp7dc");
const WHSEC = shaped("whsec", "8f1a2b3c4d5e6f708192a3b4c5d6e7f8");
const ACCOUNT = shaped("acct", "1Qa2Ws3Ed4Rf5Tg6");
const RUN = "leasebook_sandbox_run_ab12cd34";
const PAYMENT_A = "0199e0f2-7a10-7c4e-9a1b-111111111111";
const PAYMENT_B = "0199e0f2-7a10-7c4e-9a1b-222222222222";
const MANIFEST = JSON.stringify({
  Payments: {
    Mode: "StripeSandbox",
    SigningKey: "never printed",
    Fixtures: [
      {
        OrgId: "0199e0f2-0000-7000-8000-000000000001",
        Account: ACCOUNT,
        Generation: "0199e0f2-0000-7000-8000-000000000002",
        BankId: "0199e0f2-0000-7000-8000-000000000003",
      },
    ],
  },
});
const PAID =
  "Paid on 2026-10-14 — on its way to the bank, not yet on your ledger";
const charged = {
  submitting: true,
  payments: [
    { method: "ach", id: PAYMENT_A, state: PAID },
    { method: "card", id: PAYMENT_B, state: PAID },
  ],
};
const quiet = {
  livemode: false,
  available: [{ amount: 0, currency: "usd" }],
  pending: [{ amount: 0, currency: "usd" }],
};

/**
 * Fake dependencies for a whole run. Every operation is written to `log` in the order it happened,
 * which is what the cleanup and sequencing tests read.
 */
function fakes(options = {}) {
  const {
    env = { STRIPE_SANDBOX_SECRET_KEY: KEY },
    interactive = false,
    balance = quiet,
    playwrightCode = 0,
    invariantsCode = 0,
    sweeps = { completed: 1, failed: 0 },
    record = {
      source: "stripe",
      startedAt: "2026-10-14T10:00:00.000Z",
      state: "charged",
      payments: [
        { method: "ach", id: PAYMENT_A },
        { method: "card", id: PAYMENT_B },
      ],
    },
    hostRunning = false,
    connected = 0,
    onPlaywright = () => {},
    cli,
  } = options;
  // A step that wrote nothing at all is `result: undefined`, which a default would hide.
  const result = Object.hasOwn(options, "result") ? options.result : charged;
  const log = [];
  const printed = [];
  const seen = {};
  let clock = 0;
  let listening = hostRunning;
  const deps = {
    env,
    signal: undefined,
    isInteractive: interactive,
    prompt: async () => {
      log.push("prompt");
      return `${KEY}\n`;
    },
    print: (line) => printed.push(line),
    now: () => clock,
    sleep: async (ms) => {
      clock += ms;
    },
    randomBytes: () => Buffer.from("ab12cd34", "hex"),
    readFile: () => MANIFEST,
    tempDir: () => "/scratch/result",
    removeDir: () => log.push("remove result"),
    readResult: () => result,
    connection: (database, role) => connectionString(database, role),
    answers: async () => (listening ? 200 : 0),
    build: async () => log.push("build"),
    copy: async (source, target) => log.push(`copy ${source} ${target}`),
    comment: async (target, saved) => {
      seen.comment = saved;
      log.push(
        saved.lastResume
          ? `comment resumed ${saved.lastResume.outcome}`
          : `comment ${saved.state}`,
      );
    },
    readComment: async () => JSON.stringify(record),
    exists: async () => true,
    connections: async () => connected,
    drop: async (target) => log.push(`drop ${target}`),
    listRuns: async () => [],
    migrate: async (target) => log.push(`migrate ${target}`),
    startHost: (hostEnv) => {
      seen.hostEnv = hostEnv;
      log.push("host start");
      listening = true;
      return {
        exitCode: () => null,
        sweeps: () => sweeps,
        stop: async () => {
          listening = false;
          log.push("host stop");
        },
      };
    },
    playwright: (playwrightEnv) => {
      seen.playwrightEnv = playwrightEnv;
      log.push(`playwright ${playwrightEnv.STRIPE_SANDBOX_E2E_STEP}`);
      const exited = onPlaywright(log) ?? Promise.resolve(playwrightCode);
      return {
        exited,
        stop: async () => log.push("playwright stop"),
      };
    },
    invariants: async () => {
      log.push("invariants");
      return { code: invariantsCode };
    },
    balance: async () => {
      log.push("balance");
      return balance;
    },
    cli:
      cli ??
      (() => ({
        printSecret: async () => {
          log.push("print-secret start");
          await new Promise((done) => setTimeout(done, 5));
          log.push("print-secret end");
          return WHSEC;
        },
        listen: async () => {
          log.push("listen start");
          return { stop: async () => log.push("listen stop") };
        },
      })),
  };
  return { deps, log, printed, seen };
}

const after = (log, later, earlier) =>
  assert.ok(
    log.indexOf(later) > log.indexOf(earlier) && log.indexOf(earlier) >= 0,
    `${later} after ${earlier}: ${log.join(", ")}`,
  );

describe("refusals before anything runs", () => {
  it("refuses in CI, before the key is even looked at", async () => {
    const { deps, log } = fakes({
      env: { CI: "true", STRIPE_SANDBOX_SECRET_KEY: KEY },
    });
    await assert.rejects(start(deps), /never runs in CI/);
    assert.deepEqual(log, []);
  });

  it("refuses a live key, and anything that is not a test-mode key", async () => {
    for (const key of [LIVE_KEY, shaped("rk", "live", "abc12345")]) {
      const { deps, log } = fakes({ env: { STRIPE_SANDBOX_SECRET_KEY: key } });
      await assert.rejects(start(deps), /live Stripe key/);
      assert.deepEqual(log, []);
    }
    for (const key of [
      shaped("pk", "test", "abc12345"),
      `${KEY} `,
      "sk_test_",
    ]) {
      assert.throws(() => assertTestKey(key), /test-mode secret key/);
    }
    assert.equal(
      assertTestKey(shaped("rk", "test", "abc12345")),
      shaped("rk", "test", "abc12345"),
    );
  });

  it("refuses a missing key with no terminal to ask in", async () => {
    const { deps, log } = fakes({ env: {} });
    await assert.rejects(start(deps), /not set and there is no terminal/);
    assert.deepEqual(log, []);
  });

  it("asks for a missing key, masked, when there is a terminal", async () => {
    const { deps, log, printed } = fakes({ env: {}, interactive: true });
    assert.equal(await start(deps), 0);
    assert.equal(log[0], "prompt");
    assert.ok(printed.every((line) => !line.includes(KEY)));
  });

  it("refuses a test source it does not know", async () => {
    const { deps } = fakes({
      env: { STRIPE_SANDBOX_RUN_TEST_SOURCE: "stripe" },
    });
    await assert.rejects(start(deps), /takes only "standin"/);
  });

  it("answers an unknown command with the usage", async () => {
    const { deps } = fakes();
    await assert.rejects(main(["go"], deps), /Usage:/);
    await assert.rejects(main(["start", "now"], deps), /Usage:/);
    await assert.rejects(main(["resume"], deps), /Usage:/);
  });
});

describe("the fixture is only ever a copy's source", () => {
  it("refuses every write, migration, connection and drop that names a fixture", () => {
    for (const name of [
      FIXTURE_DATABASE,
      STAND_IN_DATABASE,
      "leasebook_payment_fixture",
    ]) {
      assert.throws(
        () => assertRunDatabase(name),
        /only ever the source of a copy/,
      );
      assert.throws(() => dropSql(name), Refusal);
      assert.throws(() => commentSql(name, {}), Refusal);
      assert.throws(() => connectionString(name, "leasebook_app"), Refusal);
      assert.throws(
        () => connectionString(name, "leasebook_migrator"),
        Refusal,
      );
      assert.throws(() => copySql(FIXTURE_DATABASE, name), Refusal);
    }
    for (const name of [
      "leasebook",
      "leasebook_sandbox_run_AB12CD34",
      "leasebook_sandbox_run_ab12",
      `${RUN};`,
    ]) {
      assert.throws(() => assertRunDatabase(name), /not a sandbox run's copy/);
    }
  });

  it("copies only from a fixture, with the template, into a run's name", () => {
    assert.equal(
      copySql(FIXTURE_DATABASE, RUN),
      `CREATE DATABASE ${RUN} TEMPLATE ${FIXTURE_DATABASE} OWNER leasebook_migrator;`,
    );
    assert.throws(() => copySql("leasebook", RUN), /not a payment fixture/);
    assert.equal(dropSql(RUN), `DROP DATABASE ${RUN};`);
    assert.equal(runName("ab12cd34"), RUN);
    assert.equal(runName(RUN), RUN);
  });

  it("escapes a comment and names only the copy", () => {
    assert.equal(
      commentSql(RUN, { note: "it's" }),
      `COMMENT ON DATABASE ${RUN} IS '{"note":"it''s"}';`,
    );
  });

  it("copies the fixture and connects the host and the migration to the copy alone", async () => {
    const { deps, log, seen } = fakes();
    await start(deps);
    assert.equal(log[1], `copy ${FIXTURE_DATABASE} ${RUN}`);
    assert.ok(log.includes(`migrate ${RUN}`));
    assert.match(
      seen.hostEnv.ConnectionStrings__Default,
      new RegExp(`Database=${RUN};`),
    );
    assert.ok(
      !log.some(
        (entry) =>
          entry.includes(FIXTURE_DATABASE) && !entry.startsWith("copy"),
      ),
    );
  });
});

describe("masking", () => {
  const mask = createMask([KEY, WHSEC, ACCOUNT]);

  it("masks keys, signing secrets, accounts and persons whole", () => {
    const said = mask(
      `Ready! Your webhook signing secret is ${WHSEC} (^C to quit) key ${KEY} on ${ACCOUNT} by ${shaped("person", "1Abc2Def3Ghi")}`,
    );
    assert.equal(
      said,
      "Ready! Your webhook signing secret is whsec_… (^C to quit) key sk_test_… on acct_… by person_…",
    );
  });

  it("masks a key or a secret it was not told about, by its shape", () => {
    const blind = createMask();
    assert.equal(blind(`a ${KEY}, b ${LIVE_KEY}`), "a sk_test_…, b sk_live_…");
    assert.equal(blind(`"${WHSEC}"`), '"whsec_…"');
    assert.equal(blind(shaped("rk", "test", "x1y2z3")), "rk_test_…");
  });

  it("shows at most the last four characters of a Stripe object id", () => {
    const blind = createMask();
    assert.equal(
      blind(
        "pi_3Qa2Ws3Ed4Rf5Tg6 ch_3Qa2Ws3Ed4Rf5Tg6 po_1Qa2Ws3Ed4Rf9Zz9 [evt_1Abc2Def3Gh4]",
      ),
      "pi_…5Tg6 ch_…5Tg6 po_…9Zz9 [evt_…3Gh4]",
    );
  });

  it("leaves Stripe's enumeration values, test payment methods and LeaseBook's ids alone", () => {
    const blind = createMask();
    const text = `in_progress pm_card_visa pm_usBankAccount_success card_payments ${PAYMENT_A}`;
    assert.equal(blind(text), text);
  });

  it("masks every line a run says, the listener's included", async () => {
    const { deps, printed } = fakes({
      cli: ({ say }) => ({
        printSecret: async () => WHSEC,
        listen: async () => {
          say(`stripe | Ready! Your webhook signing secret is ${WHSEC}`);
          say(`stripe | --> on ${ACCOUNT} with ${KEY}`);
          return { stop: async () => {} };
        },
      }),
    });
    await start(deps);
    const all = printed.join("\n");
    assert.match(
      all,
      /stripe \| Ready! Your webhook signing secret is whsec_…/,
    );
    assert.match(all, /stripe \| --> on acct_… with sk_test_…/);
    for (const secret of [KEY, WHSEC, ACCOUNT])
      assert.ok(!all.includes(secret), secret);
  });

  it("masks a child's output with the run's secrets once it has them", () => {
    const printed = [];
    const deps = realDependencies({
      env: {},
      print: (line) => printed.push(line),
    });
    deps.print(`host | key ${KEY}`);
    deps.remember("a-secret-without-a-shape");
    deps.print("host | a-secret-without-a-shape");
    assert.deepEqual(printed, ["host | key sk_test_…", "host | [secret]"]);
  });
});

describe("process environments", () => {
  const env = {
    PATH: "/bin",
    STRIPE_SANDBOX_SECRET_KEY: KEY,
    Payments__Stripe__ApiBase: "http://127.0.0.1:4010",
    Payments__Stripe__SecretKey: KEY,
    ConnectionStrings__Default: "Database=leasebook_payment_fixture_stripe",
    STRIPE_STAND_IN_E2E: "1",
    E2E_COVERAGE: "1",
    STRIPE_SANDBOX_RUN_TEST_WEBHOOK_SECRET: WHSEC,
    no_proxy: "example.test",
  };

  it("gives no other child a Stripe secret, a connection string or a pass switch", () => {
    assert.deepEqual(childEnvironment(env), {
      PATH: "/bin",
      no_proxy: "example.test",
    });
  });

  it("gives the host the key and the signing secret, no stand-in address, and loopback past a proxy", () => {
    const host = hostEnvironment(env, {
      connection: connectionString(RUN, "leasebook_app"),
      manifest: "/repo/payment-fixture-stripe.local",
      key: KEY,
      webhookSecret: WHSEC,
    });
    assert.equal(host.Payments__Stripe__SecretKey, KEY);
    assert.equal(host.Payments__Stripe__WebhookSecret, WHSEC);
    assert.equal(host.Payments__Stripe__ApiBase, undefined);
    assert.equal(host.ASPNETCORE_ENVIRONMENT, "Development");
    assert.equal(host.no_proxy, "example.test,127.0.0.1,localhost");
    assert.equal(host.NO_PROXY, "127.0.0.1,localhost");
  });

  it("keeps the key out of Playwright's environment", async () => {
    const { deps, seen } = fakes();
    await start(deps);
    assert.equal(seen.playwrightEnv.STRIPE_SANDBOX_SECRET_KEY, undefined);
    assert.equal(seen.playwrightEnv.STRIPE_SANDBOX_E2E, "1");
    assert.equal(seen.playwrightEnv.STRIPE_SANDBOX_E2E_HOST, HOST_URL);
    assert.ok(!Object.values(seen.playwrightEnv).includes(KEY));
    assert.ok(!Object.values(seen.playwrightEnv).includes(WHSEC));
  });
});

describe("the manifest", () => {
  it("reads only the first fixture's account and organization, and the count", () => {
    assert.deepEqual(readFixture(MANIFEST), {
      account: ACCOUNT,
      orgId: "0199e0f2-0000-7000-8000-000000000001",
      count: 1,
    });
  });

  it("refuses one that is not a sandbox manifest, without repeating it", () => {
    assert.throws(
      () => readFixture('{"Payments":{"Mode":"Simulation","Fixtures":[]}}'),
      /not a Stripe sandbox/,
    );
    assert.throws(
      () => readFixture('{"SigningKey":"abc'),
      (error) => error.message === "The fixture manifest is not JSON.",
    );
  });
});

describe("the quiet check", () => {
  it("finds every balance Stripe has not paid out", () => {
    assert.deepEqual(unpaidBalance(quiet), []);
    assert.deepEqual(
      unpaidBalance({
        available: [{ amount: 0, currency: "usd" }],
        pending: [{ amount: 100403, currency: "usd" }],
      }),
      [{ kind: "pending", amount: 100403, currency: "usd" }],
    );
    assert.throws(
      () => unpaidBalance({ ...quiet, livemode: true }),
      /live mode/,
    );
    assert.match(
      quietRefusal([{ kind: "available", amount: -1500, currency: "usd" }]),
      /holds -15\.00 USD available that Stripe has not yet paid out\. A payout is forming/,
    );
  });

  it("refuses to charge, drops the copy and stops the listener before the host", async () => {
    const { deps, log } = fakes({
      balance: { ...quiet, pending: [{ amount: 51524, currency: "usd" }] },
    });
    await assert.rejects(
      start(deps),
      /515\.24 USD pending.*Nothing was charged/s,
    );
    assert.ok(!log.some((entry) => entry.startsWith("playwright")));
    assert.deepEqual(log.slice(-3), [
      "listen stop",
      "host stop",
      `drop ${RUN}`,
    ]);
  });
});

describe("one Stripe CLI process at a time", () => {
  it("lets print-secret exit before listen starts", async () => {
    const { deps, log } = fakes();
    await start(deps);
    after(log, "print-secret end", "print-secret start");
    after(log, "host start", "print-secret end");
    after(log, "listen start", "host start");
  });

  it("refuses a second process while the first runs", async () => {
    let finish;
    const cli = oneCliAtATime({
      printSecret: () => new Promise((done) => (finish = done)),
      listen: async () => ({ stop: async () => {} }),
    });
    const secret = cli.printSecret({});
    await assert.rejects(
      cli.listen({}),
      /while --print-secret is still running/,
    );
    finish(WHSEC);
    assert.equal(await secret, WHSEC);
    const listener = await cli.listen({});
    await assert.rejects(cli.printSecret({}), /while listen is still running/);
    await listener.stop();
    const again = cli.printSecret({});
    finish(WHSEC);
    assert.equal(await again, WHSEC);
  });

  it("frees the CLI when a step fails", async () => {
    const cli = oneCliAtATime({
      printSecret: async () => {
        throw new Error("signed out");
      },
      listen: async () => ({ stop: async () => {} }),
    });
    await assert.rejects(cli.printSecret({}), /signed out/);
    await cli.listen({});
  });
});

describe("start", () => {
  it("runs every step in order, stops the servers before the invariants and keeps the copy", async () => {
    const { deps, log, printed, seen } = fakes();
    assert.equal(await start(deps), 0);
    assert.deepEqual(log, [
      "build",
      `copy ${FIXTURE_DATABASE} ${RUN}`,
      "comment starting",
      `migrate ${RUN}`,
      "print-secret start",
      "print-secret end",
      "host start",
      "listen start",
      "balance",
      "playwright start",
      "remove result",
      "comment charged",
      "listen stop",
      "host stop",
      "invariants",
    ]);
    assert.deepEqual(seen.comment.payments, [
      { method: "ach", id: PAYMENT_A },
      { method: "card", id: PAYMENT_B },
    ]);
    const summary = printed.join("\n");
    assert.match(summary, new RegExp(`Stripe sandbox run ${RUN}`));
    assert.match(
      summary,
      /ACH {2}\$500\.00 {2}payment 0199e0f2-7a10-7c4e-9a1b-111111111111 {2}Paid on 2026-10-14/,
    );
    assert.match(summary, /check-invariants --all: all clean/);
    assert.match(summary, new RegExp(`resume ${RUN}`));
  });

  it("waits for one sweep of each fixture, and fails on a sweep that failed", async () => {
    const { deps, log } = fakes({ sweeps: { completed: 0, failed: 1 } });
    await assert.rejects(start(deps), /first sweep failed \(event 4607\)/);
    assert.deepEqual(log.slice(-3), [
      "listen stop",
      "host stop",
      `drop ${RUN}`,
    ]);
  });

  it("drops the copy when Playwright fails before a charge was submitted", async () => {
    const { deps, log, printed } = fakes({
      playwrightCode: 1,
      result: { submitting: false, payments: [] },
    });
    await assert.rejects(start(deps), /did not see both payments paid/);
    assert.deepEqual(log.slice(-3), [
      "listen stop",
      "host stop",
      `drop ${RUN}`,
    ]);
    assert.match(printed.join("\n"), /no charge was submitted/);
  });

  it("drops the copy when Playwright wrote nothing at all", async () => {
    const { deps, log } = fakes({ playwrightCode: 1, result: undefined });
    await assert.rejects(start(deps), Refusal);
    assert.equal(log.at(-1), `drop ${RUN}`);
  });

  it("keeps the copy, and says why, when a charge may have been submitted", async () => {
    for (const result of [
      { submitting: true, payments: [] },
      null,
      { ...charged, payments: [charged.payments[0]] },
    ]) {
      const { deps, log, printed } = fakes({ playwrightCode: 1, result });
      await assert.rejects(start(deps), Refusal);
      assert.ok(!log.some((entry) => entry.startsWith("drop")), log.join(", "));
      assert.deepEqual(log.slice(-3), [
        "listen stop",
        "host stop",
        "comment charge may exist",
      ]);
      assert.match(
        printed.join("\n"),
        /a test charge may have been submitted, and only this copy knows it/,
      );
    }
  });

  it("keeps the copy when the payments did not both end paid", async () => {
    const failed = {
      ...charged,
      payments: [
        charged.payments[0],
        {
          ...charged.payments[1],
          state: "Test payment failed — no payment recorded",
        },
      ],
    };
    const { deps, log } = fakes({ result: failed });
    await assert.rejects(start(deps), /did not see both payments paid/);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
  });

  it("stops Playwright first, then the listener and the host, on Ctrl-C during the charge", async () => {
    const controller = new AbortController();
    const { deps, log } = fakes({
      result: { submitting: true, payments: [] },
      onPlaywright: () => {
        setTimeout(() => controller.abort(), 5);
        return new Promise(() => {});
      },
    });
    deps.signal = controller.signal;
    await assert.rejects(start(deps), Interrupted);
    assert.deepEqual(log.slice(-5), [
      "playwright stop",
      "remove result",
      "listen stop",
      "host stop",
      "comment charge may exist",
    ]);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
  });

  it("drops the copy on Ctrl-C before any charge", async () => {
    const controller = new AbortController();
    const { deps, log } = fakes();
    deps.signal = controller.signal;
    deps.balance = async () => {
      controller.abort();
      return new Promise(() => {});
    };
    await assert.rejects(start(deps), Interrupted);
    assert.deepEqual(log.slice(-3), [
      "listen stop",
      "host stop",
      `drop ${RUN}`,
    ]);
  });

  it("drops the copy when the host does not start, and starts no listener", async () => {
    const { deps, log } = fakes();
    deps.startHost = () => {
      log.push("host start");
      return {
        exitCode: () => 1,
        sweeps: () => ({ completed: 0, failed: 0 }),
        stop: async () => log.push("host stop"),
      };
    };
    await assert.rejects(start(deps), /exited with code 1 before it was ready/);
    assert.ok(!log.includes("listen start"));
    assert.deepEqual(log.slice(-2), ["host stop", `drop ${RUN}`]);
  });

  it("refuses a port something already answers on, before starting the host", async () => {
    const { deps, log } = fakes({ hostRunning: true });
    await assert.rejects(start(deps), /Something already answers/);
    assert.ok(!log.includes("host start"));
    assert.equal(log.at(-1), `drop ${RUN}`);
  });

  it("reports a failed invariant check and keeps the copy", async () => {
    const { deps, log, printed } = fakes({ invariantsCode: 1 });
    assert.equal(await start(deps), 1);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
    assert.match(printed.join("\n"), /check-invariants --all: FAILED/);
  });
});

describe("chargeMayExist", () => {
  it("reads the start step's record conservatively", () => {
    assert.equal(chargeMayExist(undefined), false);
    assert.equal(chargeMayExist({ submitting: false, payments: [] }), false);
    assert.equal(chargeMayExist(null), true);
    assert.equal(chargeMayExist({ submitting: true, payments: [] }), true);
  });
});

describe("resume", () => {
  const outcomes = {
    none: { outcome: "none", others: 0 },
    review: {
      outcome: "review",
      payout: "po_…9Zz9",
      reason: "unsupported_settlement",
      explanation: "This payout contains something LeaseBook does not post.",
    },
    held: { outcome: "held", payout: "po_…9Zz9", amount: "1000.00", lines: 2 },
  };

  it("migrates the copy again, then print-secret, host and listener in order", async () => {
    const { deps, log, seen } = fakes({ result: outcomes.none });
    assert.equal(await resume([RUN], deps), 0);
    assert.deepEqual(log.slice(0, 6), [
      "build",
      `migrate ${RUN}`,
      "print-secret start",
      "print-secret end",
      "host start",
      "listen start",
    ]);
    assert.equal(seen.playwrightEnv.STRIPE_SANDBOX_E2E_STEP, "resume");
    assert.equal(
      seen.playwrightEnv.STRIPE_SANDBOX_E2E_PAYMENTS,
      `${PAYMENT_A},${PAYMENT_B}`,
    );
    assert.equal(
      seen.playwrightEnv.STRIPE_SANDBOX_E2E_SINCE,
      "2026-10-14T10:00:00.000Z",
    );
    assert.ok(!log.includes("balance"));
  });

  it("with no payout yet, says so, stops everything and keeps the copy", async () => {
    const { deps, log, printed } = fakes({ result: outcomes.none });
    assert.equal(await resume(["ab12cd34"], deps), 0);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
    assert.ok(!log.includes("invariants"));
    after(log, "host stop", "listen stop");
    assert.match(
      printed.join("\n"),
      /has not paid out this run's payments yet/,
    );
    assert.match(printed.join("\n"), new RegExp(`Kept ${RUN}\\.`));
  });

  it("with a payout in review, reports the reason, runs the invariants and drops the copy", async () => {
    const { deps, log, printed } = fakes({ result: outcomes.review });
    assert.equal(await resume([RUN], deps), 1);
    assert.deepEqual(log.slice(-5), [
      "listen stop",
      "host stop",
      "invariants",
      "comment resumed review",
      `drop ${RUN}`,
    ]);
    assert.match(
      printed.join("\n"),
      /Payout po_…9Zz9 is in review: unsupported_settlement\./,
    );
    assert.match(
      printed.join("\n"),
      /The screen says: This payout contains something/,
    );
  });

  it("with a held payout, runs the invariants and drops the copy", async () => {
    const { deps, log, printed } = fakes({ result: outcomes.held });
    assert.equal(await resume([RUN], deps), 0);
    assert.deepEqual(log.slice(-5), [
      "listen stop",
      "host stop",
      "invariants",
      "comment resumed held",
      `drop ${RUN}`,
    ]);
    assert.match(
      printed.join("\n"),
      /waiting for the bank: the processor's amount is \$1,000\.00/,
    );
    assert.match(printed.join("\n"), new RegExp(`Dropped ${RUN}\\.`));
  });

  it("with --keep, keeps the copy of a held payout and says so", async () => {
    const { deps, log, printed } = fakes({ result: outcomes.held });
    assert.equal(await resume([RUN, "--keep"], deps), 0);
    assert.ok(log.includes("invariants"));
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
    assert.match(
      printed.join("\n"),
      /Kept leasebook_sandbox_run_ab12cd34, as --keep asked/,
    );
  });

  it("keeps the copy when the Playwright step fails, and says what it had found", async () => {
    const { deps, log, printed } = fakes({
      playwrightCode: 1,
      result: outcomes.held,
    });
    assert.equal(await resume([RUN], deps), 1);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
    assert.match(
      printed.join("\n"),
      /The Playwright step failed \(exit code 1\)/,
    );
    assert.match(printed.join("\n"), /It had found: held, payout po_…9Zz9/);
  });

  it("keeps the copy when the invariants fail after a held payout", async () => {
    const { deps, log } = fakes({ result: outcomes.held, invariantsCode: 1 });
    assert.equal(await resume([RUN], deps), 1);
    assert.ok(!log.some((entry) => entry.startsWith("drop")));
  });

  it("refuses a copy made from the other source", async () => {
    const { deps } = fakes({ record: { source: "standin" } });
    await assert.rejects(
      resume([RUN], deps),
      /copied from the stand-in fixture/,
    );
  });

  it("decides drop or keep from the outcome alone", () => {
    assert.deepEqual(
      resumeDecision({ code: 0, result: outcomes.none, keep: false }),
      { outcome: "none", ends: false, drop: false },
    );
    assert.deepEqual(
      resumeDecision({ code: 0, result: outcomes.held, keep: false }),
      { outcome: "held", ends: true, drop: true },
    );
    assert.deepEqual(
      resumeDecision({ code: 0, result: outcomes.held, keep: true }),
      { outcome: "held", ends: true, drop: false },
    );
    assert.deepEqual(
      resumeDecision({ code: 0, result: { outcome: "other" }, keep: false }),
      { outcome: "other", ends: false, drop: false },
    );
    assert.deepEqual(
      resumeDecision({ code: 0, result: undefined, keep: false }),
      { outcome: "unknown", ends: false, drop: false },
    );
  });
});

describe("discard", () => {
  it("drops the copy when nothing is listening or connected", async () => {
    const { deps, log, printed } = fakes();
    assert.equal(await discard(["ab12cd34"], deps), 0);
    assert.deepEqual(log, [`drop ${RUN}`]);
    assert.match(printed.join("\n"), /test payments stay at Stripe/);
  });

  it("refuses while a host answers, or a session is connected", async () => {
    for (const options of [{ hostRunning: true }, { connected: 2 }]) {
      const { deps, log } = fakes(options);
      await assert.rejects(discard([RUN], deps), /still/);
      assert.deepEqual(log, []);
    }
  });

  it("never drops a fixture", async () => {
    const { deps, log } = fakes();
    await assert.rejects(
      discard([FIXTURE_DATABASE], deps),
      /only ever the source of a copy/,
    );
    assert.deepEqual(log, []);
  });
});
