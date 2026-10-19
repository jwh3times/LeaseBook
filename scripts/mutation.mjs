#!/usr/bin/env node

/**
 * Runs mutation testing on a module and reports what survived (ADR-055 addendum,
 * docs/testing/mutation.md). Stryker.NET changes the module's code one small edit at a time (a
 * mutant) and reruns the module's fast test project against each. A mutant the tests notice is
 * killed; one they do not notice survived, and is either a gap in the tests or an edit that changes
 * nothing observable (an equivalent mutant).
 *
 * Usage:
 *
 *   node scripts/mutation.mjs <accounting|payments|all> [--concurrency <n>] [--mutate <glob>]...
 *                             [--out-md <file>]
 *   node scripts/mutation.mjs <accounting|payments> --part <1|2> [--concurrency <n>] [--out-md <file>]
 *   node scripts/mutation.mjs <accounting|payments> --report <mutation-report.json> [--out-md <file>]
 *
 * The first form runs Stryker from the module's test project, where its `stryker-config.json` is,
 * writing to `StrykerOutput/<module>/`, then reads the JSON report. `--concurrency` and `--mutate`
 * pass through to Stryker. A `--mutate` given here replaces the config file's list rather than adding
 * to it, so it names the files to mutate: docs/testing/mutation.md has examples. The second form reads
 * a report a run already wrote and runs nothing.
 *
 * `--part` runs half of a module that is split in two: part 1 mutates the directories its entry in
 * MODULES names, and part 2 everything else the config file mutates. CI runs both modules that way,
 * one job per part, because a whole run of either comes too near a hosted job's time limit.
 *
 * The markdown summary goes to standard output, and also to `--out-md` when given. CI appends it to
 * the job summary.
 *
 * The exit code is non-zero when Stryker fails or writes no report, and when a module whose gate is
 * on has an undetected mutant that the equivalents list does not explain. With every gate off, as it
 * ships, the run only reports. The gates and the equivalents list are in
 * `tests/mutation/equivalent-mutants.json`.
 */

import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const REPO = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

/** The modules mutated, each against its own fast test project, where its Stryker config lives. */
export const MODULES = {
  accounting: {
    name: "Accounting",
    testProject: "tests/LeaseBook.Tests.Accounting",
    // Split in two for CI: a whole run took 296 minutes on a hosted runner, against GitHub's 360-minute
    // limit for a job (docs/testing/mutation.md). Part 1 is these directories; part 2 is the rest of
    // the module, so a directory added later falls into part 2 and is never left out. Banking and
    // Reconciliation moved to part 2 on 2026-10-18, when part 1 took 4:22 to part 2's 3:00.
    firstPart: [
      "Features/LedgerPosting",
      "Features/Posting",
      "Features/Statements",
    ],
  },
  payments: {
    name: "Payments",
    testProject: "tests/LeaseBook.Tests.Payments",
    // Split in two for CI: once every mutant had a test, a whole run took 142 minutes locally, which
    // a hosted runner, 1.2 to 1.75 times slower, could stretch past four hours. Part 1 is the two
    // engines, 864 of the 1,369 mutants that run tested; part 2 is the rest of the module.
    firstPart: ["Processing"],
  },
};

export const EQUIVALENTS_FILE = "tests/mutation/equivalent-mutants.json";

/** Stryker's statuses, in the order the summary shows them, with the label it uses. */
export const STATUSES = [
  ["Killed", "Killed"],
  ["Survived", "Survived"],
  ["Timeout", "Timeout"],
  ["NoCoverage", "No coverage"],
  ["CompileError", "Compile error"],
  ["Ignored", "Ignored"],
];

/** Undetected: no test failed. A no-coverage mutant is a survivor that no test even ran. */
export const UNDETECTED = new Set(["Survived", "NoCoverage"]);

/** The longest original or replacement text a table cell shows before it is shortened. */
export const CELL_LIMIT = 60;

/** The most rows the survivor table lists; the HTML report has the rest. */
export const ROW_LIMIT = 1500;

export function parseArgs(argv) {
  const options = { module: undefined, mutate: [] };
  for (let i = 0; i < argv.length; i++) {
    const arg = argv[i];
    const value = () => {
      const next = argv[++i];
      if (next === undefined || next.startsWith("--")) {
        throw new Error(`${arg} needs a value.`);
      }
      return next;
    };
    switch (arg) {
      case "--concurrency": {
        const n = Number(value());
        if (!Number.isInteger(n) || n < 1) {
          throw new Error("--concurrency is a whole number of at least 1.");
        }
        options.concurrency = n;
        break;
      }
      case "--mutate":
        options.mutate.push(value());
        break;
      case "--part": {
        const part = Number(value());
        if (part !== 1 && part !== 2) throw new Error("--part is 1 or 2.");
        options.part = part;
        break;
      }
      case "--report":
        options.report = value();
        break;
      case "--out-md":
        options.outMd = value();
        break;
      default:
        if (arg.startsWith("--")) throw new Error(`Unknown option ${arg}.`);
        if (options.module !== undefined) {
          throw new Error(`One module only, not ${options.module} and ${arg}.`);
        }
        options.module = arg;
    }
  }
  if (options.module !== "all" && !(options.module in MODULES)) {
    throw new Error(
      `Name a module: ${[...Object.keys(MODULES), "all"].join(", ")}.`,
    );
  }
  if (options.report !== undefined && options.module === "all") {
    throw new Error("--report reads one module's report; name the module.");
  }
  if (
    options.part !== undefined &&
    MODULES[options.module]?.firstPart === undefined
  ) {
    throw new Error(`${options.module} is not split into parts.`);
  }
  if (options.part !== undefined && options.mutate.length > 0) {
    throw new Error("--part chooses what to mutate, so it takes no --mutate.");
  }
  if (
    options.report !== undefined &&
    (options.concurrency !== undefined ||
      options.mutate.length > 0 ||
      options.part !== undefined)
  ) {
    throw new Error("--report runs nothing, so it takes no Stryker options.");
  }
  return options;
}

/** Whitespace runs as one space, ends trimmed: how lines and replacements are compared. */
export function normalise(text) {
  return String(text ?? "")
    .replace(/\s+/g, " ")
    .trim();
}

/** A report path as the repository names it: forward slashes, from `src/` on when it has one. */
export function repoPath(file, projectRoot = REPO) {
  const forward = file.replaceAll("\\", "/");
  const index = forward.lastIndexOf("/src/");
  if (index >= 0) return forward.slice(index + 1);
  if (forward.startsWith("src/")) return forward;
  const root = projectRoot.replaceAll("\\", "/").replace(/\/?$/, "/");
  return forward.startsWith(root) ? forward.slice(root.length) : forward;
}

function lineOf(lines, number) {
  return lines[number - 1] ?? "";
}

/** The source text a location spans. Stryker's lines and columns count from 1; the end is exclusive. */
export function spanOf(lines, location) {
  const { start, end } = location;
  if (start.line === end.line) {
    return lineOf(lines, start.line).slice(start.column - 1, end.column - 1);
  }
  const parts = [lineOf(lines, start.line).slice(start.column - 1)];
  for (let n = start.line + 1; n < end.line; n++) parts.push(lineOf(lines, n));
  parts.push(lineOf(lines, end.line).slice(0, end.column - 1));
  return parts.join("\n");
}

/**
 * Whether an equivalents entry explains a mutant. Matched on content and never on Stryker's mutant
 * id or line number, which both shift whenever the file changes: the file, the mutator, the trimmed
 * text of the line the mutant starts on, and the replacement. The entry's text may instead be the
 * whole span the mutant replaces, for a mutant whose first line alone, such as a bare `{`, would
 * name every block in the file.
 */
export function matches(entry, mutant) {
  const original = normalise(entry.original);
  return (
    entry.file === mutant.file &&
    entry.mutator === mutant.mutator &&
    (original === normalise(mutant.line) ||
      original === normalise(mutant.original)) &&
    normalise(entry.replacement) === normalise(mutant.replacement)
  );
}

/**
 * One module's figures from a Stryker JSON report (mutation-testing-report-schema), with each
 * undetected mutant marked explained or not, and the equivalents entries that matched nothing.
 */
export function summarise(report, equivalents = []) {
  const counts = Object.fromEntries(STATUSES.map(([status]) => [status, 0]));
  let other = 0;
  const undetected = [];
  const files = new Set();
  const listed = Object.entries(report?.files ?? {});
  for (const [file, entry] of listed) {
    const relative = repoPath(file, report.projectRoot);
    const lines = String(entry.source ?? "").split(/\r?\n/);
    for (const mutant of entry.mutants ?? []) {
      // Counted only with a mutant: Stryker also lists the files the config excludes, with none.
      files.add(relative);
      if (mutant.status in counts) counts[mutant.status] += 1;
      else other += 1;
      if (!UNDETECTED.has(mutant.status)) continue;
      undetected.push({
        file: relative,
        line: lineOf(lines, mutant.location.start.line).trim(),
        lineNumber: mutant.location.start.line,
        mutator: mutant.mutatorName,
        original: spanOf(lines, mutant.location),
        replacement: mutant.replacement ?? "",
        status: mutant.status,
      });
    }
  }
  if (listed.length === 0) {
    throw new Error("The Stryker report lists no files.");
  }
  undetected.sort(
    (a, b) =>
      a.file.localeCompare(b.file) ||
      a.lineNumber - b.lineNumber ||
      a.mutator.localeCompare(b.mutator),
  );
  const used = new Set();
  for (const mutant of undetected) {
    const index = equivalents.findIndex((entry) => matches(entry, mutant));
    mutant.explained = index >= 0 ? equivalents[index] : undefined;
    if (index >= 0) used.add(index);
  }
  // An entry for a file this run did not mutate is not checked: a run limited with --mutate would
  // otherwise call every other file's entries stale.
  const stale = equivalents.filter(
    (entry, index) => files.has(entry.file) && !used.has(index),
  );
  const detected = counts.Killed + counts.Timeout;
  const valid = detected + counts.Survived + counts.NoCoverage;
  return {
    counts,
    other,
    total: Object.values(counts).reduce((a, b) => a + b, 0) + other,
    score: valid === 0 ? null : (100 * detected) / valid,
    undetected,
    unexplained: undetected.filter((m) => !m.explained).length,
    stale,
    files: files.size,
  };
}

/** Whether a module fails the run: its gate is on and something undetected is unexplained. */
export function fails(summary, gate) {
  return gate === true && summary.unexplained > 0;
}

export function shorten(text, limit = CELL_LIMIT) {
  const flat = normalise(text);
  return flat.length <= limit ? flat : `${flat.slice(0, limit - 1)}…`;
}

// Inside a table cell, as code: pipes would end the cell and backticks the code span.
function code(text) {
  const shown = shorten(text).replaceAll("|", "\\|").replaceAll("`", "'");
  return shown === "" ? "(empty)" : `\`${shown}\``;
}

function percent(score) {
  return score === null ? "n/a" : `${score.toFixed(1)}%`;
}

/** One module's section of the summary. */
export function renderModule(name, summary, gate) {
  const out = [];
  out.push(`## Mutation testing: ${name}`, "");
  out.push(
    `Mutation score **${percent(summary.score)}**: the mutants a test noticed (killed or timed out), of every mutant that compiled and was not ignored. ${summary.files} files mutated.`,
    "",
  );
  out.push(
    gate
      ? "Gate: **on**. An undetected mutant the equivalents list does not explain fails this run."
      : "Gate: off. This run reports and does not fail on what survived.",
    "",
  );
  out.push(
    `| ${STATUSES.map(([, label]) => label).join(" | ")} | Total |`,
    `| ${STATUSES.map(() => "---:").join(" | ")} | ---: |`,
    `| ${STATUSES.map(([status]) => summary.counts[status]).join(" | ")} | ${summary.total} |`,
    "",
  );
  if (summary.other > 0) {
    out.push(
      `${summary.other} mutants had another status (for example a runtime error) and are counted in the total only.`,
      "",
    );
  }
  const explained = summary.undetected.length - summary.unexplained;
  if (summary.undetected.length === 0) {
    out.push("No mutant survived.", "");
  } else {
    out.push(
      `### Undetected mutants: ${summary.undetected.length}, of which ${explained} explained and ${summary.unexplained} unexplained`,
      "",
      "Survived means the tests ran the mutated code and none failed; no coverage means no test ran it.",
      "An explained mutant matches an entry in the equivalents list.",
      "",
      "| File | Line | Mutator | Original | Replacement | Status | Explained |",
      "| --- | ---: | --- | --- | --- | --- | --- |",
    );
    for (const m of summary.undetected.slice(0, ROW_LIMIT)) {
      const label = m.status === "NoCoverage" ? "No coverage" : m.status;
      out.push(
        `| ${m.file.replace(/^src\/LeaseBook\.Modules\.[^/]+\//, "")} | ${m.lineNumber} | ${m.mutator} | ${code(m.original)} | ${code(m.replacement)} | ${label} | ${m.explained ? "yes" : "no"} |`,
      );
    }
    if (summary.undetected.length > ROW_LIMIT) {
      out.push(
        "",
        `${summary.undetected.length - ROW_LIMIT} more are in the HTML report.`,
      );
    }
    out.push("");
  }
  if (summary.stale.length > 0) {
    out.push(
      `### Stale equivalents entries: ${summary.stale.length}`,
      "",
      "These entries match no undetected mutant in a file this run mutated. The line changed, or a test now kills the mutant; remove or update each one.",
      "",
    );
    for (const entry of summary.stale) {
      out.push(
        `- ${entry.file}: ${entry.mutator}, ${code(entry.original)} to ${code(entry.replacement)}`,
      );
    }
    out.push("");
  }
  return out.join("\n");
}

/** Reads and checks the committed equivalents file: a gate and a list of entries per module. */
export function readEquivalents(file = path.join(REPO, EQUIVALENTS_FILE)) {
  const document = JSON.parse(readFileSync(file, "utf8"));
  for (const key of Object.keys(MODULES)) {
    const entry = document[key];
    if (entry === undefined) throw new Error(`${file} has no "${key}" entry.`);
    if (typeof entry.gate !== "boolean") {
      throw new Error(`${file}: "${key}.gate" is true or false.`);
    }
    if (!Array.isArray(entry.equivalents)) {
      throw new Error(`${file}: "${key}.equivalents" is a list.`);
    }
    for (const [i, item] of entry.equivalents.entries()) {
      for (const field of [
        "file",
        "mutator",
        "original",
        "replacement",
        "reason",
      ]) {
        if (
          typeof item[field] !== "string" ||
          (field !== "replacement" && item[field].trim() === "")
        ) {
          throw new Error(
            `${file}: "${key}.equivalents[${i}].${field}" is missing.`,
          );
        }
      }
    }
  }
  for (const key of Object.keys(document)) {
    if (!(key in MODULES))
      throw new Error(`${file}: "${key}" is not a module.`);
  }
  return document;
}

/**
 * The `mutate` list for one part of a split module, from the config file's list. Part 1 mutates the
 * named directories, still without anything the config excludes; part 2 is the config's list with
 * those directories excluded. Together they mutate what the config alone does.
 */
export function partGlobs(configMutate, firstPart, part) {
  const directories = firstPart.map((directory) => `**/${directory}/**/*.cs`);
  if (part === 1) {
    return [
      ...directories,
      ...configMutate.filter((glob) => glob.startsWith("!")),
    ];
  }
  return [...configMutate, ...directories.map((glob) => `!${glob}`)];
}

/** The `mutate` list in a module's stryker-config.json. */
export function configMutate(key, repo = REPO) {
  const file = path.join(repo, MODULES[key].testProject, "stryker-config.json");
  const mutate = JSON.parse(readFileSync(file, "utf8"))["stryker-config"]
    ?.mutate;
  if (!Array.isArray(mutate) || mutate.length === 0) {
    throw new Error(`${file} has no mutate list.`);
  }
  return mutate;
}

/** Where a run writes, and how the summary names it: the module, or the module and its part. */
export function runName(key, part) {
  return part === undefined ? key : `${key}-${part}`;
}

/** The arguments Stryker is run with, from the module's test project directory. */
export function strykerArgs(output, options) {
  const args = ["stryker", "--output", output];
  if (options.concurrency !== undefined) {
    args.push("--concurrency", String(options.concurrency));
  }
  for (const glob of options.mutate) args.push("--mutate", glob);
  return args;
}

function defaultSpawn(command, args, cwd) {
  return spawnSync(command, args, { cwd, stdio: "inherit" }).status;
}

/**
 * Runs Stryker for one module and returns the path of its JSON report, or throws when Stryker failed
 * or wrote none. The output directory is emptied first, so an old report cannot pass for a new one.
 */
export function runStryker(key, options, spawn = defaultSpawn, repo = REPO) {
  const output = path.join(repo, "StrykerOutput", runName(key, options.part));
  rmSync(output, { recursive: true, force: true });
  mkdirSync(output, { recursive: true });
  const mutate =
    options.part === undefined
      ? options.mutate
      : partGlobs(
          configMutate(key, repo),
          MODULES[key].firstPart,
          options.part,
        );
  const status = spawn(
    "dotnet",
    strykerArgs(output, { ...options, mutate }),
    path.join(repo, MODULES[key].testProject),
  );
  if (status !== 0) {
    throw new Error(
      `Stryker failed for ${MODULES[key].name} (exit ${status}).`,
    );
  }
  const report = path.join(output, "reports", "mutation-report.json");
  if (!existsSync(report)) {
    throw new Error(
      `Stryker wrote no report for ${MODULES[key].name} at ${report}.`,
    );
  }
  return report;
}

/** The whole run: returns the exit code. `spawn` and `log` are replaceable for the tests. */
export function run(
  argv,
  {
    spawn = defaultSpawn,
    log = console.log,
    repo = REPO,
    equivalentsFile,
  } = {},
) {
  const options = parseArgs(argv);
  const document = readEquivalents(
    equivalentsFile ?? path.join(repo, EQUIVALENTS_FILE),
  );
  const keys =
    options.module === "all" ? Object.keys(MODULES) : [options.module];
  const sections = [];
  let code = 0;
  for (const key of keys) {
    const name =
      options.part === undefined
        ? MODULES[key].name
        : `${MODULES[key].name}, part ${options.part} of 2`;
    const { gate, equivalents } = document[key];
    let summary;
    try {
      const reportFile =
        options.report ?? runStryker(key, options, spawn, repo);
      summary = summarise(
        JSON.parse(readFileSync(reportFile, "utf8")),
        equivalents,
      );
    } catch (error) {
      sections.push(
        `## Mutation testing: ${name}`,
        "",
        `**Not measured.** ${error.message}`,
        "",
      );
      code = 1;
      continue;
    }
    sections.push(renderModule(name, summary, gate));
    if (fails(summary, gate)) code = 1;
  }
  const markdown = sections.join("\n");
  log(markdown);
  if (options.outMd) {
    mkdirSync(path.dirname(path.resolve(options.outMd)), { recursive: true });
    writeFileSync(options.outMd, `${markdown}\n`);
  }
  return code;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  try {
    process.exitCode = run(process.argv.slice(2));
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
