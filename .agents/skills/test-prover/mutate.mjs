#!/usr/bin/env node
// Runs a branch's tests against a broken copy of its fix, in a scratch worktree, for the
// test-prover skill. It edits only files inside a scratch worktree, restores each file's original
// bytes after every run, and verifies them; the checkout it was set up from is never written.
//
//   node .agents/skills/test-prover/mutate.mjs setup    --repo <checkout> [--link <dir> ...]
//   node .agents/skills/test-prover/mutate.mjs run      --root <scratch> --command "<test command>" [run options]
//   node .agents/skills/test-prover/mutate.mjs edit     --root <scratch> (--file <path> --search <text> --replace <text> | --edit <json file>)
//                                                       [--occurrence <n>] --command "<test command>" [run options]
//   node .agents/skills/test-prover/mutate.mjs revert   --root <scratch> --base <ref> --file <path> ... --command "<test command>" [run options]
//   node .agents/skills/test-prover/mutate.mjs teardown --root <scratch>
//
//   run options: [--cwd <dir inside the scratch>] [--timeout <seconds>] [--zero-match <text> ...] [--count <regex> ...]
//
// `setup` adds a detached worktree at the checkout's HEAD under the system's temporary folder, copies
// the checkout's uncommitted work into it byte for byte (changed, added and untracked files; deleted
// files are deleted), links each `--link` folder (`node_modules`) from the checkout so the tests can
// load their dependencies, and prints the worktree's path with forward slashes. `teardown` unlinks
// those folders first, so their contents are never deleted, then removes the worktree and prunes it.
// `run`, `edit`, `revert` and `teardown` act only on a worktree `setup` made: one whose parent folder
// is named `test-prover-*` and holds the manifest `setup` wrote for it. A path through a linked
// folder is refused, so nothing this script writes reaches the checkout's own dependencies; a test
// runner may still write its caches there (`node_modules/.vite`).
//
// `run` runs the command. `edit` applies one exact search-and-replace to one file first: the search
// must occur exactly once, or `--occurrence <n>` picks one, and in a CRLF file an LF search and
// replace are matched and written with CRLF. `--edit <json file>` takes `{ "file", "search",
// "replace", "occurrence" }` from a file instead, for text a shell would mangle. `revert` puts each
// `--file` back as it was at `--base` (deleting one the base did not have) for the run. Each prints
// `outcome`: `red` (the tests ran and failed), `green` (they passed), `zero-match` (it ran no tests),
// `timeout`, or `error` (the command never ran or never finished on its own: it could not start, the
// shell did not find it, or a signal ended it), with the exit code, the test count it read, the shell
// and the output's last lines. After the run every touched file is put back as it was and read back;
// a file that does not match is an error. An interrupt (SIGINT, SIGTERM) kills the command's process
// tree and puts the files back before exiting.
//
// The command runs in bash, so one command line works on every machine: Git Bash on Windows (found
// from git's own install, never WSL's bash), bash elsewhere, sh where there is none. It runs from the
// worktree, or `--cwd` inside it, with CI=true, colour off and MSYS_NO_PATHCONV=1, and is killed with
// its whole process tree at the timeout (default 300 seconds). Zero-match signatures and test counts
// of the common runners are built in: node's test runner, Vitest, Jest, Playwright, dotnet test,
// pytest, and go test with `-v` (without it, go prints no per-test lines to count). A signature
// counts only at the start of a line (after any leading marks, `Error:` or `testing: warning:`), or
// at its end for a bracketed one (`[no test files]`), so one quoted in a failure message does not.
// `--zero-match` adds a signature, matched the same way in any case, and `--count` a regex whose
// first group is the number of tests run (write it without backslashes, `tests ([0-9]+)`, since
// shells mangle them). Every command prints one JSON object, and exits 1 with `{ "error": ... }` on
// failure; a red test is a result, not a failure.
//
// It never runs a git command that discards work: only rev-parse, show, cat-file, diff, ls-files and
// worktree add, remove and prune, so it runs under a guard that asks before those commands.

import { spawn, spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  copyFileSync,
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  realpathSync,
  rmdirSync,
  rmSync,
  symlinkSync,
  unlinkSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const COMMANDS = ["setup", "run", "edit", "revert", "teardown"];
const DEFAULT_TIMEOUT_MS = 300_000;
const TAIL_LINES = 60;
const MANIFEST = "test-prover.json";
const SCRATCH_PREFIX = "test-prover-";

// Lower-case text that shows a run selected no tests, when a line starts with it (a bracketed one:
// when a line ends with it).
export const ZERO_MATCH_SIGNATURES = [
  "no test files found", // Vitest
  "no test suite found", // Vitest
  "no tests found", // Jest, Playwright
  "no tests ran", // pytest
  "no tests to run", // go test -run, "testing: warning: no tests to run"
  "[no test files]", // go test
  "[no tests to run]", // go test
  "no test matches the given testcase filter", // dotnet test
  "no test is available", // dotnet test
  "could not find '", // node --test, given a test file that does not exist
];
// Exit codes a shell gives a command it could not find: sh and bash, then cmd.exe.
const NOT_FOUND_EXIT_CODES = new Set([127, 9009]);

// Output shapes that mean a run selected no tests, each with the name a result gives it. node's
// test runner reports a test file in which nothing matched `--test-name-pattern` as one passing
// test, the file itself, with `1..0` in TAP, so its count alone reads 1.
export const ZERO_MATCH_PATTERNS = [
  { pattern: /^[ \t]*1\.\.0[ \t]*$/m, name: "1..0 (TAP: a test file planned no tests)" },
  {
    pattern: /^[ \t]*✔ [^\s(]+\.(?:[cm]?[jt]s|[jt]sx)[ \t]+\(/m,
    name: "a test file reported as a passing test (node's runner: no test in it matched)",
  },
];

// The number of tests a run ran is read from its runner's summary by the first counter below that
// finds one. A test counts only when it ran, passed or failed: one skipped, todo, ignored, filtered
// out or deselected ran no assertion, so it proves nothing either way, and a filter that selected
// none reads 0 (Vitest's and Jest's totals count the tests a `-t` filter skipped). Each pattern is
// anchored, with no two neighbouring quantifiers able to match the same text, so none backtracks
// badly on a long log.

function lastMatch(pattern, text) {
  let last = null;
  for (const match of text.matchAll(pattern)) last = match;
  return last;
}

// The sum of the `<n> <word>` parts of a summary whose word is one of `ran`, or null when the
// summary names none of `known`.
function sumParts(summary, ran, known) {
  let total = 0;
  let any = false;
  for (const part of summary.matchAll(/(\d+) ([a-z]+)/g)) {
    if (!known.includes(part[2])) continue;
    any = true;
    if (ran.includes(part[2])) total += Number(part[1]);
  }
  return any ? total : null;
}

// node --test, spec and TAP reporters: `ℹ tests 3` / `# tests 3`, less the skipped and todo.
function countNode(text) {
  const tests = lastMatch(/^[ \t]*[#ℹ] tests (\d+)[ \t]*$/gm, text);
  if (!tests) return null;
  const skipped = lastMatch(/^[ \t]*[#ℹ] (?:skip|skipped) (\d+)[ \t]*$/gm, text);
  const todo = lastMatch(/^[ \t]*[#ℹ] todo (\d+)[ \t]*$/gm, text);
  return Number(tests[1]) - Number(skipped?.[1] ?? 0) - Number(todo?.[1] ?? 0);
}

// Vitest: `Tests  1 passed | 9 skipped (10)`, or `Tests  no tests`.
function countVitest(text) {
  const line = lastMatch(/^[ \t]*Tests[ \t]+(?:no tests|([^ \t\n(][^\n(]*)\((\d+)\))[ \t]*$/gm, text);
  if (!line) return null;
  if (line[2] === undefined) return 0;
  return sumParts(line[1], ["passed", "failed"], ["passed", "failed", "skipped", "todo"]) ?? Number(line[2]);
}

// Jest: `Tests:       1 failed, 3 skipped, 1 passed, 5 total`.
function countJest(text) {
  const line = lastMatch(/^Tests:([^\n]*) (\d+) total[ \t]*$/gm, text);
  if (!line) return null;
  return sumParts(line[1], ["passed", "failed"], ["passed", "failed", "skipped", "todo"]) ?? Number(line[2]);
}

// dotnet test on VSTest: `Failed:     0, Passed:     2, Skipped:     1, Total:     3`, or the older
// `Total tests: 5` block with its own `Skipped: 1` line.
function countVsTest(text) {
  const line = lastMatch(/\bFailed: +(\d+), Passed: +(\d+), Skipped: +(\d+), Total: +(\d+)/g, text);
  if (line) return Number(line[1]) + Number(line[2]);
  const total = lastMatch(/\bTotal tests: (\d+)/g, text);
  if (!total) return null;
  const skipped = lastMatch(/^[ \t]*Skipped: (\d+)[ \t]*$/gm, text.slice(total.index));
  return Number(total[1]) - Number(skipped?.[1] ?? 0);
}

// dotnet test on Microsoft Testing Platform: the indented `total:`, `failed:`, `succeeded:` and
// `skipped:` lines under the last `Test run summary:`.
function countTestingPlatform(text) {
  const summary = lastMatch(/^Test run summary:/gm, text);
  if (!summary) return null;
  const block = text.slice(summary.index);
  const field = (name) => block.match(new RegExp(`^[ \\t]+${name}: (\\d+)[ \\t]*$`, "m"))?.[1];
  const failed = field("failed");
  const succeeded = field("succeeded");
  if (failed !== undefined && succeeded !== undefined) return Number(failed) + Number(succeeded);
  const total = field("total");
  return total === undefined ? null : Number(total);
}

// Playwright: `Running 6 tests using 2 workers`.
function countPlaywright(text) {
  const line = lastMatch(/\bRunning (\d+) tests? using/g, text);
  return line ? Number(line[1]) : null;
}

// pytest's closing summary, `==== 1 failed, 2 passed, 1 deselected in 0.05s ====`: the tests that
// ran are the passed, failed, errored, xfailed and xpassed, never the skipped or deselected.
function countPytest(text) {
  const line = lastMatch(/^=+ ([^=\n]+) in [0-9.]+s(?: \([0-9:]+\))? =+[ \t]*$/gm, text);
  if (!line) return null;
  let total = 0;
  for (const part of line[1].matchAll(/(\d+) (passed|failed|errors?|xfailed|xpassed)\b/g)) total += Number(part[1]);
  return total;
}

// go test -v prints one `--- PASS|FAIL|SKIP: <name>` line per test and subtest; a SKIP did not run.
function countGo(text) {
  const lines = text.match(/^[ \t]*--- (?:PASS|FAIL|SKIP): /gm);
  if (!lines) return null;
  return lines.filter((line) => !line.includes("SKIP")).length;
}

// cargo test prints `test result: ok. 1 passed; 0 failed; 0 ignored; ...` for each test target
// (the doc-tests' `running 0 tests` often last), so the targets are summed.
function countCargo(text) {
  let total = null;
  for (const match of text.matchAll(/^test result: (?:ok|FAILED)\. (\d+) passed; (\d+) failed;/gm)) {
    total = (total ?? 0) + Number(match[1]) + Number(match[2]);
  }
  return total;
}

// deno test: `ok | 1 passed (2 steps) | 0 failed | 6 filtered out (12ms)`.
function countDeno(text) {
  const line = lastMatch(/^(?:ok|FAILED) \| (\d+) passed(?: \(\d+ steps?\))? \| (\d+) failed/gm, text);
  return line ? Number(line[1]) + Number(line[2]) : null;
}

// Python's unittest: `Ran 3 tests in 0.002s`, then `OK (skipped=1)` or `FAILED (failures=1, skipped=2)`.
function countUnittest(text) {
  const line = lastMatch(/^Ran (\d+) tests? in /gm, text);
  if (!line) return null;
  const skipped = text.slice(line.index).match(/^(?:OK|FAILED) \([^)\n]*\bskipped=(\d+)/m);
  return Number(line[1]) - Number(skipped?.[1] ?? 0);
}

export const COUNTERS = [
  countNode,
  countVitest,
  countJest,
  countVsTest,
  countTestingPlatform,
  countPlaywright,
  countPytest,
  countGo,
  countCargo,
  countDeno,
  countUnittest,
];

// The signature a line shows, matched at the start of the line once leading marks, `Error:` and
// `testing: warning:` are taken off, or at its end for a bracketed signature.
function lineSignature(line, signatures) {
  const trimmed = line.toLowerCase().trim();
  const start = trimmed
    .replace(/^[^a-z0-9[]+/, "")
    .replace(/^(?:error|warning|testing: warning):[ \t]*/, "");
  return (
    signatures.find((value) =>
      value.startsWith("[") ? trimmed.endsWith(value) || start.startsWith(value) : start.startsWith(value),
    ) ?? null
  );
}

export class MutateError extends Error {}

// ---------------------------------------------------------------------------------------------
// Arguments

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (!COMMANDS.includes(command)) {
    throw new MutateError(`expected one of ${COMMANDS.join(", ")}; got ${command ?? "(none)"}`);
  }
  const options = {
    command,
    repo: null,
    root: null,
    links: [],
    run: null,
    cwd: null,
    timeoutMs: DEFAULT_TIMEOUT_MS,
    zeroMatch: [],
    count: [],
    files: [],
    search: null,
    replace: null,
    occurrence: null,
    editFile: null,
    base: null,
  };
  const single = {
    "--repo": "repo",
    "--root": "root",
    "--command": "run",
    "--cwd": "cwd",
    "--search": "search",
    "--replace": "replace",
    "--edit": "editFile",
    "--base": "base",
  };
  const repeated = { "--link": "links", "--zero-match": "zeroMatch", "--count": "count", "--file": "files" };
  for (let index = 0; index < rest.length; index += 1) {
    const flag = rest[index];
    const value = rest[index + 1];
    const known = flag in single || flag in repeated || flag === "--timeout" || flag === "--occurrence";
    if (!known) throw new MutateError(`unknown argument: ${flag}`);
    if (value === undefined) throw new MutateError(`${flag} needs a value`);
    index += 1;
    if (flag in repeated) options[repeated[flag]].push(value);
    else if (flag === "--timeout") {
      const seconds = Number(value);
      if (!Number.isFinite(seconds) || seconds <= 0) throw new MutateError(`--timeout takes seconds; got "${value}"`);
      options.timeoutMs = Math.round(seconds * 1000);
    } else if (flag === "--occurrence") {
      const occurrence = Number(value);
      if (!Number.isInteger(occurrence) || occurrence < 1) throw new MutateError(`--occurrence takes 1, 2, ...; got "${value}"`);
      options.occurrence = occurrence;
    } else options[single[flag]] = value;
  }
  if (command === "setup") {
    if (!options.repo) throw new MutateError("setup needs --repo <checkout>");
    return options;
  }
  if (!options.root) throw new MutateError(`${command} needs --root <scratch worktree>`);
  if (command === "teardown") return options;
  if (!options.run) throw new MutateError(`${command} needs --command "<test command>"`);
  if (command === "edit") {
    if (options.editFile) {
      if (options.files.length || options.search !== null || options.replace !== null) {
        throw new MutateError("give --edit <json file>, or --file, --search and --replace, not both");
      }
    } else if (options.files.length !== 1 || options.search === null || options.replace === null) {
      throw new MutateError("edit needs one --file with --search and --replace, or --edit <json file>");
    }
  }
  if (command === "revert") {
    if (!options.base) throw new MutateError("revert needs --base <ref>");
    if (!options.files.length) throw new MutateError("revert needs at least one --file");
  }
  return options;
}

// ---------------------------------------------------------------------------------------------
// Editing and reading output

function occurrences(text, search) {
  const found = [];
  let at = text.indexOf(search);
  while (at !== -1) {
    found.push(at);
    at = text.indexOf(search, at + search.length);
  }
  return found;
}

// One exact replacement in `content`. In a file whose lines end in CRLF, LF line breaks in the
// search and the replacement are written as CRLF, so an edit written on any machine matches.
export function applyEdit(content, search, replace, occurrence = null) {
  if (search === "") throw new MutateError("the search text is empty");
  if (search === replace) throw new MutateError("the replacement changes nothing");
  const crlf = content.includes("\r\n");
  const toFile = (text) => (crlf ? text.replace(/\r?\n/g, "\r\n") : text);
  const wanted = toFile(search);
  const found = occurrences(content, wanted);
  if (found.length === 0) throw new MutateError(`the search text was not found: ${JSON.stringify(search)}`);
  if (occurrence === null && found.length > 1) {
    throw new MutateError(`the search text occurs ${found.length} times; name one with --occurrence`);
  }
  const index = occurrence ?? 1;
  if (index > found.length) {
    throw new MutateError(`occurrence ${index} asked for, but the search text occurs ${found.length} time(s)`);
  }
  const at = found[index - 1];
  return content.slice(0, at) + toFile(replace) + content.slice(at + wanted.length);
}

// Built from the escape character's code, since a control character in a regex literal fails
// some consumers' lint (no-control-regex).
const ANSI = new RegExp(`${String.fromCharCode(27)}\\[[0-9;?]*[A-Za-z]`, "g");

export function stripAnsi(text) {
  return text.replace(ANSI, "");
}

function lastGroup(pattern, text) {
  const flags = pattern.flags.includes("g") ? pattern.flags : `${pattern.flags}g`;
  let last = null;
  for (const match of text.matchAll(new RegExp(pattern.source, flags))) last = match[1];
  return last === null || last === undefined ? null : Number(last);
}

// The zero-match signature the output shows, if any, and the number of tests it says ran.
export function detect(output, { zeroMatch = [], count = [] } = {}) {
  const text = stripAnsi(output);
  const signatures = [
    ...zeroMatch.map((value) => value.toLowerCase().trim()).filter(Boolean),
    ...ZERO_MATCH_SIGNATURES,
  ];
  let signature = null;
  for (const line of text.split(/\r?\n/)) {
    signature = lineSignature(line, signatures);
    if (signature) break;
  }
  signature ??= ZERO_MATCH_PATTERNS.find((entry) => entry.pattern.test(text))?.name ?? null;
  let testCount = null;
  for (const source of count) {
    const value = lastGroup(new RegExp(source, "gm"), text);
    if (value !== null && Number.isFinite(value)) {
      testCount = value;
      break;
    }
  }
  for (const counter of COUNTERS) {
    if (testCount !== null) break;
    testCount = counter(text);
  }
  return { zeroMatch: signature, testCount };
}

// A run that never ran its command, or never finished on its own, proves nothing either way: it is
// an `error`, never a red. A failing run that counted tests is red whatever else its output says.
export function classify({ exitCode, timedOut, zeroMatch, testCount }) {
  if (timedOut) return "timeout";
  if (exitCode === null || exitCode === undefined || NOT_FOUND_EXIT_CODES.has(exitCode)) return "error";
  if (exitCode !== 0 && testCount > 0) return "red";
  if (zeroMatch || testCount === 0) return "zero-match";
  return exitCode === 0 ? "green" : "red";
}

function tail(text) {
  return stripAnsi(text)
    .split(/\r?\n/)
    .filter((line) => line.trim() !== "")
    .slice(-TAIL_LINES)
    .map((line) => (line.length > 400 ? `${line.slice(0, 400)}…` : line));
}

// ---------------------------------------------------------------------------------------------
// Running a command

function killTree(child) {
  if (process.platform === "win32") {
    spawnSync("taskkill", ["/pid", String(child.pid), "/T", "/F"], { windowsHide: true });
    return;
  }
  try {
    process.kill(-child.pid, "SIGKILL");
  } catch {
    child.kill("SIGKILL");
  }
}

// Git Bash's bash.exe, from git's own install: `git --exec-path` is <git>/mingw64/libexec/git-core
// (or <git>/libexec/git-core). A bare `bash` on Windows may be WSL's, which runs in another system.
function gitBash() {
  const result = spawnSync("git", ["--exec-path"], { encoding: "utf8", windowsHide: true });
  if (result.status !== 0 || !result.stdout) return null;
  let dir = path.resolve(result.stdout.trim());
  for (let up = 0; up < 4; up += 1) {
    for (const candidate of [path.join(dir, "bin", "bash.exe"), path.join(dir, "usr", "bin", "bash.exe")]) {
      if (existsSync(candidate)) return candidate;
    }
    dir = path.dirname(dir);
  }
  return null;
}

let shellCache;

// The shell commands run in: [program, its arguments before the command, a name for the result].
export function findShell() {
  if (shellCache) return shellCache;
  if (process.platform === "win32") {
    const bash = gitBash();
    shellCache = bash ? { program: bash, name: slash(bash) } : { program: null, name: "cmd.exe" };
  } else {
    const bash = ["/bin/bash", "/usr/bin/bash", "/usr/local/bin/bash"].find((candidate) => existsSync(candidate));
    shellCache = bash ? { program: bash, name: bash } : { program: "/bin/sh", name: "/bin/sh" };
  }
  return shellCache;
}

// The command running now, which an interrupt kills before it puts the files back.
let running = null;
// The put-back of the files changed for the run in progress.
let pending = null;

// Kills the running command's process tree and puts back every file changed for it, at once. The
// signal handlers call it before exiting; it is safe to call twice.
export function interrupt() {
  if (running) killTree(running);
  if (pending) pending();
}

export function runShell(command, { cwd, timeoutMs }) {
  return new Promise((resolve) => {
    const started = Date.now();
    const env = { ...process.env, CI: "true", FORCE_COLOR: "0", NO_COLOR: "1", MSYS_NO_PATHCONV: "1" };
    // Set when this script itself runs under `node --test`; a child that inherits it reports to that
    // runner in its binary protocol instead of printing its results.
    delete env.NODE_TEST_CONTEXT;
    const shell = findShell();
    const options = { cwd, env, windowsHide: true, detached: process.platform !== "win32" };
    const child = shell.program
      ? spawn(shell.program, ["-c", command], options)
      : spawn(command, { ...options, shell: true });
    running = child;
    const chunks = [];
    let size = 0;
    const keep = (chunk) => {
      chunks.push(chunk);
      size += chunk.length;
      while (size > 8 * 1024 * 1024 && chunks.length > 1) size -= chunks.shift().length;
    };
    child.stdout.on("data", keep);
    child.stderr.on("data", keep);
    let timedOut = false;
    const timer = setTimeout(() => {
      timedOut = true;
      killTree(child);
    }, timeoutMs);
    let settled = false;
    const finish = (exitCode, signal) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      if (running === child) running = null;
      resolve({
        exitCode,
        signal: signal ?? null,
        timedOut,
        output: Buffer.concat(chunks).toString("utf8"),
        durationMs: Date.now() - started,
      });
    };
    child.on("error", (error) => {
      keep(Buffer.from(`could not start the command: ${error.message}\n`));
      finish(null, null);
    });
    child.on("close", (code, signal) => finish(code, signal));
  });
}

// ---------------------------------------------------------------------------------------------
// Git and the scratch worktree

function git(cwd, args, { buffer = false, allowFail = false } = {}) {
  const result = spawnSync("git", args, {
    cwd,
    encoding: buffer ? "buffer" : "utf8",
    windowsHide: true,
    maxBuffer: 256 * 1024 * 1024,
  });
  if (result.error) throw new MutateError(`could not run git: ${result.error.message}`, { cause: result.error });
  if (result.status !== 0) {
    if (allowFail) return null;
    const stderr = buffer ? result.stderr.toString("utf8") : result.stderr;
    throw new MutateError(`git ${args.join(" ")} failed: ${stderr.trim()}`);
  }
  return result.stdout;
}

function slash(value) {
  return value.replaceAll("\\", "/");
}

function sameDir(a, b) {
  const norm = (value) => {
    const resolved = path.resolve(value);
    return process.platform === "win32" ? resolved.toLowerCase() : resolved;
  };
  return norm(a) === norm(b);
}

// The manifest `setup` wrote beside the scratch worktree at `root`, or null when `root` is not one:
// its parent folder must be named `test-prover-*` and hold a manifest naming this root.
function readManifest(root) {
  const resolved = path.resolve(root);
  const parent = path.dirname(resolved);
  const manifestPath = path.join(parent, MANIFEST);
  if (!path.basename(parent).startsWith(SCRATCH_PREFIX) || !existsSync(manifestPath)) return null;
  let manifest;
  try {
    manifest = JSON.parse(readFileSync(manifestPath, "utf8"));
  } catch {
    return null;
  }
  if (typeof manifest.root !== "string" || !sameDir(manifest.root, resolved)) return null;
  return { ...manifest, links: Array.isArray(manifest.links) ? manifest.links : [], parent };
}

// The worktree's top folder and its manifest, when `root` is the top of a scratch worktree `setup`
// made; otherwise an error. A checkout's main worktree, or any other linked worktree, is refused,
// so live work is never edited.
export function scratchTop(root) {
  if (!existsSync(root)) throw new MutateError(`no folder ${root}`);
  const manifest = readManifest(root);
  if (!manifest) {
    throw new MutateError(`${root} is not a scratch worktree setup made; make one with setup and pass its root`);
  }
  const top = git(root, ["rev-parse", "--show-toplevel"], { allowFail: true });
  if (!top) throw new MutateError(`${root} is not a git worktree; make a scratch worktree with setup`);
  const gitDir = git(root, ["rev-parse", "--absolute-git-dir"]).trim();
  const commonDir = path.resolve(root, git(root, ["rev-parse", "--git-common-dir"]).trim());
  if (sameDir(gitDir, commonDir)) {
    throw new MutateError(`${root} is a checkout's main worktree, not a scratch worktree; make one with setup`);
  }
  if (!sameDir(realpathSync(top.trim()), realpathSync(root))) {
    throw new MutateError(`--root names the top of the scratch worktree, ${slash(top.trim())}`);
  }
  return { root: realpathSync(root), links: manifest.links };
}

// The nearest existing folder at or above `absolute`, resolved through links.
function realParent(absolute) {
  let dir = absolute;
  while (!existsSync(dir)) dir = path.dirname(dir);
  return realpathSync(dir);
}

// The path inside the worktree, refusing one that leaves it, reaches into .git, or passes through a
// folder `setup` linked from the checkout (which would write the checkout's own copy).
function inside(root, file, { links = [], real = true } = {}) {
  const absolute = path.resolve(root, file);
  const relative = path.relative(root, absolute);
  if (!relative || relative.startsWith("..") || path.isAbsolute(relative)) {
    throw new MutateError(`${file} is not inside the worktree`);
  }
  if (relative.split(path.sep)[0] === ".git") throw new MutateError(`${file} is inside .git`);
  const rel = slash(relative);
  const link = links.find((entry) => rel === entry || rel.startsWith(`${entry}/`));
  if (link) throw new MutateError(`${file} is inside ${link}, a linked folder from the checkout; it is never written`);
  if (real) {
    const resolved = path.relative(realpathSync(root), realParent(absolute));
    if (resolved.startsWith("..") || path.isAbsolute(resolved)) {
      throw new MutateError(`${file} resolves outside the worktree, through a link`);
    }
  }
  return { absolute, relative: rel };
}

function nulList(text) {
  return text.split("\0").filter(Boolean);
}

function copyInto(from, to) {
  mkdirSync(path.dirname(to), { recursive: true });
  copyFileSync(from, to);
}

function setup(options) {
  const repo = git(options.repo, ["rev-parse", "--show-toplevel"]).trim();
  const head = git(repo, ["rev-parse", "HEAD"]).trim();
  const parent = mkdtempSync(path.join(os.tmpdir(), SCRATCH_PREFIX));
  const root = path.join(parent, "tree");
  git(repo, ["worktree", "add", "--detach", root, head]);
  const manifest = { repo: slash(repo), root: slash(root), head, links: [] };
  writeFileSync(path.join(parent, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`);

  const copied = [];
  const deleted = [];
  const changes = nulList(git(repo, ["diff", "HEAD", "--name-status", "--no-renames", "-z"]));
  for (let index = 0; index < changes.length; index += 2) {
    const status = changes[index];
    const file = changes[index + 1];
    if (status.startsWith("D")) {
      rmSync(path.join(root, file), { force: true });
      deleted.push(file);
    } else if (existsSync(path.join(repo, file)) && lstatSync(path.join(repo, file)).isFile()) {
      copyInto(path.join(repo, file), path.join(root, file));
      copied.push(file);
    }
  }
  for (const file of nulList(git(repo, ["ls-files", "--others", "--exclude-standard", "-z"]))) {
    if (!lstatSync(path.join(repo, file)).isFile()) continue;
    copyInto(path.join(repo, file), path.join(root, file));
    copied.push(file);
  }

  const linked = [];
  for (const link of options.links) {
    // The checkout's folder may itself be a link (a package manager's store): only the scratch side
    // must resolve inside its tree.
    const target = inside(repo, link, { real: false });
    const at = inside(root, link);
    if (!existsSync(target.absolute)) throw new MutateError(`--link ${link}: the checkout has no ${link}`);
    if (existsSync(at.absolute)) throw new MutateError(`--link ${link}: the worktree already has ${link}`);
    mkdirSync(path.dirname(at.absolute), { recursive: true });
    symlinkSync(target.absolute, at.absolute, process.platform === "win32" ? "junction" : "dir");
    manifest.links.push(at.relative);
    writeFileSync(path.join(parent, MANIFEST), `${JSON.stringify(manifest, null, 2)}\n`);
    linked.push(at.relative);
  }
  return { root: slash(root), repo: slash(repo), head, copied, deleted, linked };
}

function teardown(options) {
  const root = path.resolve(options.root);
  const manifest = readManifest(root);
  if (!manifest) {
    throw new MutateError(`${options.root} is not a scratch worktree setup made; teardown removes only those`);
  }
  const { parent } = manifest;
  // Unlink each linked folder before anything deletes the tree, so the checkout's copy survives.
  for (const link of manifest.links) {
    const at = path.join(root, link);
    if (!existsSync(at) && !lstatSync(at, { throwIfNoEntry: false })) continue;
    if (!lstatSync(at).isSymbolicLink()) throw new MutateError(`${link} in the worktree is no longer a link; remove it by hand`);
    // A Windows junction is removed as an empty directory would be, which never follows it.
    if (process.platform === "win32") rmdirSync(at);
    else unlinkSync(at);
    if (lstatSync(at, { throwIfNoEntry: false })) throw new MutateError(`could not unlink ${link}; remove the worktree by hand`);
  }
  // With the checkout gone there is no worktree list to prune: removing the folder is all there is.
  if (existsSync(manifest.repo)) {
    if (existsSync(root)) git(manifest.repo, ["worktree", "remove", "--force", root]);
    git(manifest.repo, ["worktree", "prune"]);
  }
  rmSync(parent, { recursive: true, force: true });
  return { root: slash(root), removed: !existsSync(root) };
}

// ---------------------------------------------------------------------------------------------
// Changing files for one run

function digest(buffer) {
  return createHash("sha256").update(buffer).digest("hex");
}

// What a file holds now, as `withChanges` records it before a run: its bytes and their hash, or
// null for both when it does not exist.
export function snapshot(absolute) {
  const content = existsSync(absolute) ? readFileSync(absolute) : null;
  return { absolute, content, hash: content ? digest(content) : null };
}

// Puts each snapshot back (deleting a file that did not exist) and reads it back, returning the
// paths whose bytes still differ. `write` is the file writer, replaced only by the tests.
export function putBack(originals, write = writeFileSync) {
  const problems = [];
  for (const original of originals) {
    if (original.content === null) rmSync(original.absolute, { force: true });
    else {
      mkdirSync(path.dirname(original.absolute), { recursive: true });
      write(original.absolute, original.content);
    }
    const now = existsSync(original.absolute) ? digest(readFileSync(original.absolute)) : null;
    if (now !== original.hash) problems.push(original.absolute);
  }
  return problems;
}

// Writes each change ({ absolute, content: Buffer | null }), runs `run`, then puts every file back
// as it was and reads it back. The originals are put back on an interrupt too.
async function withChanges(changes, run) {
  const originals = changes.map((change) => snapshot(change.absolute));
  const restore = () => putBack(originals);
  // An interrupt kills the command's process tree (its own process group on POSIX, which a signal
  // to this process does not reach) before the files go back, so nothing runs on against them.
  const onSignal = () => {
    interrupt();
    process.exit(130);
  };
  const signals = ["SIGINT", "SIGTERM", "SIGHUP"];
  for (const signal of signals) process.on(signal, onSignal);
  pending = restore;
  let result;
  let failed = false;
  let failure;
  try {
    for (const change of changes) {
      if (change.content === null) rmSync(change.absolute, { force: true });
      else {
        mkdirSync(path.dirname(change.absolute), { recursive: true });
        writeFileSync(change.absolute, change.content);
      }
    }
    result = await run();
  } catch (error) {
    failed = true;
    failure = error;
  }
  // The files go back whether or not the run threw. The checks come after, not in a `finally`
  // (no-unsafe-finally): a restore that failed wins, carrying the run's own error as its cause.
  for (const signal of signals) process.off(signal, onSignal);
  pending = null;
  const problems = restore();
  if (problems.length) {
    throw new MutateError(`restore failed: ${problems.map(slash).join(", ")} no longer match their original bytes`, {
      cause: failed ? failure : undefined,
    });
  }
  if (failed) throw failure;
  return result;
}

async function runAndRead(scratch, options) {
  const cwd = options.cwd ? inside(scratch.root, options.cwd, { links: scratch.links }).absolute : scratch.root;
  const ran = await runShell(options.run, { cwd, timeoutMs: options.timeoutMs });
  const seen = detect(ran.output, { zeroMatch: options.zeroMatch, count: options.count });
  return {
    command: options.run,
    shell: findShell().name,
    exitCode: ran.exitCode,
    signal: ran.signal,
    timedOut: ran.timedOut,
    durationMs: ran.durationMs,
    ...seen,
    outcome: classify({ exitCode: ran.exitCode, timedOut: ran.timedOut, ...seen }),
    tail: tail(ran.output),
  };
}

function readEditSpec(options) {
  if (!options.editFile) {
    return { file: options.files[0], search: options.search, replace: options.replace, occurrence: options.occurrence };
  }
  let spec;
  try {
    spec = JSON.parse(readFileSync(options.editFile, "utf8"));
  } catch (error) {
    throw new MutateError(`--edit ${options.editFile}: ${error.message}`, { cause: error });
  }
  for (const key of ["file", "search", "replace"]) {
    if (typeof spec[key] !== "string") throw new MutateError(`--edit ${options.editFile}: "${key}" must be a string`);
  }
  return { ...spec, occurrence: options.occurrence ?? spec.occurrence ?? null };
}

async function commandRun(options) {
  return runAndRead(scratchTop(options.root), options);
}

async function commandEdit(options) {
  const scratch = scratchTop(options.root);
  const spec = readEditSpec(options);
  const target = inside(scratch.root, spec.file, { links: scratch.links });
  if (!existsSync(target.absolute)) throw new MutateError(`${spec.file} does not exist in the worktree`);
  const original = readFileSync(target.absolute, "utf8");
  const edited = applyEdit(original, spec.search, spec.replace, spec.occurrence);
  const result = await withChanges([{ absolute: target.absolute, content: Buffer.from(edited, "utf8") }], () =>
    runAndRead(scratch, options),
  );
  return { file: target.relative, search: spec.search, replace: spec.replace, ...result, restored: true };
}

async function commandRevert(options) {
  const scratch = scratchTop(options.root);
  const { root } = scratch;
  git(root, ["rev-parse", "--verify", "--quiet", `${options.base}^{commit}`]);
  const changes = [];
  const reverted = [];
  for (const file of options.files) {
    const target = inside(root, file, { links: scratch.links });
    const spec = `${options.base}:${target.relative}`;
    const present = git(root, ["cat-file", "-e", spec], { allowFail: true }) !== null;
    changes.push({ absolute: target.absolute, content: present ? git(root, ["show", spec], { buffer: true }) : null });
    reverted.push({ file: target.relative, atBase: present ? "present" : "absent" });
  }
  const result = await withChanges(changes, () => runAndRead(scratch, options));
  return { base: options.base, reverted, ...result, restored: true };
}

const HANDLERS = {
  setup: async (options) => setup(options),
  teardown: async (options) => teardown(options),
  run: commandRun,
  edit: commandEdit,
  revert: commandRevert,
};

export async function main(argv) {
  const options = parseArgs(argv);
  return HANDLERS[options.command](options);
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try {
    console.log(JSON.stringify(await main(process.argv.slice(2)), null, 2));
  } catch (error) {
    console.log(JSON.stringify({ error: error.message }, null, 2));
    process.exitCode = 1;
  }
}
