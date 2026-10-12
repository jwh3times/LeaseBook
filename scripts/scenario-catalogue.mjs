#!/usr/bin/env node

/**
 * The scenario catalogue's checks (ADR-056, docs/testing/scenarios.md).
 *
 * `web/e2e/scenario-catalogue.json` names every end-to-end scenario: the workflow it drives, the
 * journal event types it should post, the API routes it should call, and the spec that holds its
 * tests. It also lists every exemption: a journal event type or a route that has no scenario, with
 * the reason. Each Playwright test carries its scenario's id as the tag `@scenario:<id>`.
 *
 * Three commands:
 *
 *   check   Static, for pull requests. Every journal event type in the code and every route in the
 *           API contract has a scenario or an exemption, and never both. Every catalogue id has a
 *           tagged test in its spec, and every test has a tag the catalogue knows. The generated
 *           part of docs/testing/scenarios.md matches the catalogue.
 *   doc     Rewrites the generated part of docs/testing/scenarios.md.
 *   verify  Measured, for the coverage run (`npm run e2e:coverage`). Compares what each scenario
 *           declares with what its tests did: the requests they made, and the journal entries
 *           posted while they ran. Fails when a scenario did not produce what it declares.
 *
 * Usage (from the repository root):
 *
 *   node scripts/scenario-catalogue.mjs check [--openapi <document.json>] [--no-tags]
 *   node scripts/scenario-catalogue.mjs doc
 *   node scripts/scenario-catalogue.mjs verify --openapi <document.json> --evidence <dir> \
 *     --passes main[,payments] --out-json <file> --out-md <file>
 *
 * `check` reads the routes only when `--openapi` names the emitted contract, and says so when it
 * does not. `--no-tags` skips the part that asks Playwright to list the tests, for a job without
 * the web dependencies installed.
 *
 * Every command also takes `--catalogue <file>` and `--doc <file>` in place of the repository's own,
 * which is how a change is tried on a copy.
 *
 * The journal event types come from the product source and not from a list kept here; see
 * `eventCatalogueFromSource`.
 */

import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import {
  matchRoute,
  parseRequestLines,
  routesFromOpenApi,
} from "./route-inventory.mjs";

const repoRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
export const CATALOGUE_PATH = path.join(
  repoRoot,
  "web",
  "e2e",
  "scenario-catalogue.json",
);
export const DOC_PATH = path.join(repoRoot, "docs", "testing", "scenarios.md");

export const TAG_PREFIX = "@scenario:";
export const BASELINE_GAP = "baseline gap";
const DOC_START = "<!-- scenario-catalogue:start -->";
const DOC_END = "<!-- scenario-catalogue:end -->";
const DEFAULT_PASS = "main";

const routeKey = (route) => `${route.method} ${route.path}`;

// ---------------------------------------------------------------------------------------------
// Journal event types, read from the product source
// ---------------------------------------------------------------------------------------------

/** Every `.cs` file under a directory, skipping build output and the EF migrations. */
export function readSourceFiles(directory) {
  const files = [];
  const walk = (current) => {
    for (const entry of readdirSync(current, { withFileTypes: true })) {
      const full = path.join(current, entry.name);
      if (entry.isDirectory()) {
        if (["bin", "obj", "Migrations"].includes(entry.name)) continue;
        walk(full);
      } else if (entry.name.endsWith(".cs")) {
        files.push({
          path: path.relative(repoRoot, full).replaceAll("\\", "/"),
          text: readFileSync(full, "utf8"),
        });
      }
    }
  };
  walk(directory);
  return files.sort((a, b) => a.path.localeCompare(b.path));
}

const lineOf = (text, index) => text.slice(0, index).split("\n").length;

// The text of the first `count` top-level arguments of a call, given the index just past its
// opening parenthesis. Brackets nest and string literals are skipped.
function leadingArguments(text, start, count) {
  const found = [];
  let depth = 0;
  let current = "";
  for (let index = start; index < text.length; index += 1) {
    const char = text[index];
    if (char === '"') {
      const end = text.indexOf('"', index + 1);
      if (end < 0) break;
      current += text.slice(index, end + 1);
      index = end;
      continue;
    }
    if ("([{".includes(char)) depth += 1;
    if (")]}".includes(char)) {
      if (depth === 0) {
        found.push(current.trim());
        return found;
      }
      depth -= 1;
    }
    if (char === "," && depth === 0) {
      found.push(current.trim());
      if (found.length === count) return found;
      current = "";
      continue;
    }
    current += char;
  }
  return found;
}

/**
 * The journal event types the product can post, and which of them are business events.
 *
 * Two things are read, and they are two lists in this codebase:
 *
 *   - **Business events** are the records that derive from `AccountingEvent`. A caller hands one to
 *     `IAccountingEvents.PostAsync`.
 *   - **Posting templates** are the places that build a `PostEntryRequest`, the only type the
 *     journal's write path accepts. Its second argument is the entry's `event_type`. Every business
 *     event has one template of the same name. A few templates have no business event: an opening
 *     position, a balance forward and a reversal are posted through their own methods.
 *
 * So the template list is the whole set of `journal_entries.event_type` values, and the business
 * events are a subset of it. This fails, and does not guess, when a `PostEntryRequest` names its
 * event type with anything but a string literal or an `EventTypes` constant, and when a business
 * event has no template of its own name.
 *
 * One limit: a `PostEntryRequest` built with a target-typed `new(...)` is not seen. A business
 * event added that way still fails here, for want of a template. A template with no business event
 * added that way would be missed here and caught by `verify`, which fails on a posted event type
 * this list does not hold.
 */
export function eventCatalogueFromSource(files) {
  const constants = new Map();
  const businessEvents = new Set();
  const templates = new Map();
  const problems = [];

  for (const file of files) {
    for (const match of file.text.matchAll(
      /\bclass\s+EventTypes\b[^{]*\{([^}]*)\}/g,
    )) {
      for (const constant of match[1].matchAll(
        /\bconst\s+string\s+(\w+)\s*=\s*"([^"]+)"/g,
      )) {
        constants.set(constant[1], constant[2]);
      }
    }
    for (const match of file.text.matchAll(
      /\brecord\s+(\w+)\s*\([^;{}]*?\)\s*:\s*AccountingEvent\b/g,
    )) {
      businessEvents.add(match[1]);
    }
  }

  for (const file of files) {
    for (const match of file.text.matchAll(/\bnew\s+PostEntryRequest\s*\(/g)) {
      const where = `${file.path}:${lineOf(file.text, match.index)}`;
      const [, second] = leadingArguments(
        file.text,
        match.index + match[0].length,
        2,
      );
      const argument = (second ?? "").replace(/^EventType\s*:\s*/, "");
      const literal = argument.match(/^"([^"]+)"$/);
      const constant = argument.match(/^EventTypes\.(\w+)$/);
      let eventType;
      if (literal) eventType = literal[1];
      else if (constant && constants.has(constant[1])) {
        eventType = constants.get(constant[1]);
      }
      if (!eventType) {
        problems.push(
          `${where}: cannot read the event type of this PostEntryRequest (\`${argument}\`). ` +
            "Name it with a string literal or an EventTypes constant.",
        );
        continue;
      }
      if (!templates.has(eventType)) templates.set(eventType, where);
    }
  }

  for (const name of businessEvents) {
    if (!templates.has(name)) {
      problems.push(
        `Business event ${name} has no posting template the check can find: no PostEntryRequest names "${name}".`,
      );
    }
  }
  if (templates.size === 0) {
    problems.push("No posting template was found in the product source.");
  }

  const events = [...templates.keys()].sort().map((name) => ({
    name,
    businessEvent: businessEvents.has(name),
    source: templates.get(name),
  }));
  return { events, problems };
}

// ---------------------------------------------------------------------------------------------
// The catalogue file
// ---------------------------------------------------------------------------------------------

const ID = /^[a-z0-9]+(-[a-z0-9]+)*$/;
const DATE = /^\d{4}-\d{2}-\d{2}$/;
const ROUTE = /^(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS) \/\S*$/;

const duplicates = (values) => [
  ...new Set(values.filter((value, index) => values.indexOf(value) !== index)),
];

/** Problems with the catalogue's own shape, before it is compared with anything. */
export function validateCatalogue(catalogue) {
  const problems = [];
  const scenarios = catalogue?.scenarios;
  const exemptions = catalogue?.exemptions;
  if (!Array.isArray(scenarios) || scenarios.length === 0) {
    return ["The catalogue lists no scenarios."];
  }
  if (
    !exemptions ||
    !Array.isArray(exemptions.events) ||
    !Array.isArray(exemptions.routes)
  ) {
    return ["The catalogue needs `exemptions.events` and `exemptions.routes`."];
  }

  for (const scenario of scenarios) {
    const name = `Scenario ${scenario.id ?? "(no id)"}`;
    if (typeof scenario.id !== "string" || !ID.test(scenario.id)) {
      problems.push(
        `${name}: the id must be lower-case words joined by hyphens.`,
      );
    }
    for (const field of ["workflow", "area", "spec"]) {
      if (
        typeof scenario[field] !== "string" ||
        scenario[field].trim() === ""
      ) {
        problems.push(`${name}: \`${field}\` is required.`);
      }
    }
    for (const field of ["events", "routes"]) {
      if (!Array.isArray(scenario[field])) {
        problems.push(
          `${name}: \`${field}\` must be a list (it may be empty).`,
        );
      }
    }
    for (const route of scenario.routes ?? []) {
      if (!ROUTE.test(route)) {
        problems.push(`${name}: \`${route}\` is not "METHOD /path".`);
      }
    }
    for (const field of ["events", "routes"]) {
      for (const repeated of duplicates(scenario[field] ?? [])) {
        problems.push(`${name}: \`${repeated}\` is listed twice.`);
      }
    }
    // A scenario that posts to the journal is a money scenario. It names the organization it
    // drives, which is the one whose invariants the run checks afterwards.
    if ((scenario.events ?? []).length > 0 && !scenario.org) {
      problems.push(
        `${name}: a scenario that declares journal events must name its organization in \`org\`.`,
      );
    }
  }
  for (const repeated of duplicates(scenarios.map((scenario) => scenario.id))) {
    problems.push(`Scenario id ${repeated} is used twice.`);
  }

  const checkExemption = (kind, key, entry) => {
    const name = `Exemption for ${kind} ${entry[key] ?? "(unnamed)"}`;
    if (typeof entry[key] !== "string" || entry[key] === "") {
      problems.push(`${name}: \`${key}\` is required.`);
    }
    if (typeof entry.reason !== "string" || entry.reason.trim() === "") {
      problems.push(`${name}: a reason is required.`);
    }
    if (typeof entry.date !== "string" || !DATE.test(entry.date)) {
      problems.push(`${name}: \`date\` must be YYYY-MM-DD.`);
    }
    if (entry.issue !== undefined && !Number.isInteger(entry.issue)) {
      problems.push(`${name}: \`issue\` must be an issue number.`);
    }
    if (entry.reason === BASELINE_GAP && entry.issue === undefined) {
      problems.push(
        `${name}: a "${BASELINE_GAP}" names the issue that closes it.`,
      );
    }
  };
  for (const entry of exemptions.events)
    checkExemption("event", "event", entry);
  for (const entry of exemptions.routes)
    checkExemption("route", "route", entry);
  for (const repeated of duplicates(exemptions.events.map((e) => e.event))) {
    problems.push(`Event ${repeated} is exempted twice.`);
  }
  for (const repeated of duplicates(exemptions.routes.map((e) => e.route))) {
    problems.push(`Route ${repeated} is exempted twice.`);
  }
  return problems;
}

// Every item of a kind has a scenario or an exemption, and never both; nothing names an item that
// does not exist. `known` is what the code or the contract holds.
function checkCoverage({ noun, known, declared, exempted, fix }) {
  const problems = [];
  const knownSet = new Set(known);
  for (const item of known) {
    const scenarios = declared.get(item) ?? [];
    if (scenarios.length === 0 && !exempted.has(item)) {
      problems.push(`${noun} ${item} has no scenario and no exemption. ${fix}`);
    }
    if (scenarios.length > 0 && exempted.has(item)) {
      problems.push(
        `${noun} ${item} is exempted, but scenario ${scenarios.join(", ")} declares it. Remove the exemption.`,
      );
    }
  }
  for (const [item, scenarios] of declared) {
    if (!knownSet.has(item)) {
      problems.push(
        `Scenario ${scenarios.join(", ")} declares ${noun.toLowerCase()} ${item}, which does not exist.`,
      );
    }
  }
  for (const item of exempted) {
    if (!knownSet.has(item)) {
      problems.push(
        `${noun} ${item} is exempted, but it does not exist. Remove the exemption.`,
      );
    }
  }
  return problems;
}

function declaredBy(catalogue, field) {
  const declared = new Map();
  for (const scenario of catalogue.scenarios) {
    for (const item of scenario[field] ?? []) {
      declared.set(item, [...(declared.get(item) ?? []), scenario.id]);
    }
  }
  return declared;
}

/** Every journal event type in the code has a scenario or an exemption. */
export function checkEvents(catalogue, events) {
  return checkCoverage({
    noun: "Journal event type",
    known: events.map((event) => event.name),
    declared: declaredBy(catalogue, "events"),
    exempted: new Set(catalogue.exemptions.events.map((entry) => entry.event)),
    fix: "Add it to a scenario's `events` in web/e2e/scenario-catalogue.json, or record an exemption with its reason.",
  });
}

/** Every route in the API contract has a scenario or an exemption. */
export function checkRoutes(catalogue, routes) {
  return checkCoverage({
    noun: "Route",
    known: routes.map(routeKey),
    declared: declaredBy(catalogue, "routes"),
    exempted: new Set(catalogue.exemptions.routes.map((entry) => entry.route)),
    fix: "Add it to a scenario's `routes` in web/e2e/scenario-catalogue.json, or record an exemption with its reason.",
  });
}

/** The scenario ids a test's tags name. */
export function scenarioIdsOf(tags) {
  return (tags ?? [])
    .filter((tag) => tag.startsWith(TAG_PREFIX))
    .map((tag) => tag.slice(TAG_PREFIX.length));
}

/** Flattens `playwright test --list --reporter=json` into one entry per test. */
export function testsFromPlaywrightList(report) {
  const tests = [];
  const walk = (suite, titles) => {
    const here =
      suite.title && suite.title !== suite.file
        ? [...titles, suite.title]
        : titles;
    for (const spec of suite.specs ?? []) {
      tests.push({
        file: path.basename(spec.file),
        title: [...here, spec.title].join(" > "),
        tags: (spec.tags ?? []).map((tag) =>
          tag.startsWith("@") ? tag : `@${tag}`,
        ),
      });
    }
    for (const child of suite.suites ?? []) walk(child, here);
  };
  for (const suite of report.suites ?? []) walk(suite, []);
  return tests;
}

/** Every catalogue id has a tagged test in its spec, and every test has a tag the catalogue knows. */
export function checkTags(catalogue, tests) {
  const problems = [];
  const byId = new Map(catalogue.scenarios.map((entry) => [entry.id, entry]));
  const tagged = new Set();
  for (const test of tests) {
    const ids = scenarioIdsOf(test.tags);
    const name = `${test.file}: "${test.title}"`;
    if (ids.length === 0) {
      problems.push(
        `${name} carries no scenario tag. Give it \`{ tag: '${TAG_PREFIX}<id>' }\`.`,
      );
    }
    for (const id of ids) {
      const scenario = byId.get(id);
      if (!scenario) {
        problems.push(
          `${name} is tagged ${TAG_PREFIX}${id}, which the catalogue does not list.`,
        );
        continue;
      }
      if (scenario.spec !== test.file) {
        problems.push(
          `${name} is tagged ${TAG_PREFIX}${id}, but the catalogue places that scenario in ${scenario.spec}.`,
        );
        continue;
      }
      tagged.add(id);
    }
  }
  for (const scenario of catalogue.scenarios) {
    if (!tagged.has(scenario.id)) {
      problems.push(
        `Scenario ${scenario.id} has no test tagged ${TAG_PREFIX}${scenario.id} in ${scenario.spec}.`,
      );
    }
  }
  return problems;
}

// ---------------------------------------------------------------------------------------------
// The generated document
// ---------------------------------------------------------------------------------------------

const code = (value) => `\`${value}\``;
const cell = (values) =>
  values.length === 0 ? "" : values.map(code).join("<br>");

/** The generated part of docs/testing/scenarios.md, from the catalogue alone. */
export function renderDoc(catalogue) {
  const { scenarios, exemptions } = catalogue;
  const reasons = new Map();
  for (const entry of [...exemptions.events, ...exemptions.routes]) {
    reasons.set(entry.reason, (reasons.get(entry.reason) ?? 0) + 1);
  }
  const issue = (entry) => (entry.issue === undefined ? "" : `#${entry.issue}`);
  const lines = [
    `${scenarios.length} scenarios. ${exemptions.events.length} journal event types and ` +
      `${exemptions.routes.length} routes are exempt.`,
    "",
    "### Scenarios",
    "",
    "| Scenario | Workflow | Area | Spec | Organization | Journal events | Routes |",
    "| :-- | :-- | :-- | :-- | :-- | :-- | :-- |",
  ];
  for (const scenario of scenarios) {
    lines.push(
      `| ${code(scenario.id)} | ${scenario.workflow} | ${scenario.area} | ${code(scenario.spec)} | ` +
        `${scenario.org ?? ""} | ${cell(scenario.events)} | ${cell(scenario.routes)} |`,
    );
  }
  lines.push("", "### Exemptions", "", "| Reason | Count |", "| :-- | --: |");
  for (const [reason, count] of [...reasons].sort()) {
    lines.push(`| ${reason} | ${count} |`);
  }
  lines.push(
    "",
    "#### Journal event types with no scenario",
    "",
    "| Event type | Reason | Date | Issue |",
    "| :-- | :-- | :-- | :-- |",
  );
  if (exemptions.events.length === 0) lines.push("| None | | | |");
  for (const entry of exemptions.events) {
    lines.push(
      `| ${code(entry.event)} | ${entry.reason} | ${entry.date} | ${issue(entry)} |`,
    );
  }
  lines.push(
    "",
    "#### Routes with no scenario",
    "",
    "| Route | Reason | Date | Issue |",
    "| :-- | :-- | :-- | :-- |",
  );
  if (exemptions.routes.length === 0) lines.push("| None | | | |");
  for (const entry of exemptions.routes) {
    lines.push(
      `| ${code(entry.route)} | ${entry.reason} | ${entry.date} | ${issue(entry)} |`,
    );
  }
  return lines.join("\n");
}

/** Replaces what sits between the two markers. Throws when the document has lost them. */
export function spliceGenerated(document, generated) {
  const start = document.indexOf(DOC_START);
  const end = document.indexOf(DOC_END);
  if (start < 0 || end < start) {
    throw new Error(
      `The document needs a ${DOC_START} line and a ${DOC_END} line after it.`,
    );
  }
  return (
    document.slice(0, start + DOC_START.length) +
    `\n<!-- prettier-ignore-start -->\n\n${generated}\n\n<!-- prettier-ignore-end -->\n` +
    document.slice(end)
  );
}

/** True when the document's generated part is what the catalogue renders to. */
export function docIsCurrent(document, catalogue) {
  return spliceGenerated(document, renderDoc(catalogue)) === document;
}

// ---------------------------------------------------------------------------------------------
// The measured check
// ---------------------------------------------------------------------------------------------

/**
 * Compares what each scenario declares with what its tests did.
 *
 *   - `tests`: one record per test attempt, as `web/e2e/coverage-fixture.ts` writes it, with the
 *     attempt's `requests` attached.
 *   - `postings`: the journal entries posted during the run: `pass`, `eventType` and `postedAt`
 *     (milliseconds).
 *   - `passes`: the Playwright passes that ran. A scenario of a pass that did not run is reported
 *     and not judged.
 *
 * The suite runs on one worker, so a journal entry posted between a test's start and end was
 * posted by that test.
 */
export function verifyRun({
  catalogue,
  tests,
  postings,
  routes,
  passes,
  knownEvents,
}) {
  const problems = [];
  const known = new Set(knownEvents);
  for (const eventType of new Set(postings.map((entry) => entry.eventType))) {
    if (!known.has(eventType)) {
      problems.push(
        `The run posted journal event type ${eventType}, which the check did not find in the product source.`,
      );
    }
  }

  const rows = catalogue.scenarios.map((scenario) => {
    const pass = scenario.pass ?? DEFAULT_PASS;
    const row = {
      id: scenario.id,
      pass,
      state: "verified",
      attempts: 0,
      missingEvents: [],
      missingRoutes: [],
      undeclaredEvents: [],
    };
    if (!passes.includes(pass)) {
      row.state = "pass not run";
      return row;
    }
    const attempts = tests.filter(
      (test) =>
        test.pass === pass &&
        test.status !== "skipped" &&
        scenarioIdsOf(test.tags).includes(scenario.id),
    );
    row.attempts = attempts.length;
    if (attempts.length === 0) {
      row.state = "failed";
      problems.push(`Scenario ${scenario.id} has no test that ran.`);
      return row;
    }

    const reached = new Set();
    for (const attempt of attempts) {
      for (const request of attempt.requests ?? []) {
        if (request.served === false) continue;
        const route = matchRoute(routes, String(request.method), request.path);
        if (route) reached.add(routeKey(route));
      }
    }
    const posted = new Set(
      postings
        .filter(
          (entry) =>
            entry.pass === pass &&
            attempts.some(
              (attempt) =>
                entry.postedAt >= attempt.startedAt &&
                entry.postedAt <= attempt.endedAt,
            ),
        )
        .map((entry) => entry.eventType),
    );

    row.missingRoutes = scenario.routes.filter((route) => !reached.has(route));
    row.missingEvents = scenario.events.filter((event) => !posted.has(event));
    row.undeclaredEvents = [...posted]
      .filter((event) => !scenario.events.includes(event))
      .sort();
    for (const event of row.missingEvents) {
      problems.push(
        `Scenario ${scenario.id} declares journal event ${event}, and no test of it posted one.`,
      );
    }
    for (const route of row.missingRoutes) {
      problems.push(
        `Scenario ${scenario.id} declares route ${route}, and no test of it was served a request for it.`,
      );
    }
    if (row.missingEvents.length + row.missingRoutes.length > 0) {
      row.state = "failed";
    }
    return row;
  });

  return { rows, problems };
}

/** The job-summary rendering of a measured check. */
export function renderEvidence(result, title = "Scenario catalogue") {
  const { rows, problems } = result;
  const count = (state) => rows.filter((row) => row.state === state).length;
  const lines = [
    `## ${title}`,
    "",
    `${count("verified")} of ${rows.length} scenarios produced what they declare. ` +
      `${count("failed")} did not, and ${count("pass not run")} belong to a pass this run did not make.`,
    "",
  ];
  if (problems.length > 0) {
    lines.push("**Failures:**", "");
    for (const problem of problems) lines.push(`- ${problem}`);
    lines.push("");
  }
  const extra = rows.filter((row) => row.undeclaredEvents.length > 0);
  lines.push(
    `<details><summary>Journal events a scenario posted and does not declare (${extra.length})</summary>`,
    "",
  );
  if (extra.length === 0) lines.push("None.");
  for (const row of extra) {
    lines.push(
      `- ${code(row.id)}: ${row.undeclaredEvents.map(code).join(", ")}`,
    );
  }
  lines.push("", "</details>", "");
  return lines.join("\n");
}

/** Reads the fixture's per-test records and attaches each one's recorded requests. */
export function readEvidence(directory) {
  const testsDir = path.join(directory, "tests");
  if (!existsSync(testsDir)) {
    throw new Error(
      `No test records under ${testsDir}. Was the e2e run started with E2E_COVERAGE=1?`,
    );
  }
  const tests = readdirSync(testsDir)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .map((name) => {
      const record = JSON.parse(
        readFileSync(path.join(testsDir, name), "utf8"),
      );
      const requests = path.join(
        directory,
        "requests",
        name.replace(/\.json$/, ".ndjson"),
      );
      record.requests = existsSync(requests)
        ? parseRequestLines(readFileSync(requests, "utf8"))
        : [];
      return record;
    });
  if (tests.length === 0) {
    throw new Error(`No test records under ${testsDir}.`);
  }
  const journalDir = path.join(directory, "journal");
  if (!existsSync(journalDir)) {
    throw new Error(`No journal reading under ${journalDir}.`);
  }
  const postings = readdirSync(journalDir)
    .filter((name) => name.endsWith(".json"))
    .sort()
    .flatMap((name) =>
      JSON.parse(readFileSync(path.join(journalDir, name), "utf8")),
    );
  return { tests, postings };
}

// ---------------------------------------------------------------------------------------------
// Command line
// ---------------------------------------------------------------------------------------------

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (!["check", "doc", "verify"].includes(command)) {
    throw new Error(
      "Usage: scenario-catalogue.mjs check [--openapi <json>] [--no-tags] | doc | " +
        "verify --openapi <json> --evidence <dir> --passes <a,b> --out-json <file> --out-md <file>",
    );
  }
  const options = { command, tags: true };
  const names = {
    "--openapi": "openapi",
    "--evidence": "evidence",
    "--passes": "passes",
    "--out-json": "outJson",
    "--out-md": "outMd",
    "--catalogue": "catalogue",
    "--doc": "doc",
  };
  for (let index = 0; index < rest.length; index += 1) {
    if (rest[index] === "--no-tags") {
      options.tags = false;
      continue;
    }
    const name = names[rest[index]];
    const value = rest[index + 1];
    if (!name || value === undefined) {
      throw new Error(`Unknown or incomplete argument: ${rest[index]}`);
    }
    options[name] = value;
    index += 1;
  }
  if (command === "verify") {
    for (const required of [
      "openapi",
      "evidence",
      "passes",
      "outJson",
      "outMd",
    ]) {
      if (!options[required]) {
        throw new Error(
          `verify needs ${Object.keys(names).find((key) => names[key] === required)}.`,
        );
      }
    }
  }
  return options;
}

function readJson(file) {
  return JSON.parse(readFileSync(file, "utf8"));
}

function listPlaywrightTests() {
  const result = spawnSync(
    "npx",
    ["playwright", "test", "--list", "--reporter=json"],
    {
      cwd: path.join(repoRoot, "web"),
      encoding: "utf8",
      maxBuffer: 64 * 1024 * 1024,
    },
  );
  if (result.error) throw result.error;
  if (result.status !== 0) {
    throw new Error(
      `Playwright could not list the tests:\n${result.stderr || result.stdout}`,
    );
  }
  return testsFromPlaywrightList(JSON.parse(result.stdout));
}

function sourceEvents() {
  const { events, problems } = eventCatalogueFromSource(
    readSourceFiles(path.join(repoRoot, "src")),
  );
  return { events, problems };
}

function contractRoutes(file) {
  const routes = routesFromOpenApi(readJson(file));
  if (routes.length === 0) throw new Error(`${file} holds no routes.`);
  return routes;
}

export function run(argv) {
  const options = parseArgs(argv);
  const cataloguePath = options.catalogue ?? CATALOGUE_PATH;
  const docPath = options.doc ?? DOC_PATH;
  const catalogue = readJson(cataloguePath);
  const shape = validateCatalogue(catalogue);
  if (shape.length > 0) return { problems: shape, notes: [] };

  if (options.command === "doc") {
    const current = readFileSync(docPath, "utf8");
    const next = spliceGenerated(current, renderDoc(catalogue));
    if (next !== current) writeFileSync(docPath, next);
    return {
      problems: [],
      notes: [
        next === current
          ? "The generated table was already current."
          : `Rewrote the generated table in ${path.relative(repoRoot, docPath)}.`,
      ],
    };
  }

  const source = sourceEvents();

  if (options.command === "check") {
    const problems = [...source.problems];
    const notes = [];
    problems.push(...checkEvents(catalogue, source.events));
    notes.push(
      `Journal event types: ${source.events.length} in the code ` +
        `(${source.events.filter((event) => event.businessEvent).length} business events).`,
    );
    if (options.openapi) {
      const routes = contractRoutes(options.openapi);
      problems.push(...checkRoutes(catalogue, routes));
      notes.push(`Routes: ${routes.length} in the contract.`);
    } else {
      notes.push("Routes: NOT checked (no --openapi).");
    }
    if (options.tags) {
      const tests = listPlaywrightTests();
      problems.push(...checkTags(catalogue, tests));
      notes.push(`Tests: ${tests.length} listed by Playwright.`);
    } else {
      notes.push("Test tags: NOT checked (--no-tags).");
    }
    if (!docIsCurrent(readFileSync(docPath, "utf8"), catalogue)) {
      problems.push(
        `${path.relative(repoRoot, docPath)} is stale. Run \`node scripts/scenario-catalogue.mjs doc\` and commit the result.`,
      );
    }
    notes.push(
      `Catalogue: ${catalogue.scenarios.length} scenarios, ${catalogue.exemptions.events.length} exempt event types, ` +
        `${catalogue.exemptions.routes.length} exempt routes.`,
    );
    return { problems, notes };
  }

  // verify
  const routes = contractRoutes(options.openapi);
  const { tests, postings } = readEvidence(options.evidence);
  const result = verifyRun({
    catalogue,
    tests,
    postings,
    routes,
    passes: options.passes.split(",").filter(Boolean),
    knownEvents: source.events.map((event) => event.name),
  });
  result.problems.unshift(...source.problems);
  for (const target of [options.outJson, options.outMd]) {
    mkdirSync(path.dirname(path.resolve(target)), { recursive: true });
  }
  writeFileSync(options.outJson, `${JSON.stringify(result, null, 2)}\n`);
  writeFileSync(options.outMd, renderEvidence(result));
  const verified = result.rows.filter((row) => row.state === "verified").length;
  return {
    problems: result.problems,
    notes: [
      `Scenarios: ${verified} of ${result.rows.length} produced what they declare.`,
    ],
  };
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    const { problems, notes } = run(process.argv.slice(2));
    for (const note of notes) console.log(note);
    if (problems.length > 0) {
      console.error(`\nScenario catalogue: ${problems.length} problem(s).`);
      for (const problem of problems) console.error(`  - ${problem}`);
      process.exit(1);
    }
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
