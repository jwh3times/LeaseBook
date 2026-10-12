import assert from "node:assert/strict";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import {
  buildInventory,
  matchRoute,
  parseArgs,
  parseRequestLines,
  renderMarkdown,
  routesFromOpenApi,
  run,
  segmentsOf,
} from "./route-inventory.mjs";

const document = {
  paths: {
    "/api/directory/owners": { get: {}, post: {} },
    "/api/directory/owners/{ownerId}": {
      get: {},
      put: {},
      parameters: [{ name: "ownerId" }],
    },
    "/api/directory/owners/export": { get: {} },
    "/api/statements/{ownerId}/pdf": { get: {} },
    "/api/health": { get: {} },
    "/callbacks/payments/simulation": { post: {} },
  },
};
const routes = routesFromOpenApi(document);

const scratch = [];
function tempDir() {
  const directory = mkdtempSync(path.join(os.tmpdir(), "leasebook-routes-"));
  scratch.push(directory);
  return directory;
}
afterEach(() => {
  for (const directory of scratch.splice(0)) {
    rmSync(directory, { recursive: true, force: true });
  }
});

describe("routesFromOpenApi", () => {
  it("lists one route per HTTP method and skips path-level keys that are not methods", () => {
    assert.deepEqual(
      routes.map((route) => `${route.method} ${route.path}`),
      [
        "GET /api/directory/owners",
        "POST /api/directory/owners",
        "GET /api/directory/owners/{ownerId}",
        "PUT /api/directory/owners/{ownerId}",
        "GET /api/directory/owners/export",
        "GET /api/statements/{ownerId}/pdf",
        "GET /api/health",
        "POST /callbacks/payments/simulation",
      ],
    );
  });

  it("groups by the segment after /api, and by the first segment outside it", () => {
    const groups = Object.fromEntries(
      routes.map((route) => [route.path, route.group]),
    );
    assert.equal(groups["/api/directory/owners"], "directory");
    assert.equal(groups["/api/health"], "health");
    assert.equal(groups["/callbacks/payments/simulation"], "callbacks");
  });
});

describe("segmentsOf", () => {
  it("drops the query string, the fragment and a trailing slash", () => {
    assert.deepEqual(segmentsOf("/api/a/b/?x=1&y=/z#frag"), ["api", "a", "b"]);
  });
});

describe("matchRoute", () => {
  it("matches a parameter segment to any single value", () => {
    const route = matchRoute(
      routes,
      "GET",
      "/api/statements/01923000-0000-7000-8000-000000000a05/pdf?year=2026&month=5",
    );
    assert.equal(route?.path, "/api/statements/{ownerId}/pdf");
  });

  it("prefers a literal segment to a parameter, as ASP.NET Core routing does", () => {
    assert.equal(
      matchRoute(routes, "GET", "/api/directory/owners/export")?.path,
      "/api/directory/owners/export",
    );
    assert.equal(
      matchRoute(routes, "GET", "/api/directory/owners/abc")?.path,
      "/api/directory/owners/{ownerId}",
    );
  });

  it("tells methods apart on the same path", () => {
    assert.equal(
      matchRoute(routes, "put", "/api/directory/owners/abc")?.method,
      "PUT",
    );
    assert.equal(
      matchRoute(routes, "DELETE", "/api/directory/owners/abc"),
      undefined,
    );
  });

  it("ignores the case of literal segments", () => {
    assert.equal(
      matchRoute(routes, "GET", "/API/Directory/Owners")?.path,
      "/api/directory/owners",
    );
  });

  it("does not match a path with more or fewer segments", () => {
    assert.equal(
      matchRoute(routes, "GET", "/api/directory/owners/a/b"),
      undefined,
    );
    assert.equal(matchRoute(routes, "GET", "/api/directory"), undefined);
  });
});

describe("buildInventory", () => {
  const requests = [
    { method: "GET", path: "/api/directory/owners?page=1", via: "browser" },
    { method: "GET", path: "/api/directory/owners", via: "browser" },
    { method: "GET", path: "/api/directory/owners", via: "api-client" },
    // Only the test's own client called it: not a route a screen reached.
    {
      method: "POST",
      path: "/callbacks/payments/simulation",
      via: "api-client",
    },
    // The test answered it with page.route, so the host never saw it.
    {
      method: "GET",
      path: "/api/directory/owners/export",
      via: "browser",
      served: false,
    },
    // Mapped by the host but absent from the contract.
    { method: "GET", path: "/api/health/ready", via: "browser" },
    { method: "GET", path: "/api/health/ready/", via: "browser" },
  ];
  const inventory = buildInventory(routes, requests);
  const row = (method, routePath) =>
    inventory.routes.find(
      (route) => route.method === method && route.path === routePath,
    );

  it("counts a route a page reached as reached by the browser", () => {
    const owners = row("GET", "/api/directory/owners");
    assert.equal(owners.reached, "browser");
    assert.equal(owners.browserRequests, 2);
    assert.equal(owners.apiClientRequests, 1);
  });

  it("keeps a route only the request client called apart from the browser's", () => {
    assert.equal(
      row("POST", "/callbacks/payments/simulation").reached,
      "api-client",
    );
  });

  it("does not count a request the test intercepted", () => {
    const exported = row("GET", "/api/directory/owners/export");
    assert.equal(exported.reached, "none");
    assert.equal(exported.interceptedRequests, 1);
  });

  it("lists requests the contract has no route for, merged by method and path", () => {
    assert.deepEqual(inventory.unmatched, [
      { method: "GET", path: "/api/health/ready", requests: 2 },
    ]);
  });

  it("totals every route exactly once", () => {
    const { totals } = inventory;
    assert.equal(totals.routes, routes.length);
    assert.equal(
      totals.browser + totals.apiClientOnly + totals.notReached,
      totals.routes,
    );
    assert.deepEqual(
      [totals.browser, totals.apiClientOnly, totals.notReached],
      [1, 1, 6],
    );
  });

  it("renders the totals, each group and the unreached routes", () => {
    const markdown = renderMarkdown(inventory);
    assert.match(markdown, /1 of 8 routes .* reached by a browser \(12\.5%\)/);
    assert.match(markdown, /\| directory \| 5 \| 1 \| 0 \| 4 \|/);
    assert.match(markdown, /\| callbacks \| 1 \| 0 \| 1 \| 0 \|/);
    assert.match(markdown, /- `PUT \/api\/directory\/owners\/\{ownerId\}`/);
    assert.match(
      markdown,
      /- `GET \/api\/directory\/owners\/export` \(requested, but the test answered it/,
    );
    assert.match(markdown, /- `GET \/api\/health\/ready` \(2 requests\)/);
  });
});

describe("parseRequestLines", () => {
  it("reads one object per line and skips blank lines", () => {
    assert.deepEqual(
      parseRequestLines(
        '{"method":"GET","path":"/a"}\n\n{"method":"POST","path":"/b"}\n',
      ),
      [
        { method: "GET", path: "/a" },
        { method: "POST", path: "/b" },
      ],
    );
  });
});

describe("run", () => {
  function inputs() {
    const directory = tempDir();
    const openapi = path.join(directory, "openapi.json");
    writeFileSync(openapi, JSON.stringify(document));
    const requests = path.join(directory, "requests");
    return { directory, openapi, requests };
  }

  it("writes the JSON and the markdown from a directory of request files", () => {
    const { directory, openapi, requests } = inputs();
    mkdirSync(requests);
    writeFileSync(
      path.join(requests, "a.ndjson"),
      '{"method":"GET","path":"/api/health","via":"browser"}\n',
    );
    writeFileSync(
      path.join(requests, "b.ndjson"),
      '{"method":"POST","path":"/api/directory/owners","via":"browser"}\n',
    );
    const outJson = path.join(directory, "out", "routes.json");
    const outMd = path.join(directory, "out", "routes.md");

    const inventory = run([
      "--openapi",
      openapi,
      "--requests",
      requests,
      "--out-json",
      outJson,
      "--out-md",
      outMd,
    ]);

    assert.equal(inventory.totals.browser, 2);
    assert.deepEqual(JSON.parse(readFileSync(outJson, "utf8")), inventory);
    assert.match(readFileSync(outMd, "utf8"), /^## Route inventory/);
  });

  it("fails when nothing was recorded, and does not report every route as unreached", () => {
    const { directory, openapi, requests } = inputs();
    mkdirSync(requests);
    assert.throws(
      () =>
        run([
          "--openapi",
          openapi,
          "--requests",
          requests,
          "--out-json",
          path.join(directory, "routes.json"),
          "--out-md",
          path.join(directory, "routes.md"),
        ]),
      /No requests were recorded/,
    );
  });

  it("fails when the request directory is missing", () => {
    const { directory, openapi, requests } = inputs();
    assert.throws(
      () =>
        run([
          "--openapi",
          openapi,
          "--requests",
          requests,
          "--out-json",
          path.join(directory, "routes.json"),
          "--out-md",
          path.join(directory, "routes.md"),
        ]),
      /No recorded requests at/,
    );
  });
});

describe("parseArgs", () => {
  it("rejects an unknown argument and a missing one", () => {
    assert.throws(() => parseArgs(["--nope", "x"]), /Unknown or incomplete/);
    assert.throws(() => parseArgs(["--openapi", "x"]), /Usage:/);
  });
});
