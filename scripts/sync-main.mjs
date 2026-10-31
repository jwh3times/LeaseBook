#!/usr/bin/env node
// Bring this checkout and its private companion to the latest origin/main.
//
//   node scripts/sync-main.mjs                  # both repositories
//   node scripts/sync-main.mjs --skip-private   # this checkout only
//   node scripts/sync-main.mjs --target ../ops  # a companion somewhere other than private/
//
// Both repositories are checked before either moves, so a refusal leaves them as they were. Then
// each one fetches origin, moves to main, and fast-forwards to origin/main. It refuses uncommitted
// changes, a detached HEAD and a diverged main; it never stashes, merges or rewrites history. A
// missing companion is reported and skipped, since a public clone has none.
//
// When another linked worktree holds main, this one cannot switch to it. It fast-forwards its own
// branch instead, provided that branch has no commits outside origin/main.
//
// Companion results never print a revision, a branch or a raw Git error: this output lands in public
// logs, and the companion's metadata stays out of them.
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { spawnSync } from "node:child_process";
import { existsSync, lstatSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const TARGET_BRANCH = "main";
export const DEFAULT_TARGET = "private";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const REMOTE_BRANCH = `origin/${TARGET_BRANCH}`;

export function parseArgs(argv) {
  const options = { syncPrivate: true, target: DEFAULT_TARGET };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--skip-private") {
      options.syncPrivate = false;
      continue;
    }
    if (argument !== "--target") throw new Error(`Unknown argument: ${argument}`);
    const value = argv[index + 1];
    if (!value) throw new Error(`${argument} requires a value.`);
    options.target = value;
    index += 1;
  }
  return options;
}

export function isDirty(porcelain) {
  return porcelain.trim().length > 0;
}

export function isBranchCheckedOut(worktreePorcelain, branch = TARGET_BRANCH) {
  return worktreePorcelain
    .split(/\r?\n/)
    .some((line) => line.trim() === `branch refs/heads/${branch}`);
}

const marks = { updated: "+", current: "=", skipped: "-", failed: "x" };

export function describeResult(result) {
  return `${marks[result.status]} ${result.label}: ${result.detail}`;
}

function withoutGitTracing(environment) {
  const clean = { ...environment };
  for (const name of Object.keys(clean)) {
    if (/^GIT_TRACE|^GIT_CURL_VERBOSE$/.test(name) || name.toUpperCase().startsWith("OP_")) {
      delete clean[name];
    }
  }
  return clean;
}

export function runGit(root, args) {
  const result = spawnSync("git", ["-C", root, ...args], {
    encoding: "utf8",
    env: withoutGitTracing(process.env),
    windowsHide: true,
  });
  return {
    status: result.status ?? 1,
    stdout: result.stdout ?? "",
    stderr: result.stderr || result.error?.message || "",
  };
}

function firstLine(...texts) {
  for (const text of texts) {
    const line = text
      .split(/\r?\n/)
      .map((part) => part.trim())
      .find(Boolean);
    if (line) return line;
  }
  return "Git returned no output";
}

function failed(repository, detail) {
  return { label: repository.label, status: "failed", detail };
}

function gitFailure(repository, operation, result) {
  return failed(
    repository,
    repository.companion
      ? `${operation} failed; inspect it locally`
      : `${operation} failed: ${firstLine(result.stderr, result.stdout)}`,
  );
}

function isIndependentClone(root) {
  const gitPath = path.join(root, ".git");
  return (
    !lstatSync(root).isSymbolicLink() &&
    lstatSync(gitPath).isDirectory() &&
    !lstatSync(gitPath).isSymbolicLink()
  );
}

// Returns a result when this repository must not move, and null when it may.
export function preflightRepository(repository, git = runGit) {
  const { root, label, companion } = repository;
  if (!existsSync(path.join(root, ".git"))) {
    return { label, status: "skipped", detail: `no Git repository at ${root}` };
  }
  if (companion && !isIndependentClone(root)) {
    return failed(repository, "the path is not an independent Git clone");
  }

  const prefix = git(root, ["rev-parse", "--show-prefix"]);
  if (prefix.status !== 0) return gitFailure(repository, "repository check", prefix);
  if (prefix.stdout.trim()) return failed(repository, "the path is not a Git root");

  const status = git(root, ["status", "--porcelain", "--untracked-files=all"]);
  if (status.status !== 0) return gitFailure(repository, "status", status);
  if (isDirty(status.stdout)) {
    return failed(repository, "uncommitted changes; commit or stash them, then run this again");
  }

  const branch = git(root, ["branch", "--show-current"]);
  if (branch.status !== 0) return gitFailure(repository, "branch check", branch);
  if (!branch.stdout.trim()) {
    return failed(repository, "detached HEAD; switch to a branch, then run this again");
  }
  return null;
}

// Moves to main, or decides to stay on this branch. Returns a failure, or whether it stayed.
function moveToMain(repository, previousBranch, git) {
  const { root } = repository;
  const localMain = git(root, ["show-ref", "--verify", "--quiet", `refs/heads/${TARGET_BRANCH}`]);
  if (localMain.status !== 0) {
    const create = git(root, ["switch", "--create", TARGET_BRANCH, "--track", REMOTE_BRANCH]);
    return create.status === 0 ? {} : { failure: gitFailure(repository, "switch to main", create) };
  }

  const worktrees = git(root, ["worktree", "list", "--porcelain"]);
  if (worktrees.status !== 0) {
    return { failure: gitFailure(repository, "worktree list", worktrees) };
  }
  if (isBranchCheckedOut(worktrees.stdout)) {
    const contained = git(root, ["merge-base", "--is-ancestor", "HEAD", REMOTE_BRANCH]);
    if (contained.status === 0) return { stayed: true };
    return {
      failure: failed(
        repository,
        repository.companion
          ? "main is checked out in another worktree and this branch has other commits"
          : `main is checked out in another worktree and ${previousBranch} has other commits`,
      ),
    };
  }

  const move = git(root, ["switch", TARGET_BRANCH]);
  return move.status === 0 ? {} : { failure: gitFailure(repository, "switch to main", move) };
}

function updateRepository(repository, git) {
  const { root, label, companion } = repository;
  const fetch = git(root, ["fetch", "--prune", "origin"]);
  if (fetch.status !== 0) return gitFailure(repository, "fetch from origin", fetch);

  const remote = git(root, ["show-ref", "--verify", "--quiet", `refs/remotes/${REMOTE_BRANCH}`]);
  if (remote.status !== 0) return failed(repository, `${REMOTE_BRANCH} does not exist`);

  const previousBranch = git(root, ["branch", "--show-current"]).stdout.trim();
  let stayed = false;
  if (previousBranch !== TARGET_BRANCH) {
    const moved = moveToMain(repository, previousBranch, git);
    if (moved.failure) return moved.failure;
    stayed = Boolean(moved.stayed);
  }

  const before = git(root, ["rev-parse", "HEAD"]);
  if (before.status !== 0) return gitFailure(repository, "revision check", before);

  const merge = git(root, ["merge", "--ff-only", REMOTE_BRANCH]);
  if (merge.status !== 0) return gitFailure(repository, `fast-forward from ${REMOTE_BRANCH}`, merge);

  const after = git(root, ["rev-parse", "HEAD"]);
  if (after.status !== 0) return gitFailure(repository, "revision check", after);
  const [from, to] = [before.stdout.trim(), after.stdout.trim()];
  const status = from === to ? "current" : "updated";

  if (companion) {
    const where = stayed ? "this worktree's branch (main is checked out elsewhere)" : TARGET_BRANCH;
    const verb = from === to ? "already matches" : "fast-forwarded to";
    return { label, status, detail: `${where} ${verb} ${REMOTE_BRANCH}` };
  }

  const name = stayed ? previousBranch : TARGET_BRANCH;
  const context = stayed
    ? " (main is checked out elsewhere)"
    : previousBranch === TARGET_BRANCH
      ? ""
      : ` (was on ${previousBranch})`;
  if (from === to) {
    return {
      label,
      status,
      detail: `${name} already matches ${REMOTE_BRANCH} at ${to.slice(0, 7)}${context}`,
    };
  }
  const count = git(root, ["rev-list", "--count", `${from}..${to}`]).stdout.trim();
  const commits = count && count !== "0" ? `, ${count} new commit(s)` : "";
  return { label, status, detail: `${name} now at ${to.slice(0, 7)}${commits}${context}` };
}

export function syncRepositories(repositories, git = runGit) {
  const preflights = repositories.map((repository) => preflightRepository(repository, git));
  if (preflights.some((result) => result?.status === "failed")) {
    return preflights.map(
      (result, index) =>
        result ?? {
          label: repositories[index].label,
          status: "skipped",
          detail: "not updated, because another repository refused",
        },
    );
  }
  return repositories.map(
    (repository, index) => preflights[index] ?? updateRepository(repository, git),
  );
}

export function main(argv, root = repositoryRoot) {
  const options = parseArgs(argv);
  const repositories = [{ root, label: "public checkout", companion: false }];
  const skipped = [];

  if (options.syncPrivate) {
    const companionRoot = path.resolve(root, options.target);
    if (existsSync(path.join(companionRoot, ".git"))) {
      repositories.push({ root: companionRoot, label: "private companion", companion: true });
    } else {
      skipped.push({
        label: "private companion",
        status: "skipped",
        detail: `not installed at ${options.target}`,
      });
    }
  }

  const results = [...syncRepositories(repositories), ...skipped];
  for (const result of results) console.log(describeResult(result));
  return results.some((result) => result.status === "failed") ? 1 : 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
