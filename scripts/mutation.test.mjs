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
  DEFAULT_CONFIG_FILE,
  EQUIVALENTS_FILE,
  configFileOf,
  configMutate,
  MODULES,
  fails,
  matches,
  normalise,
  parseArgs,
  partCount,
  partGlobs,
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

/** Every module with its gate off and no entries: the smallest equivalents file the runner accepts. */
function ungated(overrides = {}) {
  return {
    ...Object.fromEntries(
      Object.keys(MODULES).map((key) => [
        key,
        { gate: false, equivalents: [] },
      ]),
    ),
    ...overrides,
  };
}

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
      () => parseArgs(["all", "--part", "1"]),
      /not split into parts/,
    );
    assert.throws(
      () => parseArgs(["tenancy-web", "--part", "1"]),
      /tenancy-web is not split into parts/,
    );
    assert.throws(
      () => parseArgs(["payments", "--part", "3"]),
      /from 1 to 2 for payments/,
    );
    assert.throws(
      () => parseArgs(["accounting", "--part", "0"]),
      /from 1 to 4 for accounting/,
    );
    assert.throws(
      () => parseArgs(["accounting", "--part", "1.5"]),
      /whole number/,
    );
    assert.throws(
      () => parseArgs(["accounting", "--part", "1", "--mutate", "a"]),
      /no --mutate/,
    );
    assert.throws(
      () => parseArgs(["accounting", "--report", "r.json", "--part", "1"]),
      /no Stryker options/,
    );
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

describe("modules and their Stryker configs", () => {
  const PROJECTS = {
    accounting: "LeaseBook.Modules.Accounting.csproj",
    payments: "LeaseBook.Modules.Payments.csproj",
    "tenancy-kernel": "LeaseBook.SharedKernel.csproj",
    "tenancy-web": "LeaseBook.Web.csproj",
  };

  it("mutates these modules and names each one in the module error", () => {
    assert.deepEqual(Object.keys(MODULES).sort(), Object.keys(PROJECTS).sort());
    assert.throws(
      () => parseArgs(["banking"]),
      /Name a module: accounting, payments, tenancy-kernel, tenancy-web, all\./,
    );
    assert.equal(parseArgs(["tenancy-kernel"]).module, "tenancy-kernel");
    assert.equal(parseArgs(["tenancy-web"]).module, "tenancy-web");
  });

  for (const [key, project] of Object.entries(PROJECTS)) {
    it(`finds ${key}'s committed config, which mutates ${project}`, () => {
      const file = path.join(MODULES[key].testProject, configFileOf(key));
      const config = JSON.parse(readFileSync(file, "utf8"))["stryker-config"];
      assert.equal(config.project, project);
      assert.equal(config["test-runner"], "mtp");
      assert.equal(config["coverage-analysis"], "all");
      assert.equal(config["timeout-ratio"], 4);
      assert.equal(config.thresholds.break, 0);
      assert.deepEqual(configMutate(key), config.mutate);
    });
  }

  it("gives the two tenancy modules one test project and a config each", () => {
    const kernel = MODULES["tenancy-kernel"];
    const web = MODULES["tenancy-web"];
    assert.equal(kernel.testProject, "tests/LeaseBook.Tests.Tenancy");
    assert.equal(web.testProject, kernel.testProject);
    assert.equal(configFileOf("tenancy-kernel"), "stryker-config.kernel.json");
    assert.equal(configFileOf("tenancy-web"), "stryker-config.web.json");
    assert.equal(kernel.parts, undefined);
    assert.equal(web.parts, undefined);
  });

  it("mutates only the Tenancy directory of each tenancy project, without the EF entity and its mapping", () => {
    assert.deepEqual(configMutate("tenancy-kernel"), [
      "**/Tenancy/**/*.cs",
      "!**/obj/**",
    ]);
    assert.deepEqual(configMutate("tenancy-web"), [
      "**/Tenancy/**/*.cs",
      "!**/obj/**",
      "!**/Tenancy/AuditEvent.cs",
      "!**/Tenancy/AuditEventConfiguration.cs",
    ]);
  });

  it("uses stryker-config.json for a module that names no config", () => {
    assert.equal(configFileOf("accounting"), DEFAULT_CONFIG_FILE);
    assert.equal(configFileOf("payments"), "stryker-config.json");
  });
});

describe("parts of a split module", () => {
  const CONFIG = ["**/*.cs", "!**/obj/**", "!**/Endpoints/**/*.cs"];

  const PARTS = [["Features/Posting", "Ledgers"], ["Banking"]];

  it("takes a part of a module that is split", () => {
    assert.equal(parseArgs(["accounting", "--part", "4"]).part, 4);
    assert.equal(parseArgs(["payments", "--part", "1"]).part, 1);
  });

  it("counts one part per named group and one for the rest", () => {
    assert.equal(partCount("accounting"), 4);
    assert.equal(partCount("payments"), 2);
  });

  it("mutates a named part's directories, without what the config excludes", () => {
    assert.deepEqual(partGlobs(CONFIG, PARTS, 1), [
      "**/Features/Posting/**/*.cs",
      "**/Ledgers/**/*.cs",
      "!**/obj/**",
      "!**/Endpoints/**/*.cs",
    ]);
    assert.deepEqual(partGlobs(CONFIG, PARTS, 2), [
      "**/Banking/**/*.cs",
      "!**/obj/**",
      "!**/Endpoints/**/*.cs",
    ]);
  });

  it("mutates everything else the config does in the last part", () => {
    assert.deepEqual(partGlobs(CONFIG, PARTS, 3), [
      "**/*.cs",
      "!**/obj/**",
      "!**/Endpoints/**/*.cs",
      "!**/Features/Posting/**/*.cs",
      "!**/Ledgers/**/*.cs",
      "!**/Banking/**/*.cs",
    ]);
  });

  for (const key of ["accounting", "payments"]) {
    it(`splits ${key} into directories its committed config still mutates`, () => {
      const config = configMutate(key);
      assert.ok(config.includes("**/*.cs"));
      assert.ok(MODULES[key].parts.length > 0);
      for (const directories of MODULES[key].parts) {
        assert.ok(directories.length > 0);
        for (const directory of directories) {
          assert.ok(
            !config.some(
              (glob) => glob.startsWith("!") && glob.includes(directory),
            ),
            `${directory} is excluded by the config, so its part would mutate nothing there`,
          );
        }
      }
    });

    it(`names no ${key} directory in two parts`, () => {
      // A directory's glob also matches any directory below another of the same name, so a name in
      // one part that ends another part's name would mutate those files twice.
      const named = MODULES[key].parts.flatMap((directories, part) =>
        directories.map((directory) => ({ directory, part })),
      );
      for (const a of named) {
        for (const b of named) {
          if (a.part === b.part) continue;
          assert.ok(
            a.directory !== b.directory &&
              !a.directory.endsWith(`/${b.directory}`),
            `${a.directory} (part ${a.part + 1}) overlaps ${b.directory} (part ${b.part + 1})`,
          );
        }
      }
    });
  }
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

  it("explains a mutant by the whole span it replaces, as well as by the line it starts on", () => {
    // A block removal over the class body: its first line alone is a bare "{".
    const block = mutant(
      9,
      "Survived",
      3,
      1,
      2,
      "Block removal mutation",
      "{}",
    );
    block.location.end.line = 7;
    const blockEntry = (original) =>
      equivalent({
        mutator: "Block removal mutation",
        original,
        replacement: "{}",
      });
    const [undetected] = summarise(report([block])).undetected;
    assert.equal(matches(blockEntry("{"), undetected), true);
    assert.equal(
      matches(
        blockEntry(
          "{ public static bool Clean(decimal fee, decimal quoted) => fee == quoted; public static decimal Short(decimal a, decimal b) => a - b; }",
        ),
        undetected,
      ),
      true,
    );
    assert.equal(matches(blockEntry("{ return ids; }"), undetected), false);
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
  const valid = () =>
    ungated({ payments: { gate: true, equivalents: [equivalent()] } });

  it("reads a gate and a list per module", () => {
    assert.equal(readEquivalents(write(valid())).payments.gate, true);
  });

  it("refuses a missing module, a gate that is not a boolean, an entry with no reason, or an unknown module", () => {
    const missing = valid();
    delete missing.accounting;
    assert.throws(() => readEquivalents(write(missing)), /no "accounting"/);
    const noTenancy = valid();
    delete noTenancy["tenancy-web"];
    assert.throws(() => readEquivalents(write(noTenancy)), /no "tenancy-web"/);
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
    for (const key of Object.keys(MODULES)) {
      const { testProject } = MODULES[key];
      mkdirSync(path.join(repo, testProject), { recursive: true });
      writeFileSync(
        path.join(repo, testProject, configFileOf(key)),
        JSON.stringify({
          "stryker-config": { mutate: [`**/${key}/*.cs`, "!**/obj/**"] },
        }),
      );
    }
    mkdirSync(path.join(repo, "tests", "mutation"), { recursive: true });
    writeFileSync(path.join(repo, EQUIVALENTS_FILE), JSON.stringify(ungated()));
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
        "--config-file",
        "stryker-config.json",
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
    const { args } = calls[0];
    assert.equal(
      args[args.indexOf("--config-file") + 1],
      "stryker-config.json",
    );
  });

  it("runs each tenancy module from the shared test project with its own config and output", () => {
    const repo = fakeRepo();
    const calls = [];
    for (const key of ["tenancy-kernel", "tenancy-web"]) {
      const file = runStryker(key, { mutate: [] }, spawner(calls), repo);
      assert.equal(
        file,
        path.join(
          repo,
          "StrykerOutput",
          key,
          "reports",
          "mutation-report.json",
        ),
      );
    }
    assert.deepEqual(
      calls.map(({ args, cwd }) => ({
        cwd,
        config: args[args.indexOf("--config-file") + 1],
        output: args[args.indexOf("--output") + 1],
      })),
      [
        {
          cwd: path.join(repo, "tests/LeaseBook.Tests.Tenancy"),
          config: "stryker-config.kernel.json",
          output: path.join(repo, "StrykerOutput", "tenancy-kernel"),
        },
        {
          cwd: path.join(repo, "tests/LeaseBook.Tests.Tenancy"),
          config: "stryker-config.web.json",
          output: path.join(repo, "StrykerOutput", "tenancy-web"),
        },
      ],
    );
  });

  it("reads a part's globs from the module's own config file", () => {
    const repo = fakeRepo();
    assert.deepEqual(configMutate("tenancy-web", repo), [
      "**/tenancy-web/*.cs",
      "!**/obj/**",
    ]);
    assert.deepEqual(configMutate("tenancy-kernel", repo), [
      "**/tenancy-kernel/*.cs",
      "!**/obj/**",
    ]);
  });

  it("runs a part with its own globs and output, and names it in the summary", () => {
    const repo = fakeRepo();
    const calls = [];
    const logged = [];
    const code = run(["accounting", "--part", "4"], {
      spawn: spawner(calls),
      log: (text) => logged.push(text),
      repo,
    });
    assert.equal(code, 0);
    const { args } = calls[0];
    assert.equal(
      args[args.indexOf("--output") + 1],
      path.join(repo, "StrykerOutput", "accounting-4"),
    );
    const globs = args.filter((_, i) => args[i - 1] === "--mutate");
    assert.deepEqual(
      globs,
      partGlobs(
        ["**/accounting/*.cs", "!**/obj/**"],
        MODULES.accounting.parts,
        4,
      ),
    );
    assert.match(logged[0], /Mutation testing: Accounting, part 4 of 4/);
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

  it("reports every module and exits 0 with every gate off", () => {
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
    assert.equal(calls.length, 4);
    assert.match(
      logged[0],
      /Mutation testing: Accounting[\s\S]*Mutation testing: Payments[\s\S]*Mutation testing: Tenancy \(SharedKernel\)[\s\S]*Mutation testing: Tenancy \(Web\)/,
    );
    assert.equal(readFileSync(outMd, "utf8"), `${logged[0]}\n`);
  });

  it("exits 1 when a gated module has an unexplained survivor, and 0 once it is explained", () => {
    const repo = fakeRepo();
    const gated = (equivalents) => {
      writeFileSync(
        path.join(repo, EQUIVALENTS_FILE),
        JSON.stringify(ungated({ payments: { gate: true, equivalents } })),
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
