import assert from "node:assert/strict";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import {
  PRODUCT_ASSEMBLIES,
  areaOf,
  parseArgs,
  percent,
  renderMarkdown,
  run,
  summariseDotnet,
  summariseWeb,
} from "./coverage-summary.mjs";

const scratch = [];
function directory() {
  const created = mkdtempSync(path.join(os.tmpdir(), "leasebook-coverage-"));
  scratch.push(created);
  return created;
}
afterEach(() => {
  for (const created of scratch.splice(0)) {
    rmSync(created, { recursive: true, force: true });
  }
});

// The shape ReportGenerator's JsonSummary report writes, cut down to what is read.
function assembly(name, lines, branches, classes = 3) {
  return {
    name,
    classes,
    coveredlines: lines[0],
    coverablelines: lines[1],
    coveredbranches: branches[0],
    totalbranches: branches[1],
  };
}
function dotnetDocument(extra = []) {
  return {
    coverage: {
      assemblies: [
        // Out of order on purpose: the table has its own order.
        ...extra,
        ...PRODUCT_ASSEMBLIES.map(([name]) =>
          assembly(name, [50, 100], [5, 20]),
        ).reverse(),
      ],
    },
  };
}

describe("summariseDotnet", () => {
  it("lists the product assemblies in the fixed order, by their short names", () => {
    const result = summariseDotnet(dotnetDocument());
    assert.deepEqual(
      result.rows.map((row) => row.name),
      [
        "Accounting",
        "Directory",
        "Banking",
        "Reporting",
        "Operations",
        "Capabilities",
        "Payments",
        "SharedKernel",
        "Migrator",
        "Web host",
      ],
    );
    assert.deepEqual(result.rows[0].lines, { covered: 50, total: 100 });
    assert.deepEqual(result.rows[0].branches, { covered: 5, total: 20 });
  });

  it("totals the rows", () => {
    const { total } = summariseDotnet(dotnetDocument());
    assert.deepEqual(total.lines, { covered: 500, total: 1000 });
    assert.deepEqual(total.branches, { covered: 50, total: 200 });
    assert.equal(total.count, 30);
  });

  it("gives an assembly it does not know a row after the known ones", () => {
    const result = summariseDotnet(
      dotnetDocument([assembly("LeaseBook.Modules.New", [1, 2], [0, 0])]),
    );
    assert.equal(result.rows.at(-1).name, "LeaseBook.Modules.New");
    assert.equal(result.rows.length, PRODUCT_ASSEMBLIES.length + 1);
  });

  it("fails when a product assembly is missing from the report", () => {
    const document = dotnetDocument();
    document.coverage.assemblies = document.coverage.assemblies.filter(
      (entry) => entry.name !== "LeaseBook.Modules.Payments",
    );
    assert.throws(
      () => summariseDotnet(document),
      /no entry for: LeaseBook\.Modules\.Payments/,
    );
  });

  it("fails on a document with no assemblies at all", () => {
    assert.throws(() => summariseDotnet({}), /no entry for: LeaseBook/);
  });
});

const figures = (total, covered) => ({ total, covered, skipped: 0, pct: 0 });
const file = (lines, branches) => ({
  lines: figures(...lines),
  statements: figures(...lines),
  branches: figures(...branches),
  functions: figures(1, 1),
});
// Vitest keys files by absolute path; monocart by the path the report was configured to show.
const webDocument = {
  total: file([999, 999], [999, 999]),
  "/home/dev/leasebook/web/src/features/ledger/Ledger.tsx": file(
    [100, 50],
    [10, 5],
  ),
  "/home/dev/leasebook/web/src/features/owners/Owners.tsx": file(
    [100, 100],
    [0, 0],
  ),
  "src/design/formatMoney.ts": file([20, 20], [6, 3]),
  "src/App.tsx": file([10, 0], [2, 0]),
};

describe("areaOf", () => {
  it("names the first directory under src, from an absolute or a relative path", () => {
    assert.equal(areaOf("/a/web/src/features/ledger/Ledger.tsx"), "features");
    assert.equal(areaOf("src/design/formatMoney.ts"), "design");
    assert.equal(areaOf("C:\\repo\\web\\src\\lib\\x.ts"), "lib");
  });

  it("groups a file directly under src apart from the directories", () => {
    assert.equal(areaOf("/a/web/src/App.tsx"), "(src root)");
    assert.equal(areaOf("src/main.tsx"), "(src root)");
  });
});

describe("summariseWeb", () => {
  const result = summariseWeb(webDocument);

  it("adds the files of an area together and ignores the document's own total", () => {
    const features = result.rows.find((row) => row.name === "features");
    assert.equal(features.count, 2);
    assert.deepEqual(features.lines, { covered: 150, total: 200 });
    assert.deepEqual(features.branches, { covered: 5, total: 10 });
  });

  it("totals from the files, so the figure is the sum of the rows shown", () => {
    assert.equal(result.total.count, 4);
    assert.deepEqual(result.total.lines, { covered: 170, total: 230 });
  });

  it("orders the areas by name", () => {
    assert.deepEqual(
      result.rows.map((row) => row.name),
      ["(src root)", "design", "features"],
    );
  });

  it("fails on a summary with no files, and returns no table of zeroes", () => {
    assert.throws(
      () => summariseWeb({ total: webDocument.total }),
      /holds no source files/,
    );
  });
});

describe("percent", () => {
  it("rounds to one decimal place and does not divide by zero", () => {
    assert.equal(percent({ covered: 1, total: 3 }), "33.3%");
    assert.equal(percent({ covered: 0, total: 0 }), "n/a");
  });
});

describe("renderMarkdown", () => {
  it("writes one row per area and a total row", () => {
    const markdown = renderMarkdown(summariseWeb(webDocument), "Web unit");
    assert.match(markdown, /^## Web unit/);
    assert.match(markdown, /\| Area \| Files \| Lines covered \| Line \|/);
    assert.match(
      markdown,
      /\| features \| 2 \| 150 \/ 200 \| 75\.0% \| 5 \/ 10 \| 50\.0% \|/,
    );
    assert.match(
      markdown,
      /\| \*\*Total\*\* \| \*\*4\*\* \| \*\*170 \/ 230\*\* \| \*\*73\.9%\*\* \|/,
    );
  });

  it("heads the .NET table by module", () => {
    const markdown = renderMarkdown(summariseDotnet(dotnetDocument()), "T");
    assert.match(markdown, /\| Module \| Classes \| Lines covered \|/);
    assert.match(
      markdown,
      /\| Web host \| 3 \| 50 \/ 100 \| 50\.0% \| 5 \/ 20 \| 25\.0% \|/,
    );
  });
});

describe("run", () => {
  it("writes the markdown and the JSON when asked to", () => {
    const base = directory();
    const input = path.join(base, "coverage-summary.json");
    writeFileSync(input, JSON.stringify(webDocument));
    const outMd = path.join(base, "out", "summary.md");
    const outJson = path.join(base, "out", "summary.json");

    run([
      "web",
      "--summary",
      input,
      "--title",
      "T",
      "--out-md",
      outMd,
      "--out-json",
      outJson,
    ]);

    assert.match(readFileSync(outMd, "utf8"), /^## T/);
    assert.equal(JSON.parse(readFileSync(outJson, "utf8")).total.count, 4);
  });

  it("returns the markdown for standard output when no file is named", () => {
    const input = path.join(directory(), "Summary.json");
    writeFileSync(input, JSON.stringify(dotnetDocument()));
    const { markdown, outMd } = run([
      "dotnet",
      "--summary",
      input,
      "--title",
      "T",
    ]);
    assert.equal(outMd, undefined);
    assert.match(markdown, /\| \*\*Total\*\* \|/);
  });
});

describe("parseArgs", () => {
  it("requires a kind, the summary and the title", () => {
    assert.throws(() => parseArgs(["--summary", "x"]), /Usage:/);
    assert.throws(() => parseArgs(["web", "--summary", "x"]), /Usage:/);
    assert.throws(
      () => parseArgs(["web", "--nope", "x"]),
      /Unknown or incomplete/,
    );
  });
});
