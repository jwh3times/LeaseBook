#!/usr/bin/env node

/**
 * Names a crashed e2e dev server in CI (#798). The e2e jobs serve the SPA with the Vite dev server,
 * which Playwright starts through its `webServer` config. When that server dies mid-run, Playwright
 * neither notices nor stops: every later test fails with a refused connection, and the job ends with
 * dozens of failures and nothing that says the server went away. This wrapper runs a Playwright
 * command, passes its output through untouched, and afterwards adds one GitHub Actions `::error`
 * annotation naming the crash when it saw one. It exits with the command's own exit code, so a crash
 * still fails the job and a healthy run looks exactly as it did.
 *
 * Usage (from `web/`, as the workflows run it; a guard in its test fails a workflow Playwright run
 * that skips it):
 *
 *   node ../scripts/e2e-server-crash.mjs -- <command> [args...]
 *
 * The first `--` ends the wrapper's own arguments, so a later one goes to the command. The command
 * runs without a shell, so it is CI's: on Windows `npm` and `npx` are `.cmd` shims only a shell can
 * start (see `playwright-cli.mjs`), and this is not meant for local runs.
 *
 * Three signs count, strongest first. The annotation quotes the first line of the strongest kind
 * seen:
 *
 *   - `[WebServer] abort: …`: a web server's process aborted, and said why. This is the 2026-11-18
 *     crash, a V8 abort in the SPA dev server on Node 26.13.1 (nodejs/node#66870).
 *   - `[WebServer] ==== JS stack trace =`: V8's crash dump, when no abort line came before it.
 *   - `net::ERR_CONNECTION_REFUSED at <url>` to the SPA's origin, `http://localhost:5373` in CI
 *     (`web/playwright.config.ts`): the SPA dev server went away without a word (an exit, a kill). A
 *     refused connection to any other origin, the API host's included, is not this, and an origin that
 *     only starts the same way (`:53730`) does not match.
 *
 * Playwright prefixes the output of every web server it starts with `[WebServer]`: the SPA dev
 * server, the API host and, in the stand-in pass, the Stripe stand-in. The first two signs cannot
 * tell which of them crashed, so the annotation does not say; only the third names the SPA.
 *
 * What it cannot see: a server that hangs instead of dying, or one that dies after the last test has
 * already navigated. Neither leaves these signs.
 *
 * The exit code is the command's (1 when a signal ended it or it could not start), and 2 on a usage
 * error.
 */

import { spawn } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

/** Where `web/playwright.config.ts` serves the SPA in CI. */
const SPA_ORIGIN = "http://localhost:5373";

/** ANSI SGR escape codes: the colors and weights Playwright's output carries. */
// eslint-disable-next-line no-control-regex
const ANSI_SGR = /\u001b\[[0-9;]*m/g;

function originOf(url) {
  try {
    return new URL(url).origin;
  } catch {
    return null;
  }
}

/** A workflow command's message, with what would end or split the command escaped. */
function escapeData(text) {
  return text.replace(/%/g, "%25").replace(/\r/g, "%0D").replace(/\n/g, "%0A");
}

export function annotation(crash) {
  const what =
    crash.kind === "unreachable"
      ? "The SPA dev server stopped answering mid-run without printing why."
      : "A web server Playwright started for this run crashed mid-run.";
  return (
    "::error title=E2E web server crashed::" +
    escapeData(
      `${what} The test failures after it are connection-refused fallout, not product ` +
        `regressions: re-run the job. First sign: ${crash.line}`,
    )
  );
}

export function createScanner() {
  let abort = null;
  let dump = null;
  let refused = null;
  return {
    feed(raw) {
      const line = raw.replace(ANSI_SGR, "");
      if (abort === null && /\[WebServer\] abort: /.test(line)) {
        abort = line.slice(line.indexOf("[WebServer]")).trimEnd();
      }
      if (dump === null && /\[WebServer\] ==== JS stack trace =/.test(line)) {
        dump = line.slice(line.indexOf("[WebServer]")).trimEnd();
      }
      if (refused === null) {
        const at = /net::ERR_CONNECTION_REFUSED at (\S+)/.exec(line);
        if (at && originOf(at[1]) === SPA_ORIGIN) refused = line.trim();
      }
    },
    crash() {
      if (abort !== null) return { kind: "abort", line: abort };
      if (dump !== null) return { kind: "crash-dump", line: dump };
      if (refused !== null) return { kind: "unreachable", line: refused };
      return null;
    },
  };
}

/**
 * Feeds a stream to the scanner a line at a time, while passing every byte through untouched.
 * Resolves to whether the last thing written ended a line.
 */
function relay(from, to, scanner) {
  let partial = "";
  let endedLine = true;
  from.setEncoding("utf8");
  from.on("data", (chunk) => {
    to.write(chunk);
    if (chunk.length > 0) endedLine = chunk.endsWith("\n");
    const lines = (partial + chunk).split("\n");
    partial = lines.pop();
    for (const line of lines) scanner.feed(line);
  });
  return new Promise((resolve) =>
    from.on("end", () => {
      if (partial !== "") scanner.feed(partial);
      resolve(endedLine);
    }),
  );
}

export function parseArgs(argv) {
  if (argv[0] !== "--") {
    throw new Error(
      argv.length === 0 ? "no command" : `unknown argument: ${argv[0]}`,
    );
  }
  const command = argv.slice(1);
  if (command.length === 0) throw new Error("no command after --");
  return { command };
}

async function main() {
  let options;
  try {
    options = parseArgs(process.argv.slice(2));
  } catch (error) {
    console.error(`e2e-server-crash: ${error.message}`);
    console.error("usage: e2e-server-crash.mjs -- <command> [args...]");
    process.exit(2);
  }
  const [file, ...args] = options.command;
  const scanner = createScanner();
  // No shell: the command is given as words, and a shell would parse them again.
  const child = spawn(file, args, { stdio: ["inherit", "pipe", "pipe"] });
  const relays = Promise.all([
    relay(child.stdout, process.stdout, scanner),
    relay(child.stderr, process.stderr, scanner),
  ]);
  let started = true;
  const status = await new Promise((resolve) => {
    child.on("error", (error) => {
      started = false;
      console.error(
        `e2e-server-crash: could not start ${file}: ${error.message}`,
      );
      resolve(1);
    });
    // A run ended by a signal has no exit code; it failed.
    child.on("close", (code, signal) => resolve(code ?? (signal ? 1 : 0)));
  });
  // `close` comes after the child's output has ended, so this only drains what is left. A command
  // that never started may never end its pipes, so it is not waited for.
  // GitHub reads a workflow command only at the start of a line, so one is started if the run's
  // stdout ended mid-line.
  const [stdoutEndedLine] = started ? await relays : [true];
  const crash = scanner.crash();
  if (crash) {
    const lead = stdoutEndedLine ? "" : "\n";
    process.stdout.write(`${lead}${annotation(crash)}\n`);
  }
  process.exitCode = status;
}

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  await main();
}
