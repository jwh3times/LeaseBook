#!/usr/bin/env node
// Reads what `land` needs to decide each step of taking a pull request from open to merged and
// released: whether its checks on the current head have passed, why GitHub blocks it, which issues
// the merge will close, and what the merge started. It never writes: the merge, a catch-up and any
// re-run are commands the skill runs itself, after reading this script's answer.
//
//   node .agents/skills/land/land.mjs pr      --repo <owner/name> --pr <n> [--expect-head <sha> | --moved-from <sha>] [--wait <seconds>]
//   node .agents/skills/land/land.mjs release --repo <owner/name> --sha <merge sha> --workflow <file> [--wait <seconds>]
//   node .agents/skills/land/land.mjs runs    --repo <owner/name> --sha <merge sha> (--workflow <file> | --check <name>) ... [--wait <seconds>]
//
// `pr` gives the PR's state, its head, every check on that head with whether it is required, the
// ruleset's required checks that never reported, the issues it will close against the ones its
// link line names, and a `verdict`: merged, closed, head-moved, draft, conflicts, failed, pending,
// missing-checks, behind, blocked or ready. A `blocked` PR also lists its open code scanning alerts.
// `release` finds the release workflow's run for the merge commit by its sha, the tags on the merge,
// and the stamp commit on top of it. `runs` finds each named workflow's run for the merge, or for
// the stamp commit above it, which is the sha a `workflow_run` run lists; `--check` reads a check
// run on the merge commit by name instead, for a deploy that reports as a check (Cloudflare Pages).
//
// `--wait <seconds>` polls every 20 seconds until the answer is no longer pending, or the time is
// up; keep it under the shell's own timeout and call again to keep waiting. A failed call is retried
// on the next poll, twice, so one TLS timeout does not end a wait. Right after a push, a check can
// take a minute to register, so for the first two minutes of a wait an absent required check counts
// as pending. `--moved-from <sha>` waits instead for the head to stop being that sha, as a
// catch-up that pushes later (`@dependabot rebase`) needs, then answers for the new head; until
// then the old head's `behind` or `conflicts` does not end the wait. The repository is the
// `Public repository` setting; this script reads no settings.
// Every command prints one JSON object, and exits 1 with `{ "error": ... }` on failure.
//
// What it handles, so no skill has to:
//   - Checks are read for the PR's current head by its full sha, never `gh pr checks --watch`,
//     which can report the previous head's run straight after a push.
//   - A required check that never reported is named (from the branch's rules), since GitHub then
//     shows the PR BLOCKED with every check green.
//   - A check still pending after its workflow run completed is marked `stuck`.
//   - A cancelled check with a passing check of the same name on the same head (a duplicate
//     `pull_request` event) is listed apart and is not a failure.
//   - `mergeStateStatus` UNKNOWN, which GitHub computes lazily, counts as pending.
//   - Runs are matched by sha, never `--limit 1`, which right after a merge returns the previous
//     merge's run.
//   - Every `__typename` is asked for as `typename`, since a dangling underscore fails some
//     consumers' lint.

import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const COMMANDS = ["pr", "release", "runs"];
const POLL_MS = 20_000;
const SETTLE_MS = 120_000;
const MAX_FAILED_CALLS = 2;
const FAILED = new Set(["FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "STALE", "ERROR"]);
const PASSED = new Set(["SUCCESS", "NEUTRAL", "SKIPPED"]);
const STAMP_PREFIX = "chore(release):";

export class LandError extends Error {}

// ---------------------------------------------------------------------------------------------
// Arguments

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  if (!COMMANDS.includes(command)) {
    throw new LandError(`expected one of ${COMMANDS.join(", ")}; got ${command ?? "(none)"}`);
  }
  const options = {
    command,
    repo: null,
    pr: null,
    sha: null,
    expectHead: null,
    movedFrom: null,
    wait: 0,
    workflows: [],
    checks: [],
  };
  for (let i = 0; i < rest.length; i += 1) {
    const flag = rest[i];
    const value = rest[i + 1];
    if (value === undefined || value.startsWith("--")) throw new LandError(`${flag} needs a value`);
    i += 1;
    switch (flag) {
      case "--repo":
        options.repo = parseRepo(value);
        break;
      case "--pr":
        options.pr = parsePositive(flag, value);
        break;
      case "--sha":
        options.sha = parseSha(flag, value);
        break;
      case "--expect-head":
        options.expectHead = parseSha(flag, value);
        break;
      case "--moved-from":
        options.movedFrom = parseSha(flag, value);
        break;
      case "--wait":
        options.wait = parseSeconds(value);
        break;
      case "--workflow":
        options.workflows.push(value);
        break;
      case "--check":
        options.checks.push(value);
        break;
      default:
        throw new LandError(`unknown option ${flag}`);
    }
  }
  if (!options.repo) throw new LandError("--repo <owner/name> is required");
  if (command === "pr" && !options.pr) throw new LandError("pr needs --pr <n>");
  if (options.expectHead && options.movedFrom) throw new LandError("give --expect-head or --moved-from, not both");
  if (command !== "pr" && (options.expectHead || options.movedFrom)) {
    throw new LandError("only pr takes --expect-head or --moved-from");
  }
  if (command !== "pr" && !options.sha) throw new LandError(`${command} needs --sha <merge sha>`);
  if (command === "release" && options.workflows.length !== 1) {
    throw new LandError("release needs exactly one --workflow <file>");
  }
  if (command === "runs" && options.workflows.length + options.checks.length === 0) {
    throw new LandError("runs needs at least one --workflow <file> or --check <name>");
  }
  return options;
}

function parseRepo(value) {
  const match = /^([\w.-]+)\/([\w.-]+)$/.exec(value);
  if (!match) throw new LandError(`--repo must be owner/name; got ${value}`);
  return { owner: match[1], name: match[2] };
}

function parsePositive(flag, value) {
  const number = Number(value.replace(/^#/, ""));
  if (!Number.isInteger(number) || number <= 0) throw new LandError(`${flag} must be a positive number; got ${value}`);
  return number;
}

function parseSha(flag, value) {
  // A short sha matches nothing in the runs API, which makes a wait end at once.
  if (!/^[0-9a-f]{40}$/i.test(value)) throw new LandError(`${flag} must be a full 40-character sha; got ${value}`);
  return value.toLowerCase();
}

function parseSeconds(value) {
  const seconds = Number(value);
  if (!Number.isFinite(seconds) || seconds < 0) throw new LandError(`--wait must be a number of seconds; got ${value}`);
  return seconds;
}

// ---------------------------------------------------------------------------------------------
// GitHub

function isRetryable(message) {
  return /secondary rate limit|abuse detection|HTTP 50[234]|timed? ?out|ECONNRESET|connection reset|TLS|unexpected EOF/i.test(
    message,
  );
}

function runGh(args, input, { allowNotFound = false } = {}) {
  const result = spawnSync("gh", args, {
    input,
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, MSYS_NO_PATHCONV: "1" },
  });
  if (result.error) throw new LandError(`could not run gh: ${result.error.message}`);
  let body = null;
  try {
    body = JSON.parse(result.stdout);
  } catch {
    // gh printed no JSON: report what it said.
  }
  // A lookup of numbers that may not exist asks for the found ones back with the misses as null.
  const allNotFound = body?.data && body.errors?.every((error) => error.type === "NOT_FOUND");
  if (allowNotFound && allNotFound) return body;
  if (result.status !== 0 || body?.errors) {
    const message =
      body?.errors?.map((error) => error.message).join("; ") || body?.message || result.stderr.trim() || result.stdout.trim();
    const error = new LandError(message);
    error.retryable = isRetryable(`${message} ${result.stderr}`);
    throw error;
  }
  return body;
}

export function ghGraphql(query, variables, options) {
  return runGh(["api", "graphql", "--input", "-"], JSON.stringify({ query, variables }), options).data;
}

export function ghRest(endpoint) {
  return runGh(["api", endpoint]);
}

function pause(ms) {
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
}

// ---------------------------------------------------------------------------------------------
// Pull requests

const PR_QUERY = `query Pr($owner: String!, $name: String!, $number: Int!) {
  repository(owner: $owner, name: $name) {
    pullRequest(number: $number) {
      number title url body state isDraft mergedAt mergeStateStatus mergeable
      headRefName headRefOid baseRefName
      author { typename: __typename login }
      mergeCommit { oid }
      closingIssuesReferences(first: 50) { nodes { number state repository { nameWithOwner } } }
      commits(last: 1) { nodes { commit { oid statusCheckRollup { contexts(first: 100) { nodes {
        typename: __typename
        ... on CheckRun {
          name status conclusion title detailsUrl isRequired(pullRequestNumber: $number)
          checkSuite { app { slug } workflowRun { databaseId event workflow { name } checkSuite { status } } }
        }
        ... on StatusContext { context state description targetUrl isRequired(pullRequestNumber: $number) }
      } } } } } }
    }
  }
}`;

const ISSUES_QUERY = (numbers) => `query Issues($owner: String!, $name: String!) {
  repository(owner: $owner, name: $name) {
    ${numbers.map((n) => `i${n}: issueOrPullRequest(number: ${n}) { typename: __typename ... on Issue { number state } ... on PullRequest { number state } }`).join("\n    ")}
  }
}`;

export function normalizeContext(node) {
  if (node.typename === "StatusContext") {
    const state = node.state === "SUCCESS" ? "passed" : FAILED.has(node.state) ? "failed" : "pending";
    return {
      name: node.context,
      kind: "status",
      state,
      conclusion: node.state,
      required: Boolean(node.isRequired),
      workflow: null,
      event: null,
      runId: null,
      title: node.description ?? null,
      url: node.targetUrl ?? null,
    };
  }
  const run = node.checkSuite?.workflowRun ?? null;
  const completed = node.status === "COMPLETED";
  const state = !completed ? "pending" : PASSED.has(node.conclusion) ? "passed" : "failed";
  const check = {
    name: node.name,
    kind: "check",
    state,
    conclusion: completed ? node.conclusion : node.status,
    required: Boolean(node.isRequired),
    workflow: run?.workflow?.name ?? node.checkSuite?.app?.slug ?? null,
    event: run?.event ?? null,
    runId: run?.databaseId ?? null,
    title: node.title ?? null,
    url: node.detailsUrl ?? null,
  };
  if (!completed && run?.checkSuite?.status === "COMPLETED") check.stuck = true;
  return check;
}

export function summarizeChecks(nodes, requiredContexts = []) {
  const items = nodes.map(normalizeContext);
  const passedNames = new Set(items.filter((check) => check.state === "passed").map((check) => check.name));
  const cancelledDuplicates = [];
  const failed = [];
  for (const check of items) {
    if (check.state !== "failed") continue;
    if (check.conclusion === "CANCELLED" && passedNames.has(check.name)) cancelledDuplicates.push(check.name);
    else failed.push(check.name);
  }
  const reported = new Set(items.map((check) => check.name));
  return {
    items,
    failed,
    pending: items.filter((check) => check.state === "pending").map((check) => check.name),
    stuck: items.filter((check) => check.stuck).map((check) => check.name),
    cancelledDuplicates,
    missingRequired: [...new Set(requiredContexts)].filter((context) => !reported.has(context)),
  };
}

export function parseRules(rules) {
  const parsed = { requiredContexts: [], strict: false, mergeMethods: null, codeScanning: false };
  for (const rule of rules ?? []) {
    const parameters = rule.parameters ?? {};
    if (rule.type === "required_status_checks") {
      for (const check of parameters.required_status_checks ?? []) parsed.requiredContexts.push(check.context);
      if (parameters.strict_required_status_checks_policy) parsed.strict = true;
    } else if (rule.type === "pull_request" && parameters.allowed_merge_methods) {
      const methods = parameters.allowed_merge_methods;
      parsed.mergeMethods = parsed.mergeMethods ? parsed.mergeMethods.filter((m) => methods.includes(m)) : [...methods];
    } else if (rule.type === "code_scanning") {
      parsed.codeScanning = true;
    }
  }
  parsed.requiredContexts = [...new Set(parsed.requiredContexts)];
  return parsed;
}

const KEYWORD = String.raw`(?:close[sd]?|fix(?:e[sd])?|resolve[sd]?)`;
const REF = String.raw`(?:([\w.-]+\/[\w.-]+))?#(\d+)`;

// A line holding only link references: `Closes #109`, `Part of #12.`, `Fixes #3 and closes #4`.
const LINK_ONLY = new RegExp(String.raw`^(?:(?:${KEYWORD}:?|part of)\s+${REF}[\s,.;]*(?:and\s+)?)+$`, "i");

// The link line the development flow asks a PR body to open with: `Closes #N` or `Part of #N`. A
// PR that finishes several issues may give each its own line at the top, so the link line runs on
// through each line directly below it that holds only link references. A blank line or any other
// text ends it: a keyword further down the body is still one the PR may not mean.
export function parseLinkLine(body, repo) {
  const lines = (body ?? "").split(/\r?\n/).map((text) => text.trim());
  const start = lines.findIndex((text) => text !== "");
  const block = [];
  if (start !== -1) {
    block.push(lines[start]);
    for (const text of lines.slice(start + 1)) {
      if (!LINK_ONLY.test(text)) break;
      block.push(text);
    }
  }
  const line = block.join("\n");
  const here = `${repo.owner}/${repo.name}`.toLowerCase();
  const refs = (pattern) =>
    [...line.matchAll(new RegExp(pattern, "gi"))].map((match) => ({
      repo: (match[1] ?? here).toLowerCase(),
      number: Number(match[2]),
    }));
  const closes = refs(String.raw`\b${KEYWORD}:?\s+${REF}`);
  const partOf = refs(String.raw`\bpart of\s+${REF}`);
  return { text: line, closes, partOf };
}

// Same-repository `#N` references in the body that the merge will not close.
export function mentionedNumbers(body, closingNumbers) {
  const numbers = new Set();
  for (const match of (body ?? "").matchAll(/(?<![\w/&])#(\d+)\b/g)) {
    const number = Number(match[1]);
    if (!closingNumbers.has(number)) numbers.add(number);
  }
  return [...numbers].sort((a, b) => a - b).slice(0, 30);
}

function readIssueStates(api, repo, numbers) {
  if (numbers.length === 0) return [];
  // A body can quote a number from another repository's notes; one that resolves to nothing here
  // comes back null and is dropped.
  const data = api.graphql(ISSUES_QUERY(numbers), { owner: repo.owner, name: repo.name }, { allowNotFound: true });
  return numbers
    .map((n) => data.repository[`i${n}`])
    .filter(Boolean)
    .map((node) => ({ number: node.number, kind: node.typename === "Issue" ? "issue" : "pr", state: node.state }));
}

export function prVerdict(pr, checks, expectHead) {
  if (pr.state === "MERGED") return "merged";
  if (pr.state === "CLOSED") return "closed";
  if (expectHead && pr.headRefOid !== expectHead) return "head-moved";
  if (pr.isDraft) return "draft";
  if (pr.mergeStateStatus === "DIRTY" || pr.mergeable === "CONFLICTING") return "conflicts";
  if (checks.failed.length > 0) return "failed";
  if (checks.pending.length > 0) return "pending";
  if (checks.missingRequired.length > 0) return "missing-checks";
  switch (pr.mergeStateStatus) {
    case "BEHIND":
      return "behind";
    case "BLOCKED":
      return "blocked";
    case "CLEAN":
    case "HAS_HOOKS":
    case "UNSTABLE":
      return "ready";
    default:
      return "pending";
  }
}

function readRules(api, repo, base) {
  try {
    return { rules: parseRules(api.rest(`repos/${repo.owner}/${repo.name}/rules/branches/${encodeURIComponent(base)}`)) };
  } catch (error) {
    if (error.retryable) throw error;
    return { rules: null, rulesError: error.message };
  }
}

function readAlerts(api, repo, number) {
  try {
    const alerts = api.rest(
      `repos/${repo.owner}/${repo.name}/code-scanning/alerts?ref=${encodeURIComponent(`refs/pull/${number}/head`)}&state=open&per_page=100`,
    );
    return alerts.map((alert) => ({
      number: alert.number,
      rule: alert.rule?.id ?? null,
      severity: alert.rule?.security_severity_level ?? alert.rule?.severity ?? null,
      path: alert.most_recent_instance?.location?.path ?? null,
      line: alert.most_recent_instance?.location?.start_line ?? null,
      url: alert.html_url ?? null,
    }));
  } catch (error) {
    if (error.retryable) throw error;
    return { error: error.message };
  }
}

export function readPr(api, options) {
  const { repo } = options;
  const data = api.graphql(PR_QUERY, { owner: repo.owner, name: repo.name, number: options.pr });
  const pr = data.repository?.pullRequest;
  if (!pr) throw new LandError(`${repo.owner}/${repo.name}#${options.pr} is not a pull request`);
  const { rules, rulesError } = readRules(api, repo, pr.baseRefName);
  const commit = pr.commits.nodes[0]?.commit;
  const checks = summarizeChecks(commit?.statusCheckRollup?.contexts?.nodes ?? [], rules?.requiredContexts ?? []);
  const here = `${repo.owner}/${repo.name}`.toLowerCase();
  const closing = pr.closingIssuesReferences.nodes.map((node) => ({
    repo: node.repository.nameWithOwner.toLowerCase(),
    number: node.number,
    state: node.state,
  }));
  const linkLine = parseLinkLine(pr.body, repo);
  // GitHub reads a closing keyword anywhere in the body, negated or not ("does not close #22"), so
  // every issue the merge will close must be one the link line names with a keyword.
  const named = new Set(linkLine.closes.map((ref) => `${ref.repo}#${ref.number}`));
  const unexpectedClosing = closing.filter((ref) => !named.has(`${ref.repo}#${ref.number}`));
  const closingHere = new Set(closing.filter((ref) => ref.repo === here).map((ref) => ref.number));
  const verdict = prVerdict(pr, checks, options.expectHead);
  const result = {
    repo: `${repo.owner}/${repo.name}`,
    number: pr.number,
    title: pr.title,
    url: pr.url,
    state: pr.state,
    isDraft: pr.isDraft,
    author: pr.author?.login ?? null,
    authorIsBot: pr.author?.typename === "Bot",
    dependabot: /^dependabot(\[bot\])?$/i.test(pr.author?.login ?? ""),
    headRefName: pr.headRefName,
    headSha: pr.headRefOid,
    baseRefName: pr.baseRefName,
    mergeStateStatus: pr.mergeStateStatus,
    mergeable: pr.mergeable,
    mergedAt: pr.mergedAt,
    mergeCommit: pr.mergeCommit?.oid ?? null,
    verdict,
    checks,
    rules,
    closing,
    linkLine,
    unexpectedClosing,
    // A bot's body quotes upstream release notes, whose numbers are not this repository's.
    mentioned: pr.author?.typename === "Bot" ? [] : readIssueStates(api, repo, mentionedNumbers(pr.body, closingHere)),
  };
  if (rulesError) result.rulesError = rulesError;
  if (verdict === "blocked") result.codeScanningAlerts = readAlerts(api, repo, pr.number);
  return result;
}

// ---------------------------------------------------------------------------------------------
// After the merge

const MAIN_QUERY = `query Main($owner: String!, $name: String!) {
  repository(owner: $owner, name: $name) {
    defaultBranchRef { name target { ... on Commit { history(first: 50) { nodes {
      oid messageHeadline parents(first: 2) { nodes { oid } }
    } } } } }
    refs(refPrefix: "refs/tags/", first: 50, orderBy: { field: TAG_COMMIT_DATE, direction: DESC }) {
      nodes { name target { typename: __typename oid ... on Tag { target { oid } } } }
    }
  }
}`;

// The merge's place on the default branch: the stamp commits directly above it, and anything else
// that has landed since.
export function locateMerge(history, sha) {
  const index = history.findIndex((commit) => commit.oid === sha);
  if (index < 0) return { onMain: false, stamps: [], landedSince: [] };
  const stamps = [];
  let below = sha;
  for (let i = index - 1; i >= 0; i -= 1) {
    const commit = history[i];
    if (commit.parents.nodes[0]?.oid !== below || !commit.messageHeadline.startsWith(STAMP_PREFIX)) break;
    stamps.push({ sha: commit.oid, headline: commit.messageHeadline });
    below = commit.oid;
  }
  const stampShas = new Set(stamps.map((stamp) => stamp.sha));
  const landedSince = history
    .slice(0, index)
    .filter((commit) => !stampShas.has(commit.oid))
    .map((commit) => ({ sha: commit.oid, headline: commit.messageHeadline }));
  return { onMain: true, stamps, landedSince };
}

export function tagsAt(refs, sha) {
  return refs
    .filter((ref) => (ref.target.typename === "Tag" ? ref.target.target?.oid : ref.target.oid) === sha)
    .map((ref) => ref.name);
}

function readMain(api, repo, sha) {
  const data = api.graphql(MAIN_QUERY, { owner: repo.owner, name: repo.name });
  const history = data.repository.defaultBranchRef.target.history.nodes;
  return {
    branch: data.repository.defaultBranchRef.name,
    head: history[0]?.oid ?? null,
    ...locateMerge(history, sha),
    tags: tagsAt(data.repository.refs.nodes, sha),
  };
}

function workflowMatches(run, workflow) {
  return run.path === workflow || run.path?.endsWith(`/${workflow}`) || run.name === workflow;
}

function describeRun(run) {
  return {
    id: run.id,
    name: run.name,
    path: run.path,
    event: run.event,
    headSha: run.head_sha,
    status: run.status,
    conclusion: run.conclusion,
    attempt: run.run_attempt ?? 1,
    createdAt: run.created_at,
    url: run.html_url,
  };
}

function runState(runs) {
  if (runs.length === 0) return "none";
  if (runs.some((run) => run.status !== "completed")) return "running";
  return runs.every((run) => ["success", "skipped", "neutral"].includes(run.conclusion)) ? "success" : "failed";
}

export function releaseVerdict(runs, tags) {
  const state = runState(runs);
  if (state === "none") return "no-run";
  if (state === "running") return "running";
  if (state === "failed") return "failed";
  return tags.length > 0 ? "released" : "untagged";
}

function readRelease(api, options) {
  const { repo, sha } = options;
  const [workflow] = options.workflows;
  const listed = api.rest(`repos/${repo.owner}/${repo.name}/actions/runs?head_sha=${sha}&per_page=100`).workflow_runs;
  const runs = listed.filter((run) => workflowMatches(run, workflow)).map(describeRun);
  const main = readMain(api, repo, sha);
  return { repo: `${repo.owner}/${repo.name}`, sha, workflow, verdict: releaseVerdict(runs, main.tags), runs, main };
}

function readRuns(api, options) {
  const { repo, sha } = options;
  const main = readMain(api, repo, sha);
  const shas = new Set([sha, ...main.stamps.map((stamp) => stamp.sha)]);
  const watched = [];
  for (const workflow of options.workflows) {
    const listed = api.rest(
      `repos/${repo.owner}/${repo.name}/actions/workflows/${encodeURIComponent(workflow)}/runs?per_page=30`,
    ).workflow_runs;
    const runs = listed.filter((run) => shas.has(run.head_sha)).map(describeRun);
    watched.push({ workflow, state: runState(runs), runs });
  }
  for (const name of options.checks) {
    const listed = api.rest(
      `repos/${repo.owner}/${repo.name}/commits/${sha}/check-runs?check_name=${encodeURIComponent(name)}&per_page=30`,
    ).check_runs;
    const runs = listed.map((run) => ({
      id: run.id,
      name: run.name,
      headSha: run.head_sha,
      status: run.status,
      conclusion: run.conclusion,
      title: run.output?.title ?? null,
      url: run.html_url ?? run.details_url ?? null,
    }));
    watched.push({ check: name, state: runState(runs), runs });
  }
  const states = watched.map((entry) => entry.state);
  const verdict = states.includes("failed")
    ? "failed"
    : states.includes("running")
      ? "running"
      : states.includes("none")
        ? "waiting"
        : "success";
  return { repo: `${repo.owner}/${repo.name}`, sha, verdict, watched, stamps: main.stamps, landedSince: main.landedSince };
}

// ---------------------------------------------------------------------------------------------
// Waiting

function stillWaiting(options, result, elapsed) {
  const { command } = options;
  const settling = elapsed < SETTLE_MS;
  if (command === "pr" && options.movedFrom && result.headSha === options.movedFrom) {
    return result.verdict !== "merged" && result.verdict !== "closed";
  }
  if (command === "pr") return result.verdict === "pending" || (settling && result.verdict === "missing-checks");
  if (command === "release") return result.verdict === "running" || (settling && result.verdict === "no-run");
  return result.verdict === "running" || (settling && result.verdict === "waiting");
}

const READERS = { pr: readPr, release: readRelease, runs: readRuns };

export function runCommand(argv, { graphql = ghGraphql, rest = ghRest, sleep = pause, now = Date.now } = {}) {
  const options = parseArgs(argv);
  const api = { graphql, rest };
  const read = READERS[options.command];
  const start = now();
  const deadline = start + options.wait * 1000;
  let failedCalls = 0;
  let lastError = null;
  for (;;) {
    let result = null;
    try {
      result = read(api, options);
      failedCalls = 0;
    } catch (error) {
      if (!error.retryable) throw error;
      failedCalls += 1;
      lastError = error;
      if (failedCalls > MAX_FAILED_CALLS || now() + POLL_MS > deadline) throw error;
    }
    if (result) {
      const elapsed = now() - start;
      if (!stillWaiting(options, result, elapsed) || now() + POLL_MS > deadline) {
        result.waitedSeconds = Math.round(elapsed / 1000);
        if (options.wait > 0) result.timedOut = stillWaiting(options, result, elapsed);
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
