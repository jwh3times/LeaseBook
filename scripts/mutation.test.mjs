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
  CELL_LIMIT,
  EQUIVALENTS_FILE,
  MODULES,
  fails,
  matches,
  normalise,
  parseArgs,
  readEquivalents,
  renderModule,
  repoPath,
  run,
  runStryker,
  shorten,
  spanOf,
  strykerArgs,
  summarise,
} from "./mutation.mjs";

const scratch = [];
function directory() {
  const created = mkdtempSync(path.join(os.tmpdir(), "leasebook-mutation-"));
  scratch.push(created);
  return created;
}
afterEach(() => {
  for (const created of scratch.splice(0)) {
    rmSync(created, { recursive: true, force: true });
  }
});

const SOURCE = [
  "namespace X;", //                                         1
  "public static class Fees", //                             2
  "{", //                                                    3
  "    public static bool Clean(decimal fee, decimal quoted)", // 4
  "        => fee == quoted;", //                            5
  "    public static decimal Short(decimal a, decimal b) => a - b;", // 6
  "}", //                                                    7
].join("\n");

function mutant(
  id,
  status,
  line,
  startColumn,
  endColumn,
  mutatorName,
  replacement,
) {
  return {
    id: String(id),
    mutatorName,
    replacement,
    status,
    location: {
      start: { line, column: startColumn },
      end: { line, column: endColumn },
    },
  };
}

// The shape Stryker's JSON reporter writes (mutation-testing-report-schema), cut down to what is read.
function report(
  mutants,
  file = "/home/ci/work/src/LeaseBook.Modules.Payments/Domain/Fees.cs",
) {
  return {
    schemaVersion: "2",
    projectRoot: "/home/ci/work/src/LeaseBook.Modules.Payments",
    files: { [file]: { language: "cs", source: SOURCE, mutants } },
  };
}

const FEES = "src/LeaseBook.Modules.Payments/Domain/Fees.cs";
const EQUALITY = mutant(
  1,
  "Survived",
  5,
  12,
  25,
  "Equality mutation",
  "fee != quoted",
);
const ARITHMETIC = mutant(
  2,
  "NoCoverage",
  6,
  58,
  63,
  "Arithmetic mutation",
  "a + b",
);

function equivalent(overrides = {}) {
  return {
    file: FEES,
    mutator: "Equality mutation",
    original: "=> fee == quoted;",
    replacement: "fee != quoted",
    reason: "Illustration only.",
    ...overrides,
  };
}

describe("parseArgs", () => {
  it("takes a module, a concurrency and any number of globs", () => {
    assert.deepEqual(
      parseArgs([
        "payments",
        "--concurrency",
        "4",
        "--mutate",
        "a",
        "--mutate",
        "b",
      ]),
      { module: "payments", mutate: ["a", "b"], concurrency: 4 },
    );
    assert.equal(parseArgs(["all"]).module, "all");
  });

  it("refuses what it cannot run", () => {
    assert.throws(() => parseArgs([]), /Name a module/);
    assert.throws(() => parseArgs(["banking"]), /Name a module/);
    assert.throws(
      () => parseArgs(["payments", "accounting"]),
      /One module only/,
    );
    assert.throws(
      () => parseArgs(["payments", "--concurrency", "0"]),
      /at least 1/,
    );
    assert.throws(
      () => parseArgs(["payments", "--concurrency"]),
      /needs a value/,
    );
    assert.throws(() => parseArgs(["payments", "--fast"]), /Unknown option/);
    assert.throws(
      () => parseArgs(["all", "--report", "r.json"]),
      /name the module/,
    );
    assert.throws(
      () => parseArgs(["payments", "--report", "r.json", "--concurrency", "2"]),
      /runs nothing/,
    );
  });
});

describe("text helpers", () => {
  it("compares lines with whitespace runs collapsed", () => {
    assert.equal(normalise("  a  ==\tb \n"), "a == b");
    assert.equal(normalise(undefined), "");
  });

  it("names a report file from src/ on, whatever the checkout path", () => {
    assert.equal(
      repoPath("/home/ci/work/src/LeaseBook.Modules.Payments/A.cs"),
      "src/LeaseBook.Modules.Payments/A.cs",
    );
    assert.equal(
      repoPath("C:\\work\\src\\LeaseBook.Modules.Accounting\\B.cs"),
      "src/LeaseBook.Modules.Accounting/B.cs",
    );
    assert.equal(
      repoPath("src/LeaseBook.Modules.Payments/A.cs"),
      "src/LeaseBook.Modules.Payments/A.cs",
    );
    assert.equal(repoPath("/r/other/C.cs", "/r"), "other/C.cs");
  });

  it("reads the span a location covers, on one line or several", () => {
    const lines = SOURCE.split("\n");
    assert.equal(spanOf(lines, EQUALITY.location), "fee == quoted");
    assert.equal(
      spanOf(lines, {
        start: { line: 4, column: 43 },
        end: { line: 5, column: 11 },
      }),
      "decimal quoted)\n        =>",
    );
  });

  it("shortens long text to the cell limit with an ellipsis", () => {
    assert.equal(shorten("short"), "short");
    const long = "x".repeat(CELL_LIMIT + 10);
    assert.equal(shorten(long).length, CELL_LIMIT);
    assert.ok(shorten(long).endsWith("…"));
  });
});

describe("summarise", () => {
  it("counts every status and scores the detected share of what compiled", () => {
    const summary = summarise(
      report([
        mutant(1, "Killed", 5, 12, 25, "Equality mutation", "fee != quoted"),
        mutant(2, "Killed", 6, 58, 63, "Arithmetic mutation", "a + b"),
        mutant(3, "Timeout", 6, 58, 63, "Arithmetic mutation", "a * b"),
        EQUALITY,
        ARITHMETIC,
        mutant(
          6,
          "CompileError",
          5,
          12,
          25,
          "Equality mutation",
          "fee > quoted",
        ),
        mutant(7, "Ignored", 5, 12, 25, "Equality mutation", "fee < quoted"),
        mutant(
          8,
          "RuntimeError",
          5,
          12,
          25,
          "Equality mutation",
          "fee >= quoted",
        ),
      ]),
    );
    assert.deepEqual(summary.counts, {
      Killed: 2,
      Survived: 1,
      Timeout: 1,
      NoCoverage: 1,
      CompileError: 1,
      Ignored: 1,
    });
    assert.equal(summary.other, 1);
    assert.equal(summary.total, 8);
    // (2 killed + 1 timeout) of (3 detected + 1 survived + 1 no coverage).
    assert.equal(summary.score, 60);
    assert.equal(summary.files, 1);
  });

  it("lists the survivors and the no-coverage mutants with their line, text and replacement", () => {
    const summary = summarise(report([ARITHMETIC, EQUALITY]));
    assert.deepEqual(
      summary.undetected.map(({ explained, ...rest }) => rest),
      [
        {
          file: FEES,
          line: "=> fee == quoted;",
          lineNumber: 5,
          mutator: "Equality mutation",
          original: "fee == quoted",
          replacement: "fee != quoted",
          status: "Survived",
        },
        {
          file: FEES,
          line: "public static decimal Short(decimal a, decimal b) => a - b;",
          lineNumber: 6,
          mutator: "Arithmetic mutation",
          original: "a - b",
          replacement: "a + b",
          status: "NoCoverage",
        },
      ],
    );
    assert.equal(summary.unexplained, 2);
  });

  it("counts a file as mutated only when it has a mutant, as Stryker lists excluded files with none", () => {
    const document = report([EQUALITY]);
    const configuration =
      "/home/ci/work/src/LeaseBook.Modules.Payments/Persistence/PaymentConfigurations.cs";
    document.files[configuration] = {
      language: "cs",
      source: SOURCE,
      mutants: [],
    };
    const entry = equivalent({
      file: "src/LeaseBook.Modules.Payments/Persistence/PaymentConfigurations.cs",
    });
    const summary = summarise(document, [entry]);
    assert.equal(summary.files, 1);
    assert.equal(summary.total, 1);
    assert.deepEqual(summary.stale, []);
  });

  it("has no score when nothing could be tested", () => {
    const summary = summarise(
      report([mutant(1, "CompileError", 5, 12, 25, "Equality mutation", "x")]),
    );
    assert.equal(summary.score, null);
  });

  it("refuses a report with no files", () => {
    assert.throws(() => summarise({ files: {} }), /lists no files/);
    assert.throws(() => summarise(undefined), /lists no files/);
  });

  it("explains a mutant by content, whatever its id, line number or indentation", () => {
    const moved = { ...EQUALITY, id: "99" };
    const summary = summarise(report([moved]), [
      equivalent({ original: "  =>   fee == quoted;  " }),
    ]);
    assert.equal(summary.unexplained, 0);
    assert.equal(summary.undetected[0].explained.reason, "Illustration only.");
    assert.deepEqual(summary.stale, []);
  });

  it("does not explain a mutant when the file, mutator, line or replacement differs", () => {
    for (const overrides of [
      { file: "src/LeaseBook.Modules.Payments/Domain/Other.cs" },
      { mutator: "Boolean mutation" },
      { original: "=> fee == other;" },
      { replacement: "fee > quoted" },
    ]) {
      const entry = equivalent(overrides);
      assert.equal(
        matches(entry, summarise(report([EQUALITY])).undetected[0]),
        false,
        JSON.stringify(overrides),
      );
      assert.equal(
        summarise(report([EQUALITY]), [entry]).unexplained,
        1,
        JSON.stringify(overrides),
      );
    }
  });

  it("calls an entry stale when it matches nothing in a file the run mutated, and skips other files", () => {
    const killed = mutant(
      1,
      "Killed",
      5,
      12,
      25,
      "Equality mutation",
      "fee != quoted",
    );
    const elsewhere = equivalent({
      file: "src/LeaseBook.Modules.Payments/Domain/Unmutated.cs",
    });
    const summary = summarise(report([killed]), [equivalent(), elsewhere]);
    assert.deepEqual(summary.stale, [equivalent()]);
  });
});

describe("the gate", () => {
  it("fails only a gated module with an unexplained undetected mutant", () => {
    const open = summarise(report([EQUALITY]));
    const explained = summarise(report([EQUALITY]), [equivalent()]);
    assert.equal(fails(open, false), false);
    assert.equal(fails(open, true), true);
    assert.equal(fails(explained, true), false);
    assert.equal(fails(summarise(report([ARITHMETIC])), true), true);
  });
});

describe("renderModule", () => {
  it("shows the score, the counts and a row per undetected mutant", () => {
    const markdown = renderModule(
      "Payments",
      summarise(report([EQUALITY, ARITHMETIC]), [equivalent()]),
      false,
    );
    assert.match(markdown, /^## Mutation testing: Payments/);
    assert.match(markdown, /Mutation score \*\*0\.0%\*\*/);
    assert.match(markdown, /Gate: off/);
    assert.match(
      markdown,
      /\| Killed \| Survived \| Timeout \| No coverage \| Compile error \| Ignored \| Total \|/,
    );
    assert.match(markdown, /\| 0 \| 1 \| 0 \| 1 \| 0 \| 0 \| 2 \|/);
    assert.match(
      markdown,
      /Undetected mutants: 2, of which 1 explained and 1 unexplained/,
    );
    assert.match(
      markdown,
      /\| Domain\/Fees\.cs \| 5 \| Equality mutation \| `fee == quoted` \| `fee != quoted` \| Survived \| yes \|/,
    );
    assert.match(
      markdown,
      /\| Domain\/Fees\.cs \| 6 \| Arithmetic mutation \| `a - b` \| `a \+ b` \| No coverage \| no \|/,
    );
  });

  it("keeps a cell inside its column and says when nothing survived", () => {
    const piped = mutant(
      1,
      "Survived",
      5,
      12,
      25,
      "Logical mutation",
      "a || `b`",
    );
    const markdown = renderModule("Payments", summarise(report([piped])), true);
    assert.match(markdown, /`a \\\|\\\| 'b'`/);
    assert.match(markdown, /Gate: \*\*on\*\*/);
    const clean = renderModule(
      "Payments",
      summarise(
        report([mutant(1, "Killed", 5, 12, 25, "Equality mutation", "x")]),
      ),
      true,
    );
    assert.match(clean, /No mutant survived\./);
  });

  it("lists stale entries", () => {
    const killed = mutant(
      1,
      "Killed",
      5,
      12,
      25,
      "Equality mutation",
      "fee != quoted",
    );
    const markdown = renderModule(
      "Payments",
      summarise(report([killed]), [equivalent()]),
      false,
    );
    assert.match(markdown, /Stale equivalents entries: 1/);
  });
});

describe("readEquivalents", () => {
  function write(document) {
    const file = path.join(directory(), "equivalents.json");
    writeFileSync(file, JSON.stringify(document));
    return file;
  }
  const valid = () => ({
    accounting: { gate: false, equivalents: [] },
    payments: { gate: true, equivalents: [equivalent()] },
  });

  it("reads a gate and a list per module", () => {
    assert.equal(readEquivalents(write(valid())).payments.gate, true);
  });

  it("refuses a missing module, a gate that is not a boolean, an entry with no reason, or an unknown module", () => {
    const missing = valid();
    delete missing.accounting;
    assert.throws(() => readEquivalents(write(missing)), /no "accounting"/);
    assert.throws(
      () =>
        readEquivalents(
          write({ ...valid(), payments: { gate: "no", equivalents: [] } }),
        ),
      /gate/,
    );
    assert.throws(
      () =>
        readEquivalents(
          write({
            ...valid(),
            payments: {
              gate: false,
              equivalents: [equivalent({ reason: " " })],
            },
          }),
        ),
      /reason/,
    );
    assert.throws(
      () =>
        readEquivalents(
          write({ ...valid(), banking: { gate: false, equivalents: [] } }),
        ),
      /not a module/,
    );
  });

  it("accepts the committed list, with every gate as the document says", () => {
    const document = readEquivalents();
    assert.deepEqual(Object.keys(document).sort(), Object.keys(MODULES).sort());
    assert.ok(EQUIVALENTS_FILE.startsWith("tests/mutation/"));
  });
});

describe("running Stryker", () => {
  function fakeRepo() {
    const repo = directory();
    for (const { testProject } of Object.values(MODULES)) {
      mkdirSync(path.join(repo, testProject), { recursive: true });
    }
    mkdirSync(path.join(repo, "tests", "mutation"), { recursive: true });
    writeFileSync(
      path.join(repo, EQUIVALENTS_FILE),
      JSON.stringify({
        accounting: { gate: false, equivalents: [] },
        payments: { gate: false, equivalents: [] },
      }),
    );
    return repo;
  }

  // Stands in for `dotnet stryker`: records the call and writes the report the real one would.
  function spawner(
    calls,
    { status = 0, write = true, mutants = [EQUALITY] } = {},
  ) {
    return (command, args, cwd) => {
      calls.push({ command, args, cwd });
      if (write) {
        const output = args[args.indexOf("--output") + 1];
        mkdirSync(path.join(output, "reports"), { recursive: true });
        writeFileSync(
          path.join(output, "reports", "mutation-report.json"),
          JSON.stringify(report(mutants)),
        );
      }
      return status;
    };
  }

  it("passes the concurrency and the globs through, from the module's test project", () => {
    assert.deepEqual(
      strykerArgs("/out", { concurrency: 2, mutate: ["a", "b"] }),
      [
        "stryker",
        "--output",
        "/out",
        "--concurrency",
        "2",
        "--mutate",
        "a",
        "--mutate",
        "b",
      ],
    );
    const repo = fakeRepo();
    const calls = [];
    const file = runStryker("payments", { mutate: [] }, spawner(calls), repo);
    assert.equal(
      file,
      path.join(
        repo,
        "StrykerOutput",
        "payments",
        "reports",
        "mutation-report.json",
      ),
    );
    assert.equal(calls[0].command, "dotnet");
    assert.equal(
      calls[0].cwd,
      path.join(repo, "tests/LeaseBook.Tests.Payments"),
    );
  });

  it("removes an old report before running, so it cannot pass for a new one", () => {
    const repo = fakeRepo();
    const old = path.join(repo, "StrykerOutput", "payments", "reports");
    mkdirSync(old, { recursive: true });
    writeFileSync(path.join(old, "mutation-report.json"), "{}");
    assert.throws(
      () =>
        runStryker(
          "payments",
          { mutate: [] },
          spawner([], { write: false }),
          repo,
        ),
      /wrote no report/,
    );
  });

  it("fails when Stryker fails", () => {
    assert.throws(
      () =>
        runStryker(
          "payments",
          { mutate: [] },
          spawner([], { status: 3 }),
          fakeRepo(),
        ),
      /exit 3/,
    );
  });

  it("reports both modules and exits 0 with every gate off", () => {
    const repo = fakeRepo();
    const calls = [];
    const logged = [];
    const outMd = path.join(repo, "out", "summary.md");
    const code = run(["all", "--concurrency", "4", "--out-md", outMd], {
      spawn: spawner(calls),
      log: (text) => logged.push(text),
      repo,
    });
    assert.equal(code, 0);
    assert.equal(calls.length, 2);
    assert.match(
      logged[0],
      /Mutation testing: Accounting[\s\S]*Mutation testing: Payments/,
    );
    assert.equal(readFileSync(outMd, "utf8"), `${logged[0]}\n`);
  });

  it("exits 1 when a gated module has an unexplained survivor, and 0 once it is explained", () => {
    const repo = fakeRepo();
    const gated = (equivalents) => {
      writeFileSync(
        path.join(repo, EQUIVALENTS_FILE),
        JSON.stringify({
          accounting: { gate: false, equivalents: [] },
          payments: { gate: true, equivalents },
        }),
      );
      return run(["payments"], { spawn: spawner([]), log: () => {}, repo });
    };
    assert.equal(gated([]), 1);
    assert.equal(gated([equivalent()]), 0);
  });

  it("exits 1 and says so when Stryker fails, still summarising the other module", () => {
    const repo = fakeRepo();
    const logged = [];
    let first = true;
    const flaky = (command, args, cwd) => {
      if (first) {
        first = false;
        return 1;
      }
      return spawner([])(command, args, cwd);
    };
    assert.equal(
      run(["all"], { spawn: flaky, log: (t) => logged.push(t), repo }),
      1,
    );
    assert.match(
      logged[0],
      /Accounting\n\n\*\*Not measured\.\*\* Stryker failed/,
    );
    assert.match(logged[0], /Mutation testing: Payments/);
  });

  it("summarises an existing report without running anything", () => {
    const repo = fakeRepo();
    const file = path.join(repo, "report.json");
    writeFileSync(file, JSON.stringify(report([EQUALITY])));
    const logged = [];
    const code = run(["payments", "--report", file], {
      spawn: () => assert.fail("nothing runs"),
      log: (t) => logged.push(t),
      repo,
    });
    assert.equal(code, 0);
    assert.match(logged[0], /Undetected mutants: 1/);
  });
});
