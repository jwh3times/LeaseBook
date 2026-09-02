#!/usr/bin/env node

/**
 * Bring the public LeaseBook checkout and its optional private companion to
 * the latest origin/main, in one command.
 *
 * Usage (from the public repository root):
 *
 *   npm run sync:main
 *   npm run sync:main -- --skip-private
 *
 * Per repository, the script fetches and prunes origin, switches to main, and
 * fast-forwards main to origin/main.
 *
 * It deliberately refuses repositories with uncommitted changes and refuses
 * non-fast-forward updates. It never stashes work or creates a merge commit.
 * A missing private companion is reported and skipped because public clones
 * legitimately may not have one.
 */

import { spawnSync } from "node:child_process";
import { existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const MAIN_BRANCH = "main";

export function parseArgs(argv) {
  let syncPrivate = true;

  for (const argument of argv) {
    if (argument === "--skip-private") {
      syncPrivate = false;
      continue;
    }

    throw new Error(`Unknown argument: ${argument}`);
  }

  return { syncPrivate };
}

export function isDirty(porcelain) {
  return porcelain.trim().length > 0;
}

const marks = {
  updated: "+",
  current: "=",
  skipped: "-",
  failed: "x",
};

export function describeResult(result) {
  return `${marks[result.status]} ${result.label}: ${result.detail}`;
}

export function runGit(root, args) {
  const result = spawnSync("git", [...args], {
    cwd: root,
    encoding: "utf8",
    windowsHide: true,
  });

  return {
    status: result.status ?? 1,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? result.error?.message ?? "",
  };
}

function firstLine(text) {
  const line = text
    .split(/\r?\n/u)
    .map((value) => value.trim())
    .find((value) => value.length > 0);

  return line ?? "no output";
}

function failure(label, command) {
  return {
    label,
    status: "failed",
    detail: firstLine(command.stderr || command.stdout),
  };
}

export function syncRepository(root, label, git = runGit) {
  if (!existsSync(join(root, ".git"))) {
    return {
      label,
      status: "skipped",
      detail: `no Git repository at ${root}`,
    };
  }

  const status = git(root, ["status", "--porcelain"]);
  if (status.status !== 0) return failure(label, status);
  if (isDirty(status.stdout)) {
    return {
      label,
      status: "failed",
      detail: "uncommitted changes; commit or stash them, then run this again",
    };
  }

  const fetch = git(root, ["fetch", "--prune", "origin"]);
  if (fetch.status !== 0) return failure(label, fetch);

  const previousBranchResult = git(root, ["rev-parse", "--abbrev-ref", "HEAD"]);
  if (previousBranchResult.status !== 0)
    return failure(label, previousBranchResult);

  const previousBranch = previousBranchResult.stdout.trim();
  if (previousBranch !== MAIN_BRANCH) {
    const switchBranch = git(root, ["switch", MAIN_BRANCH]);
    if (switchBranch.status !== 0) return failure(label, switchBranch);
  }

  const beforeResult = git(root, ["rev-parse", "HEAD"]);
  if (beforeResult.status !== 0) return failure(label, beforeResult);
  const before = beforeResult.stdout.trim();

  const merge = git(root, ["merge", "--ff-only", `origin/${MAIN_BRANCH}`]);
  if (merge.status !== 0) return failure(label, merge);

  const afterResult = git(root, ["rev-parse", "HEAD"]);
  if (afterResult.status !== 0) return failure(label, afterResult);
  const after = afterResult.stdout.trim();
  const switched =
    previousBranch === MAIN_BRANCH ? "" : ` (was on ${previousBranch})`;

  if (before === after) {
    return {
      label,
      status: "current",
      detail: `${MAIN_BRANCH} already up to date at ${after.slice(0, 7)}${switched}`,
    };
  }

  const count = git(root, ["rev-list", "--count", `${before}..${after}`]);
  const commitCount = count.status === 0 ? count.stdout.trim() : "";
  const commits =
    commitCount && commitCount !== "0" ? `, ${commitCount} new commit(s)` : "";

  return {
    label,
    status: "updated",
    detail: `${MAIN_BRANCH} now at ${after.slice(0, 7)}${commits}${switched}`,
  };
}

export function main(argv) {
  const options = parseArgs(argv);
  const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
  const results = [syncRepository(repositoryRoot, "LeaseBook public")];

  if (options.syncPrivate) {
    const privateRoot = join(repositoryRoot, "private");
    if (existsSync(join(privateRoot, ".git"))) {
      results.push(syncRepository(privateRoot, "LeaseBook private"));
    } else {
      results.push({
        label: "LeaseBook private",
        status: "skipped",
        detail:
          "not installed at private/; authorized maintainers can run `npm run bootstrap:private`",
      });
    }
  }

  for (const result of results) console.log(describeResult(result));
  return results.some((result) => result.status === "failed") ? 1 : 0;
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(resolve(process.argv[1])).href
) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(error instanceof Error ? error.message : String(error));
    process.exitCode = 1;
  }
}
