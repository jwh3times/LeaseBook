#!/usr/bin/env node

/**
 * Route inventory: which mapped API routes the Playwright run called, and which it did not
 * (ADR-055, docs/testing/coverage.md).
 *
 * The routes come from the OpenAPI document `scripts/emit-openapi.mjs` writes, the same one the
 * typed client is generated from. The requests come from the files the e2e coverage fixture
 * (`web/e2e/coverage-fixture.ts`) writes when `E2E_COVERAGE=1`: one JSON object per line, holding
 * `method`, `path`, `via` and `served`.
 *
 *   - `via` is `browser` for a request the page made, and `api-client` for one the test made itself
 *     through Playwright's request client. The two are counted apart: a route only the test's own
 *     client called is a route no screen was shown to reach.
 *   - `served` is false for a request the test answered itself with `page.route`. Such a request
 *     never reached the host, so it reaches no route.
 *
 * A request that matches no route in the document is listed and not dropped. That is how a mapped
 * route the document leaves out (the readiness probe, the development-only OpenAPI endpoint) shows
 * up without a second list to keep in step.
 *
 * Usage (from the repository root):
 *
 *   node scripts/route-inventory.mjs --openapi <document.json> --requests <dir> \
 *     --out-json <inventory.json> --out-md <inventory.md>
 *
 * Measurement only: the exit code is non-zero when the inputs are missing or empty, never because
 * of how many routes were reached.
 */

import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const METHODS = new Set([
  "get",
  "post",
  "put",
  "patch",
  "delete",
  "head",
  "options",
]);

/** Splits a path into its segments, ignoring a query string, a fragment and a trailing slash. */
export function segmentsOf(rawPath) {
  const withoutQuery = rawPath.split(/[?#]/, 1)[0];
  return withoutQuery.split("/").filter((segment) => segment.length > 0);
}

/** Every method and path template in an OpenAPI document, in document order. */
export function routesFromOpenApi(document) {
  const routes = [];
  for (const [template, operations] of Object.entries(document.paths ?? {})) {
    for (const method of Object.keys(operations)) {
      if (!METHODS.has(method.toLowerCase())) continue;
      const segments = segmentsOf(template);
      routes.push({
        method: method.toUpperCase(),
        path: template,
        group: groupOf(segments),
        segments: segments.map((segment) =>
          /^\{.+\}$/.test(segment)
            ? { parameter: true }
            : { parameter: false, literal: segment.toLowerCase() },
        ),
      });
    }
  }
  return routes;
}

// `/api/accounting/...` groups under `accounting`; a route outside `/api` groups under its first
// segment (`callbacks`).
function groupOf(segments) {
  if (segments.length === 0) return "/";
  if (segments[0].toLowerCase() === "api" && segments.length > 1) {
    return segments[1].toLowerCase();
  }
  return segments[0].toLowerCase();
}

function matches(route, method, segments) {
  if (route.method !== method) return false;
  if (route.segments.length !== segments.length) return false;
  return route.segments.every((expected, index) =>
    expected.parameter
      ? true
      : // ASP.NET Core matches route literals without regard to case.
        expected.literal === segments[index].toLowerCase(),
  );
}

// ASP.NET Core prefers a literal segment to a parameter, deciding at the first segment where two
// candidates differ. `/owners/export` therefore goes to the literal route and not to `/owners/{id}`.
function moreSpecific(candidate, incumbent) {
  for (let index = 0; index < candidate.segments.length; index += 1) {
    const a = candidate.segments[index].parameter;
    const b = incumbent.segments[index].parameter;
    if (a !== b) return !a;
  }
  return false;
}

/** The route a request resolves to, or `undefined` when the document holds none for it. */
export function matchRoute(routes, method, rawPath) {
  const upper = method.toUpperCase();
  const segments = segmentsOf(rawPath);
  let best;
  for (const route of routes) {
    if (!matches(route, upper, segments)) continue;
    if (!best || moreSpecific(route, best)) best = route;
  }
  return best;
}

/** Parses the fixture's request files: one JSON object per non-empty line. */
export function parseRequestLines(text) {
  return text
    .split("\n")
    .map((line) => line.trim())
    .filter((line) => line.length > 0)
    .map((line) => JSON.parse(line));
}

const key = (method, routePath) => `${method} ${routePath}`;

/**
 * Sorts every request onto its route.
 *
 * A route is `browser` when a page reached it, `api-client` when only the test's own request client
 * did, and `none` otherwise.
 */
export function buildInventory(routes, requests) {
  const tallies = new Map(
    routes.map((route) => [
      key(route.method, route.path),
      { browser: 0, apiClient: 0, intercepted: 0 },
    ]),
  );
  const unmatched = new Map();

  for (const request of requests) {
    const method = String(request.method).toUpperCase();
    const route = matchRoute(routes, method, request.path);
    if (!route) {
      const normalised = `/${segmentsOf(request.path).join("/")}`;
      const id = key(method, normalised);
      const entry = unmatched.get(id) ?? {
        method,
        path: normalised,
        requests: 0,
      };
      entry.requests += 1;
      unmatched.set(id, entry);
      continue;
    }
    const tally = tallies.get(key(route.method, route.path));
    if (request.served === false) tally.intercepted += 1;
    else if (request.via === "api-client") tally.apiClient += 1;
    else tally.browser += 1;
  }

  const rows = routes.map((route) => {
    const tally = tallies.get(key(route.method, route.path));
    let reached = "none";
    if (tally.browser > 0) reached = "browser";
    else if (tally.apiClient > 0) reached = "api-client";
    return {
      method: route.method,
      path: route.path,
      group: route.group,
      reached,
      browserRequests: tally.browser,
      apiClientRequests: tally.apiClient,
      interceptedRequests: tally.intercepted,
    };
  });

  const count = (state) => rows.filter((row) => row.reached === state).length;
  return {
    totals: {
      routes: rows.length,
      browser: count("browser"),
      apiClientOnly: count("api-client"),
      notReached: count("none"),
      requests: requests.length,
    },
    routes: rows,
    unmatched: [...unmatched.values()].sort((a, b) =>
      key(a.method, a.path).localeCompare(key(b.method, b.path)),
    ),
  };
}

function percent(part, whole) {
  return whole === 0 ? "n/a" : `${((part / whole) * 100).toFixed(1)}%`;
}

/** The job-summary rendering: totals, a table per route group, then the routes nobody reached. */
export function renderMarkdown(inventory, title = "Route inventory") {
  const { totals, routes, unmatched } = inventory;
  const lines = [
    `## ${title}`,
    "",
    `${totals.browser} of ${totals.routes} routes in the API contract were reached by a browser ` +
      `(${percent(totals.browser, totals.routes)}). ${totals.apiClientOnly} more were called only by ` +
      `the tests' own request client, and ${totals.notReached} were not called at all.`,
    "",
    "| Group | Routes | Reached by a browser | Test client only | Not reached |",
    "| :-- | --: | --: | --: | --: |",
  ];

  const groups = [...new Set(routes.map((route) => route.group))].sort();
  for (const group of groups) {
    const inGroup = routes.filter((route) => route.group === group);
    const state = (name) =>
      inGroup.filter((route) => route.reached === name).length;
    lines.push(
      `| ${group} | ${inGroup.length} | ${state("browser")} | ${state("api-client")} | ${state("none")} |`,
    );
  }
  lines.push(
    `| **Total** | **${totals.routes}** | **${totals.browser}** | **${totals.apiClientOnly}** | **${totals.notReached}** |`,
    "",
  );

  const section = (heading, state) => {
    const selected = routes.filter((route) => route.reached === state);
    lines.push(
      `<details><summary>${heading} (${selected.length})</summary>`,
      "",
    );
    if (selected.length === 0) lines.push("None.");
    for (const route of selected) {
      const intercepted =
        route.interceptedRequests > 0
          ? " (requested, but the test answered it with `page.route`)"
          : "";
      lines.push(`- \`${route.method} ${route.path}\`${intercepted}`);
    }
    lines.push("", "</details>", "");
  };
  section("Routes no test reached", "none");
  section("Routes only the tests' request client called", "api-client");

  lines.push(
    `<details><summary>Requests that match no route in the contract (${unmatched.length})</summary>`,
    "",
  );
  if (unmatched.length === 0) lines.push("None.");
  for (const entry of unmatched) {
    lines.push(
      `- \`${entry.method} ${entry.path}\` (${entry.requests} request${entry.requests === 1 ? "" : "s"})`,
    );
  }
  lines.push("", "</details>", "");
  return lines.join("\n");
}

/** Reads every `*.ndjson` file in a directory, or the one file named. */
export function readRequests(location) {
  if (!existsSync(location)) {
    throw new Error(`No recorded requests at ${location}.`);
  }
  const files = location.endsWith(".ndjson")
    ? [location]
    : readdirSync(location)
        .filter((name) => name.endsWith(".ndjson"))
        .sort()
        .map((name) => path.join(location, name));
  return files.flatMap((file) => parseRequestLines(readFileSync(file, "utf8")));
}

export function parseArgs(argv) {
  const options = {};
  const names = {
    "--openapi": "openapi",
    "--requests": "requests",
    "--out-json": "outJson",
    "--out-md": "outMd",
    "--title": "title",
  };
  for (let index = 0; index < argv.length; index += 2) {
    const name = names[argv[index]];
    const value = argv[index + 1];
    if (!name || value === undefined) {
      throw new Error(`Unknown or incomplete argument: ${argv[index]}`);
    }
    options[name] = value;
  }
  for (const required of ["openapi", "requests", "outJson", "outMd"]) {
    if (!options[required]) {
      throw new Error(
        "Usage: route-inventory.mjs --openapi <json> --requests <dir> --out-json <file> --out-md <file> [--title <text>]",
      );
    }
  }
  return options;
}

export function run(argv) {
  const options = parseArgs(argv);
  const routes = routesFromOpenApi(
    JSON.parse(readFileSync(options.openapi, "utf8")),
  );
  if (routes.length === 0) {
    throw new Error(`${options.openapi} holds no routes.`);
  }
  const requests = readRequests(options.requests);
  // An empty recording means the fixture did not run, not that the suite calls nothing. Reporting
  // every route as unreached would be a false figure, so this fails instead.
  if (requests.length === 0) {
    throw new Error(
      `No requests were recorded under ${options.requests}. Was the e2e run started with E2E_COVERAGE=1?`,
    );
  }
  const inventory = buildInventory(routes, requests);
  for (const target of [options.outJson, options.outMd]) {
    mkdirSync(path.dirname(path.resolve(target)), { recursive: true });
  }
  writeFileSync(options.outJson, `${JSON.stringify(inventory, null, 2)}\n`);
  writeFileSync(options.outMd, renderMarkdown(inventory, options.title));
  return inventory;
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    const { totals } = run(process.argv.slice(2));
    console.log(
      `Routes: ${totals.routes}; reached by a browser: ${totals.browser}; test client only: ${totals.apiClientOnly}; not reached: ${totals.notReached}.`,
    );
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
