#!/usr/bin/env node
// Reads what `dependabot` needs to triage a repository's open Dependabot pull requests: what each
// one updates and how far, the checks on its current head, whether anyone but Dependabot has
// pushed to it, and the review verdict already posted on it. It never writes: a comment, a fix and
// a merge are steps the skill takes itself, after reading this script's answer.
//
//   node .agents/skills/dependabot/dependabot.mjs list --repo <owner/name> [--pr <n>] [--label <name>] [--wait <seconds>]
//
// `list` gives every open Dependabot pull request, oldest first, or only `--pr <n>`. For each:
//   - `updates`: each dependency with `from`, `to` and `kind` (major, minor, patch, digest, suffix,
//     downgrade, removed or unknown), read from the body Dependabot wrote, or from the title when a
//     person has replaced the body; `parsed` is false when neither names one, and `kind` is the
//     furthest-reaching update's.
//   - `ecosystem`, `directory` and `group`, from the branch name and the body.
//   - `checks`: every check run and commit status on the current head, by its full sha, as
//     `failed`, `cancelled`, `pending` and `passed`, with each failed check's run and job ids for
//     reading its log; `state` is failed, pending (also while nothing has reported yet) or passed.
//   - `mergeable` and `mergeableState`, read again a few seconds apart while GitHub is still
//     computing them (null and `unknown`, which they stay if it never finishes).
//   - `maintainedByDependabot`: false once a commit by anyone else is on the branch (a branch
//     update merging `main` in, or a fix), after which Dependabot no longer rebases it.
//   - `verdict`: the latest comment carrying the review marker, its `kind` (low-risk or
//     needs-effort, by its first character) and `versionsNamed`, true when it names every update's
//     new version; `comments`: the other comments people wrote.
// `marker` is the closing line a verdict comment carries, with `--label` (default: the repository's
// name) as the name in it.
//
// `--wait <seconds>`, with `--pr`, polls every 20 seconds until that PR's checks are no longer
// pending, or the time is up; keep it under the shell's own timeout and call again to keep waiting.
// A failed call is retried on the next poll, twice. Every call is GitHub's REST API, never GraphQL,
// so the script works where GraphQL is unavailable (scheduled cloud runs). It prints one JSON
// object, and exits 1 with `{ "error": ... }` on failure.

import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const COMMANDS = ["list"];
const POLL_MS = 20_000;
const MAX_FAILED_CALLS = 2;
const BOT = "dependabot[bot]";
const FAILED = new Set(["failure", "timed_out", "action_required", "startup_failure", "stale"]);
const PASSED = new Set(["success", "neutral", "skipped"]);
// Furthest-reaching first: a PR's `kind` is the first of these any of its updates has.
export const KIND_ORDER = ["major", "unknown", "downgrade", "suffix", "minor", "patch", "digest", "removed"];
const MARKER = /Dependabot PR review by the (.+?) routine/;

export class DependabotError extends Error {}

// ---------------------------------------------------------------------------------------------
// Arguments

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (!COMMANDS.includes(command)) {
    throw new DependabotError(`usage: dependabot.mjs ${COMMANDS.join("|")} --repo <owner/name> [--pr <n>] [--label <name>] [--wait <seconds>]`);
  }
  const options = { command, repo: null, pr: null, label: null, wait: 0 };
  for (let index = 0; index < rest.length; index += 1) {
    const flag = rest[index];
    const value = rest[index + 1];
    if (!["--repo", "--pr", "--label", "--wait"].includes(flag)) throw new DependabotError(`unknown argument: ${flag}`);
    if (value === undefined || value.startsWith("--")) throw new DependabotError(`${flag} needs a value`);
    index += 1;
    if (flag === "--repo") {
      if (!/^[\w.-]+\/[\w.-]+$/.test(value)) throw new DependabotError(`--repo must be owner/name, not ${value}`);
      options.repo = value;
    } else if (flag === "--pr") {
      if (!/^[1-9]\d*$/.test(value)) throw new DependabotError(`--pr must be a pull request number, not ${value}`);
      options.pr = Number(value);
    } else if (flag === "--label") {
      options.label = value;
    } else {
      if (!/^\d+$/.test(value)) throw new DependabotError(`--wait must be a number of seconds, not ${value}`);
      options.wait = Number(value);
    }
  }
  if (!options.repo) throw new DependabotError("--repo is required");
  if (options.wait > 0 && !options.pr) throw new DependabotError("--wait needs --pr");
  options.label ??= options.repo.split("/")[1];
  return options;
}

// ---------------------------------------------------------------------------------------------
// GitHub

function isRetryable(message) {
  return /secondary rate limit|abuse detection|HTTP 50[234]|timed? ?out|ECONNRESET|connection reset|TLS|unexpected EOF/i.test(
    message,
  );
}

export function ghRest(endpoint) {
  const result = spawnSync("gh", ["api", endpoint], {
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, MSYS_NO_PATHCONV: "1" },
  });
  if (result.error) throw new DependabotError(`could not run gh: ${result.error.message}`);
  let body = null;
  try {
    body = JSON.parse(result.stdout);
  } catch {
    // gh printed no JSON: report what it said.
  }
  if (result.status !== 0) {
    const message = body?.message || result.stderr.trim() || result.stdout.trim();
    const error = new DependabotError(`${endpoint}: ${message}`);
    error.retryable = isRetryable(`${message} ${result.stderr}`);
    throw error;
  }
  return body;
}

function pause(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

// ---------------------------------------------------------------------------------------------
// What a pull request updates

const VERSION = "`?([^`\\s]+?)`?";
const UPDATE_LINES = [
  // Grouped and multi-dependency bodies: one line per dependency.
  new RegExp(`^Updates \`([^\`]+)\` from ${VERSION} to ${VERSION}\\.?$`),
  // NuGet bodies.
  new RegExp(`^Updated \\[([^\\]]+)\\]\\([^)]*\\) from ${VERSION} to ${VERSION}\\.?$`),
  // A single dependency, linked or plain (container images); never "Bumps the <group> group".
  new RegExp(`^Bumps (?:\\[([^\\]]+)\\]\\([^)]*\\)|(?!the )([^\\s\\[]+)) from ${VERSION} to ${VERSION}\\.?$`),
];
const TITLE_UPDATE = new RegExp(`\\bbump (\\S+) from ${VERSION} to ${VERSION}(?:\\s|$)`, "i");

export function parseUpdates(title, body) {
  const updates = [];
  const seen = new Set();
  const add = (name, from, to) => {
    const key = `${name} ${from} ${to}`;
    if (seen.has(key)) return;
    seen.add(key);
    updates.push({ name, from, to, kind: from === null ? "removed" : classify(from, to) });
  };
  for (const raw of (body || "").split(/\r?\n/)) {
    const line = raw.trim();
    const removed = line.match(/^Removes `([^`]+)`\.?$/);
    if (removed) {
      add(removed[1], null, null);
      continue;
    }
    for (const pattern of UPDATE_LINES) {
      const match = line.match(pattern);
      if (!match) continue;
      const [, ...groups] = match;
      const [from, to] = groups.slice(-2);
      add(groups.slice(0, -2).find(Boolean), from, to);
      break;
    }
  }
  if (updates.length === 0) {
    const match = (title || "").match(TITLE_UPDATE);
    if (match) add(match[1], match[2], match[3]);
  }
  return updates;
}

function numericPrefix(version) {
  const match = version.replace(/^v(?=\d)/, "").match(/^(\d+(?:\.\d+)*)(.*)$/);
  return match ? { parts: match[1].split(".").map(Number), suffix: match[2] } : null;
}

export function classify(from, to) {
  if (/^[0-9a-f]{7,64}$/i.test(from) && /^[0-9a-f]{7,64}$/i.test(to)) return "digest";
  // The same tag on both sides is a new image digest behind it.
  if (from === to) return "digest";
  const a = numericPrefix(from);
  const b = numericPrefix(to);
  if (!a || !b) return "unknown";
  const length = Math.max(a.parts.length, b.parts.length);
  for (let index = 0; index < length; index += 1) {
    const x = a.parts[index] ?? 0;
    const y = b.parts[index] ?? 0;
    if (x === y) continue;
    if (y < x) return "downgrade";
    return index === 0 ? "major" : index === 1 ? "minor" : "patch";
  }
  return a.suffix === b.suffix ? "digest" : "suffix";
}

export function furthestKind(updates) {
  return KIND_ORDER.find((kind) => updates.some((update) => update.kind === kind)) ?? "unknown";
}

export function describeBranch(headRef, title, body) {
  const ecosystem = headRef.match(/^dependabot\/([^/]+)\//)?.[1] ?? null;
  const text = body || "";
  const directory =
    text.match(/ in the `?(\/[^\s`]*)`? directory/)?.[1] ??
    text.match(/ group in `?(\/[^\s`]*)`? with /)?.[1] ??
    (title || "").match(/ in (\/\S*)/)?.[1] ??
    "/";
  const group = text.match(/^Bumps the `?([\w.-]+)`? group/m)?.[1] ?? null;
  return { ecosystem, directory, group };
}

// ---------------------------------------------------------------------------------------------
// Checks, commits and comments

function idsFrom(url) {
  return {
    runId: Number(url?.match(/\/runs\/(\d+)/)?.[1]) || null,
    jobId: Number(url?.match(/\/job\/(\d+)/)?.[1]) || null,
  };
}

export function summarizeChecks(checkRuns, combinedStatus) {
  const checks = { state: "passed", failed: [], cancelled: [], pending: [], passed: [] };
  for (const run of checkRuns) {
    const entry = { name: run.name, url: run.html_url ?? null };
    if (run.status !== "completed") checks.pending.push(entry);
    else if (FAILED.has(run.conclusion)) {
      checks.failed.push({ ...entry, conclusion: run.conclusion, checkRunId: run.id ?? null, ...idsFrom(run.html_url) });
    }
    else if (run.conclusion === "cancelled") checks.cancelled.push(entry);
    else if (PASSED.has(run.conclusion)) checks.passed.push(entry);
    else checks.pending.push(entry);
  }
  for (const status of combinedStatus?.statuses ?? []) {
    const entry = { name: status.context, url: status.target_url ?? null };
    if (status.state === "failure" || status.state === "error") {
      checks.failed.push({ ...entry, conclusion: status.state, checkRunId: null, runId: null, jobId: null });
    }
    else if (status.state === "pending") checks.pending.push(entry);
    else checks.passed.push(entry);
  }
  if (checks.failed.length > 0) checks.state = "failed";
  else if (checks.pending.length > 0 || checks.passed.length + checks.cancelled.length === 0) checks.state = "pending";
  checks.passed = checks.passed.length;
  return checks;
}

function versionNamed(text, version) {
  if (/^[0-9a-f]{7,64}$/i.test(version)) return text.includes(version.slice(0, 7));
  return text.includes(version);
}

export function readVerdict(comments, updates) {
  let verdict = null;
  const others = [];
  for (const comment of comments) {
    const body = comment.body ?? "";
    const marker = body.match(MARKER);
    if (marker) {
      const first = body.trimStart();
      verdict = {
        kind: first.startsWith("✅") ? "low-risk" : first.startsWith("⚠") ? "needs-effort" : "unknown",
        label: marker[1],
        url: comment.html_url ?? null,
        createdAt: comment.created_at ?? null,
        versionsNamed: updates.length > 0 && updates.filter((update) => update.to).every((update) => versionNamed(body, update.to)),
      };
    } else if (comment.user?.type !== "Bot") {
      others.push({ author: comment.user?.login ?? null, createdAt: comment.created_at ?? null, body: body.slice(0, 500) });
    }
  }
  return { verdict, comments: others };
}

// ---------------------------------------------------------------------------------------------
// Reading

// GitHub computes `mergeable` lazily: the first read after a push answers null while it works.
const MERGEABLE_READS = 4;
const MERGEABLE_PAUSE_MS = 3_000;

function readOne(rest, sleep, options, pr) {
  let full = pr.mergeable_state === undefined ? rest(`repos/${options.repo}/pulls/${pr.number}`) : pr;
  for (let read = 1; read < MERGEABLE_READS && full.state === "open" && full.mergeable === null; read += 1) {
    sleep(MERGEABLE_PAUSE_MS);
    full = rest(`repos/${options.repo}/pulls/${full.number}`);
  }
  const head = full.head.sha;
  const updates = parseUpdates(full.title, full.body);
  const checkRuns = rest(`repos/${options.repo}/commits/${head}/check-runs?per_page=100`).check_runs ?? [];
  const status = rest(`repos/${options.repo}/commits/${head}/status`);
  const commits = rest(`repos/${options.repo}/pulls/${full.number}/commits?per_page=100`);
  const comments = rest(`repos/${options.repo}/issues/${full.number}/comments?per_page=100`);
  const othersCommitted = commits.filter((commit) => commit.author?.login !== BOT).map((commit) => commit.sha);
  return {
    number: full.number,
    title: full.title,
    url: full.html_url,
    author: full.user?.login ?? null,
    state: full.merged_at ? "merged" : full.state,
    draft: Boolean(full.draft),
    labels: (full.labels ?? []).map((label) => label.name),
    createdAt: full.created_at,
    headSha: head,
    headRef: full.head.ref,
    ...describeBranch(full.head.ref, full.title, full.body),
    updates,
    parsed: updates.length > 0,
    kind: furthestKind(updates),
    mergeable: full.mergeable ?? null,
    mergeableState: full.mergeable_state ?? "unknown",
    checks: summarizeChecks(checkRuns, status),
    maintainedByDependabot: othersCommitted.length === 0,
    othersCommitted,
    ...readVerdict(comments, updates),
  };
}

export function readList(rest, sleep, options) {
  const result = {
    repo: options.repo,
    marker: `\u{1F916} Dependabot PR review by the ${options.label} routine`,
    prs: [],
  };
  if (options.pr) {
    result.prs.push(readOne(rest, sleep, options, rest(`repos/${options.repo}/pulls/${options.pr}`)));
    return result;
  }
  const open = rest(`repos/${options.repo}/pulls?state=open&per_page=100`)
    .filter((pr) => pr.user?.login === BOT)
    .sort((a, b) => (a.created_at < b.created_at ? -1 : a.created_at > b.created_at ? 1 : 0));
  for (const pr of open) result.prs.push(readOne(rest, sleep, options, { number: pr.number }));
  return result;
}

function stillWaiting(options, result) {
  return options.pr !== null && result.prs[0]?.state === "open" && result.prs[0].checks.state === "pending";
}

export function runCommand(argv, { rest = ghRest, sleep = pause, now = Date.now } = {}) {
  const options = parseArgs(argv);
  const start = now();
  const deadline = start + options.wait * 1000;
  let failedCalls = 0;
  let lastError = null;
  for (;;) {
    let result = null;
    try {
      result = readList(rest, sleep, options);
      failedCalls = 0;
    } catch (error) {
      if (!error.retryable) throw error;
      failedCalls += 1;
      lastError = error;
      if (failedCalls > MAX_FAILED_CALLS || now() + POLL_MS > deadline) throw error;
    }
    if (result) {
      const waiting = stillWaiting(options, result);
      if (!waiting || now() + POLL_MS > deadline) {
        result.waitedSeconds = Math.round((now() - start) / 1000);
        if (options.wait > 0) result.timedOut = waiting;
        if (lastError) result.recoveredFrom = lastError.message;
        return result;
      }
    }
    sleep(POLL_MS);
  }
}

function cli() {
  try {
    console.log(JSON.stringify(runCommand(process.argv.slice(2)), null, 2));
  } catch (error) {
    console.log(JSON.stringify({ error: error.message }, null, 2));
    process.exitCode = 1;
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) cli();
