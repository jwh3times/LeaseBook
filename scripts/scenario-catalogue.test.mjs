import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { fileURLToPath } from "node:url";
import { routesFromOpenApi } from "./route-inventory.mjs";
import {
  CATALOGUE_PATH,
  DOC_PATH,
  EXPECTED_FAILURE_TAG,
  PASSES,
  checkEvents,
  checkRoutes,
  checkTags,
  docIsCurrent,
  eventCatalogueFromSource,
  parseArgs,
  readSourceFiles,
  renderDoc,
  renderEvidence,
  scenarioIdsOf,
  spliceGenerated,
  testsFromPlaywrightList,
  validateCatalogue,
  verifyRun,
} from "./scenario-catalogue.mjs";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);

// A small product: two business events, and a reversal that has a template and no business event.
const source = (extra = "") => [
  {
    path: "src/Accounting/Contracts/EventTypes.cs",
    text: 'public static class EventTypes\n{\n    public const string EntryVoided = "EntryVoided";\n}\n',
  },
  {
    path: "src/Accounting/Events.cs",
    text:
      "public abstract record AccountingEvent;\n" +
      "public sealed record Position(Guid TenantId, Money Amount);\n" +
      "public sealed record RentCharged(\n    Guid TenantId, Money Amount,\n    string? Note = null) : AccountingEvent;\n" +
      "public sealed record PaymentReceived(Guid TenantId, Money Amount) : AccountingEvent;\n" +
      extra,
  },
  {
    path: "src/Accounting/Service.cs",
    text:
      'posting.PostAsync(new PostEntryRequest(e.Date, "RentCharged", null, e.Description,\n    [new(Codes.Receivable, e.Amount, null)]), ct);\n' +
      "return await posting.PostAsync(new PostEntryRequest(\n" +
      '    Latest(e.Date, other), "PaymentReceived", Subtype(e.Method), lines), ct);\n' +
      "var request = new PostEntryRequest(\n    asOfDate,\n    EventTypes.EntryVoided,\n    EventSubtype: null);\n",
  },
];

const catalogue = () => ({
  scenarios: [
    {
      id: "record-payment",
      workflow: "Record a tenant payment",
      area: "Accounting",
      spec: "ledger.spec.ts",
      org: "demo",
      events: ["PaymentReceived"],
      routes: ["POST /api/tenants/{tenantId}/payments"],
    },
    {
      id: "browse-owners",
      workflow: "Browse the owners list",
      area: "Directory",
      spec: "directory.spec.ts",
      events: [],
      routes: ["GET /api/owners"],
    },
  ],
  exemptions: {
    events: [
      {
        event: "RentCharged",
        reason: "baseline gap",
        date: "2026-10-12",
        issue: 544,
      },
      {
        event: "EntryVoided",
        reason: "baseline gap",
        date: "2026-10-12",
        issue: 544,
      },
    ],
    routes: [
      {
        route: "GET /api/health",
        reason: "baseline gap",
        date: "2026-10-12",
        issue: 544,
      },
    ],
  },
});

const routes = routesFromOpenApi({
  paths: {
    "/api/tenants/{tenantId}/payments": { post: {} },
    "/api/owners": { get: {} },
    "/api/health": { get: {} },
  },
});

describe("eventCatalogueFromSource", () => {
  it("reads every event type a PostEntryRequest names, and marks the business events", () => {
    const { events, problems } = eventCatalogueFromSource(source());
    assert.deepEqual(problems, []);
    assert.deepEqual(
      events.map((event) => [event.name, event.businessEvent]),
      [
        ["EntryVoided", false],
        ["PaymentReceived", true],
        ["RentCharged", true],
      ],
    );
    assert.equal(events[2].source, "src/Accounting/Service.cs:1");
  });

  it("does not take a record that is not an AccountingEvent for a business event", () => {
    const { events } = eventCatalogueFromSource(source());
    assert.equal(
      events.some((event) => event.name === "Position"),
      false,
    );
  });

  it("refuses an event type it cannot read", () => {
    const files = source();
    files[2].text += "var x = new PostEntryRequest(date, eventName, null);\n";
    const { problems } = eventCatalogueFromSource(files);
    assert.equal(problems.length, 1);
    assert.match(
      problems[0],
      /Service\.cs:9: cannot read the event type.*`eventName`/,
    );
  });

  it("refuses a business event with no posting template of its name", () => {
    const { problems } = eventCatalogueFromSource(
      source(
        "public sealed record CreditIssued(Guid TenantId) : AccountingEvent;\n",
      ),
    );
    assert.deepEqual(problems, [
      'Business event CreditIssued has no posting template the check can find: no PostEntryRequest names "CreditIssued".',
    ]);
  });

  it("reads the repository's own source without a problem, and every business event has a template", () => {
    const { events, problems } = eventCatalogueFromSource(
      readSourceFiles(path.join(repoRoot, "src")),
    );
    assert.deepEqual(problems, []);
    assert.ok(events.some((event) => event.businessEvent));
    // The reversal is the template that has no business event. If this stops holding, the model
    // the check describes has changed.
    assert.ok(events.some((event) => !event.businessEvent));
  });
});

describe("validateCatalogue", () => {
  it("accepts a well-formed catalogue", () => {
    assert.deepEqual(validateCatalogue(catalogue()), []);
  });

  it("names a duplicate id, a malformed route and a missing field", () => {
    const broken = catalogue();
    broken.scenarios[1].id = "record-payment";
    broken.scenarios[1].routes = ["/api/owners"];
    delete broken.scenarios[0].workflow;
    const problems = validateCatalogue(broken).join("\n");
    assert.match(problems, /Scenario id record-payment is used twice/);
    assert.match(problems, /`\/api\/owners` is not "METHOD \/path"/);
    assert.match(problems, /`workflow` is required/);
  });

  it("refuses a pass that is not one of the passes that run", () => {
    const named = catalogue();
    for (const pass of PASSES) {
      named.scenarios[0].pass = pass;
      assert.deepEqual(validateCatalogue(named), []);
    }
    named.scenarios[0].pass = "standin";
    assert.match(
      validateCatalogue(named).join("\n"),
      /record-payment: `pass` must be one of main, payments, stand-in, or left out for main/,
    );
  });

  it("requires an expected failure to give its reason", () => {
    const named = catalogue();
    named.scenarios[1].expectedFailure =
      "ADR-000 known limit: owners cannot be listed.";
    assert.deepEqual(validateCatalogue(named), []);
    for (const reason of ["", "  ", true]) {
      named.scenarios[1].expectedFailure = reason;
      assert.match(
        validateCatalogue(named).join("\n"),
        /browse-owners: `expectedFailure` is the reason its test is expected to fail/,
      );
    }
  });

  it("requires a money scenario to name its organization", () => {
    const broken = catalogue();
    delete broken.scenarios[0].org;
    assert.match(
      validateCatalogue(broken).join("\n"),
      /record-payment: a scenario that declares journal events must name its organization/,
    );
  });

  it("requires an exemption to carry a reason and a date, and a baseline gap to name its issue", () => {
    const broken = catalogue();
    broken.exemptions.events[0] = { event: "RentCharged", reason: " " };
    delete broken.exemptions.routes[0].issue;
    const problems = validateCatalogue(broken).join("\n");
    assert.match(problems, /event RentCharged: a reason is required/);
    assert.match(problems, /event RentCharged: `date` must be YYYY-MM-DD/);
    assert.match(
      problems,
      /route GET \/api\/health: a "baseline gap" names the issue/,
    );
  });

  it("accepts an exemption that is not a baseline gap without an issue", () => {
    const fine = catalogue();
    fine.exemptions.routes[0] = {
      route: "GET /api/health",
      reason: "A probe. No person calls it.",
      date: "2026-10-12",
    };
    assert.deepEqual(validateCatalogue(fine), []);
  });
});

describe("checkEvents", () => {
  const events = () => eventCatalogueFromSource(source()).events;

  it("passes when every event type has a scenario or an exemption", () => {
    assert.deepEqual(checkEvents(catalogue(), events()), []);
  });

  it("fails on a new business event with no scenario and no exemption", () => {
    const files = source(
      "public sealed record CreditIssued(Guid TenantId) : AccountingEvent;\n",
    );
    files[2].text +=
      'posting.PostAsync(new PostEntryRequest(e.Date, "CreditIssued", null), ct);\n';
    const { events: grown, problems } = eventCatalogueFromSource(files);
    assert.deepEqual(problems, []);
    const found = checkEvents(catalogue(), grown);
    assert.equal(found.length, 1);
    assert.match(
      found[0],
      /^Journal event type CreditIssued has no scenario and no exemption\./,
    );
  });

  it("fails on an exemption a scenario has made stale", () => {
    const stale = catalogue();
    stale.scenarios[0].events.push("EntryVoided");
    assert.deepEqual(checkEvents(stale, events()), [
      "Journal event type EntryVoided is exempted, but scenario record-payment declares it. Remove the exemption.",
    ]);
  });

  it("fails on an event type that no longer exists, declared or exempted", () => {
    const gone = catalogue();
    gone.scenarios[0].events.push("Renamed");
    gone.exemptions.events.push({
      event: "Removed",
      reason: "baseline gap",
      date: "2026-10-12",
      issue: 544,
    });
    const problems = checkEvents(gone, events()).join("\n");
    assert.match(
      problems,
      /record-payment declares journal event type Renamed, which does not exist/,
    );
    assert.match(
      problems,
      /Journal event type Removed is exempted, but it does not exist/,
    );
  });
});

describe("checkRoutes", () => {
  it("passes when every contract route has a scenario or an exemption", () => {
    assert.deepEqual(checkRoutes(catalogue(), routes), []);
  });

  it("fails on a new route with no scenario and no exemption", () => {
    const grown = routesFromOpenApi({
      paths: {
        "/api/tenants/{tenantId}/payments": { post: {} },
        "/api/owners": { get: {}, post: {} },
        "/api/health": { get: {} },
      },
    });
    const problems = checkRoutes(catalogue(), grown);
    assert.equal(problems.length, 1);
    assert.match(
      problems[0],
      /^Route POST \/api\/owners has no scenario and no exemption\./,
    );
  });

  it("fails on a route that left the contract, and on a stale exemption", () => {
    const stale = catalogue();
    stale.scenarios[1].routes.push("GET /api/health", "GET /api/gone");
    const problems = checkRoutes(stale, routes).join("\n");
    assert.match(
      problems,
      /Route GET \/api\/health is exempted, but scenario browse-owners declares it/,
    );
    assert.match(
      problems,
      /browse-owners declares route GET \/api\/gone, which does not exist/,
    );
  });
});

describe("checkTags", () => {
  const tests = () => [
    {
      file: "ledger.spec.ts",
      title: "records a payment",
      tags: ["@scenario:record-payment"],
    },
    {
      file: "directory.spec.ts",
      title: "owners > lists",
      tags: ["@slow", "@scenario:browse-owners"],
    },
  ];

  it("passes when every scenario has a tagged test in its spec and every test is tagged", () => {
    assert.deepEqual(checkTags(catalogue(), tests()), []);
  });

  it("fails on a catalogue id with no tagged test", () => {
    const listed = tests().slice(0, 1);
    assert.deepEqual(checkTags(catalogue(), listed), [
      "Scenario browse-owners has no test tagged @scenario:browse-owners in directory.spec.ts.",
    ]);
  });

  it("fails on a tag the catalogue does not list", () => {
    const listed = tests();
    listed.push({
      file: "ledger.spec.ts",
      title: "voids a payment",
      tags: ["@scenario:void-payment"],
    });
    assert.deepEqual(checkTags(catalogue(), listed), [
      'ledger.spec.ts: "voids a payment" is tagged @scenario:void-payment, which the catalogue does not list.',
    ]);
  });

  it("fails on a test with no scenario tag", () => {
    const listed = tests();
    listed.push({ file: "ledger.spec.ts", title: "untagged", tags: ["@slow"] });
    assert.match(
      checkTags(catalogue(), listed)[0],
      /^ledger\.spec\.ts: "untagged" carries no scenario tag\./,
    );
  });

  it("requires the tests of an expected failure, and only those, to carry the expected-failure tag", () => {
    const expected = catalogue();
    expected.scenarios[1].expectedFailure = "ADR-000 known limit.";
    const listed = tests();
    assert.deepEqual(checkTags(expected, listed), [
      'directory.spec.ts: "owners > lists" is a test of browse-owners, which the catalogue records as an expected failure, and is not tagged @expected-failure.',
    ]);
    listed[1].tags.push(EXPECTED_FAILURE_TAG);
    assert.deepEqual(checkTags(expected, listed), []);
    assert.deepEqual(checkTags(catalogue(), listed), [
      'directory.spec.ts: "owners > lists" is tagged @expected-failure, and the catalogue records no `expectedFailure` for browse-owners.',
    ]);
  });

  it("fails when the tagged test is not in the spec the catalogue names", () => {
    const listed = tests();
    listed[1].file = "other.spec.ts";
    const problems = checkTags(catalogue(), listed).join("\n");
    assert.match(
      problems,
      /the catalogue places that scenario in directory\.spec\.ts/,
    );
    assert.match(problems, /Scenario browse-owners has no test tagged/);
  });
});

describe("testsFromPlaywrightList", () => {
  it("flattens the suites, joins the titles and keeps the tags", () => {
    const report = {
      suites: [
        {
          title: "ledger.spec.ts",
          file: "ledger.spec.ts",
          specs: [
            {
              title: "records a payment",
              file: "ledger.spec.ts",
              tags: ["scenario:record-payment"],
            },
          ],
          suites: [
            {
              title: "voids",
              file: "ledger.spec.ts",
              specs: [
                { title: "with a reason", file: "ledger.spec.ts", tags: [] },
              ],
            },
          ],
        },
      ],
    };
    assert.deepEqual(testsFromPlaywrightList(report), [
      {
        file: "ledger.spec.ts",
        title: "records a payment",
        tags: ["@scenario:record-payment"],
      },
      { file: "ledger.spec.ts", title: "voids > with a reason", tags: [] },
    ]);
  });
});

describe("scenarioIdsOf", () => {
  it("keeps only scenario tags", () => {
    assert.deepEqual(scenarioIdsOf(["@slow", "@scenario:a-b"]), ["a-b"]);
    assert.deepEqual(scenarioIdsOf(undefined), []);
  });
});

describe("the generated document", () => {
  const frame =
    "# Scenarios\n\nMethod.\n\n<!-- scenario-catalogue:start -->\nold\n<!-- scenario-catalogue:end -->\n\nAfter.\n";

  it("renders every scenario and every exemption with its reason", () => {
    const text = renderDoc(catalogue());
    assert.match(
      text,
      /^2 scenarios\. 2 journal event types and 1 routes are exempt\./,
    );
    assert.match(
      text,
      /\| `record-payment` \| Record a tenant payment \| Accounting \| `ledger\.spec\.ts` \| demo \| `PaymentReceived` \| `POST \/api\/tenants\/\{tenantId\}\/payments` \|/,
    );
    assert.match(text, /\| baseline gap \| 3 \|/);
    assert.match(
      text,
      /\| `RentCharged` \| baseline gap \| 2026-10-12 \| #544 \|/,
    );
    assert.match(
      text,
      /\| `GET \/api\/health` \| baseline gap \| 2026-10-12 \| #544 \|/,
    );
  });

  it("says which scenarios are expected failures, and why", () => {
    const expected = catalogue();
    expected.scenarios[1].expectedFailure = "ADR-000 known limit.";
    const text = renderDoc(expected);
    assert.match(
      text,
      /^2 scenarios, 1 of them an expected failure that shows a known limit\. 2 journal event types/,
    );
    assert.match(
      text,
      /\| `browse-owners` \| Browse the owners list \*\*Expected to fail:\*\* ADR-000 known limit\. \| Directory \|/,
    );
  });

  it("replaces only what sits between the markers, and is stable when run twice", () => {
    const once = spliceGenerated(frame, renderDoc(catalogue()));
    assert.ok(once.startsWith("# Scenarios\n\nMethod.\n\n"));
    assert.ok(once.endsWith("<!-- scenario-catalogue:end -->\n\nAfter.\n"));
    assert.equal(once.includes("old"), false);
    assert.equal(spliceGenerated(once, renderDoc(catalogue())), once);
    assert.equal(docIsCurrent(once, catalogue()), true);
  });

  it("is stale once the catalogue changes", () => {
    const once = spliceGenerated(frame, renderDoc(catalogue()));
    const changed = catalogue();
    changed.scenarios[0].workflow = "Record a payment by check";
    assert.equal(docIsCurrent(once, changed), false);
  });

  it("refuses a document that has lost its markers", () => {
    assert.throws(() => spliceGenerated("# Scenarios\n", "x"), /needs a/);
  });
});

describe("verifyRun", () => {
  const attempt = (overrides) => ({
    pass: "main",
    file: "ledger.spec.ts",
    title: "records a payment",
    tags: ["@scenario:record-payment"],
    status: "passed",
    startedAt: 1000,
    endedAt: 2000,
    requests: [
      {
        method: "POST",
        path: "/api/tenants/7/payments",
        via: "browser",
        served: true,
      },
    ],
    ...overrides,
  });
  const owners = () =>
    attempt({
      file: "directory.spec.ts",
      title: "owners",
      tags: ["@scenario:browse-owners"],
      startedAt: 3000,
      endedAt: 4000,
      requests: [
        { method: "GET", path: "/api/owners", via: "browser", served: true },
      ],
    });
  const posting = (overrides) => ({
    pass: "main",
    eventType: "PaymentReceived",
    postedAt: 1500,
    ...overrides,
  });
  const verify = (overrides = {}) =>
    verifyRun({
      catalogue: catalogue(),
      tests: [attempt(), owners()],
      postings: [posting()],
      routes,
      passes: ["main"],
      knownEvents: ["PaymentReceived", "RentCharged", "EntryVoided"],
      ...overrides,
    });

  it("verifies a scenario whose tests posted its events and were served its routes", () => {
    const { rows, problems } = verify();
    assert.deepEqual(problems, []);
    assert.deepEqual(
      rows.map((row) => [row.id, row.state]),
      [
        ["record-payment", "verified"],
        ["browse-owners", "verified"],
      ],
    );
  });

  it("fails when a declared event was not posted during the run", () => {
    const { rows, problems } = verify({ postings: [] });
    assert.deepEqual(problems, [
      "Scenario record-payment declares journal event PaymentReceived, and no test of it posted one.",
    ]);
    assert.equal(rows[0].state, "failed");
    assert.deepEqual(rows[0].missingEvents, ["PaymentReceived"]);
  });

  it("does not credit a scenario with an entry another scenario's test posted", () => {
    const { problems } = verify({ postings: [posting({ postedAt: 3500 })] });
    assert.equal(problems.length, 1);
    assert.match(problems[0], /record-payment declares journal event/);
  });

  it("does not credit a scenario with an entry posted in another pass", () => {
    const { problems } = verify({ postings: [posting({ pass: "payments" })] });
    assert.equal(problems.length, 1);
  });

  it("fails when a declared route was requested but the test answered it itself", () => {
    const tests = [
      attempt({
        requests: [
          { method: "POST", path: "/api/tenants/7/payments", served: false },
        ],
      }),
      owners(),
    ];
    assert.deepEqual(verify({ tests }).problems, [
      "Scenario record-payment declares route POST /api/tenants/{tenantId}/payments, and no test of it was served a request for it.",
    ]);
  });

  it("fails a scenario whose tests were all skipped", () => {
    const tests = [attempt({ status: "skipped", requests: [] }), owners()];
    assert.deepEqual(verify({ tests, postings: [] }).problems, [
      "Scenario record-payment has no test that ran.",
    ]);
  });

  it("counts a retry: one attempt producing the evidence is enough", () => {
    const tests = [
      attempt({ status: "failed", requests: [], startedAt: 100, endedAt: 200 }),
      attempt(),
      owners(),
    ];
    assert.deepEqual(verify({ tests }).problems, []);
  });

  it("reports and does not judge a scenario whose pass did not run", () => {
    const later = catalogue();
    later.scenarios[0].pass = "payments";
    const { rows, problems } = verify({ catalogue: later, tests: [owners()] });
    assert.deepEqual(problems, []);
    assert.equal(rows[0].state, "pass not run");
  });

  it("fails on a posted event type the source reading does not hold", () => {
    const { problems } = verify({
      postings: [posting(), posting({ eventType: "Mystery", postedAt: 9000 })],
    });
    assert.deepEqual(problems, [
      "The run posted journal event type Mystery, which the check did not find in the product source.",
    ]);
  });

  it("lists, without failing, an event a scenario posted and does not declare", () => {
    const result = verify({
      postings: [posting(), posting({ eventType: "EntryVoided" })],
    });
    assert.deepEqual(result.problems, []);
    assert.deepEqual(result.rows[0].undeclaredEvents, ["EntryVoided"]);
    const text = renderEvidence(result);
    assert.match(text, /2 of 2 scenarios produced what they declare/);
    assert.match(text, /- `record-payment`: `EntryVoided`/);
  });

  it("reports an expected failure that produced what it declares as one, and not as verified", () => {
    const expected = catalogue();
    expected.scenarios[1].expectedFailure = "ADR-000 known limit.";
    const tests = [
      attempt(),
      {
        ...owners(),
        tags: ["@scenario:browse-owners", EXPECTED_FAILURE_TAG],
        status: "failed",
      },
    ];
    const result = verify({ catalogue: expected, tests });
    assert.deepEqual(result.problems, []);
    assert.deepEqual(
      result.rows.map((row) => [row.id, row.state]),
      [
        ["record-payment", "verified"],
        ["browse-owners", "expected failure"],
      ],
    );
    assert.match(
      renderEvidence(result),
      /2 of 2 scenarios produced what they declare, 1 of them as an expected failure that shows a known limit\. 0 did not/,
    );
  });

  it("fails an expected failure whose test passed, since the limit it shows may be fixed", () => {
    const expected = catalogue();
    expected.scenarios[1].expectedFailure = "ADR-000 known limit.";
    const { rows, problems } = verify({ catalogue: expected });
    assert.equal(rows[1].state, "failed");
    assert.deepEqual(problems, [
      "Scenario browse-owners is recorded as an expected failure, and a test of it passed. The limit it shows may be fixed: " +
        "remove `expectedFailure`, the @expected-failure tag and the test's `test.fail()`.",
    ]);
  });

  it("puts the failures in the summary", () => {
    const text = renderEvidence(verify({ postings: [] }));
    assert.match(
      text,
      /1 of 2 scenarios produced what they declare\. 1 did not/,
    );
    assert.match(text, /- Scenario record-payment declares journal event/);
  });
});

describe("parseArgs", () => {
  it("reads each command's options", () => {
    assert.deepEqual(parseArgs(["check", "--openapi", "a.json", "--no-tags"]), {
      command: "check",
      tags: false,
      openapi: "a.json",
    });
    assert.deepEqual(parseArgs(["doc"]), { command: "doc", tags: true });
  });

  it("refuses an unknown command, an unknown option and an incomplete verify", () => {
    assert.throws(() => parseArgs(["report"]), /Usage/);
    assert.throws(() => parseArgs(["check", "--nope", "x"]), /Unknown/);
    assert.throws(
      () => parseArgs(["verify", "--openapi", "a"]),
      /verify needs/,
    );
  });
});

describe("the repository's own catalogue", () => {
  const own = JSON.parse(readFileSync(CATALOGUE_PATH, "utf8"));

  it("is well formed", () => {
    assert.deepEqual(validateCatalogue(own), []);
  });

  it("gives every journal event type in the source a scenario or an exemption", () => {
    const { events } = eventCatalogueFromSource(
      readSourceFiles(path.join(repoRoot, "src")),
    );
    assert.deepEqual(checkEvents(own, events), []);
  });

  it("matches the generated table in the scenarios document", () => {
    assert.equal(docIsCurrent(readFileSync(DOC_PATH, "utf8"), own), true);
  });
});
