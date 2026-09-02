import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import os from "node:os";
import path from "node:path";
import { afterEach, describe, it } from "node:test";
import {
  describeResult,
  isDirty,
  parseArgs,
  syncRepository,
} from "./sync-main.mjs";

const scratch = [];

function tempDir() {
  const directory = mkdtempSync(path.join(os.tmpdir(), "leasebook-sync-main-"));
  scratch.push(directory);
  return directory;
}

afterEach(() => {
  for (const directory of scratch.splice(0)) {
    rmSync(directory, { recursive: true, force: true });
  }
});

function git(root, ...args) {
  return execFileSync("git", args, { cwd: root, encoding: "utf8" });
}

function repoPair() {
  const base = tempDir();
  const origin = path.join(base, "origin.git");
  const seed = path.join(base, "seed");
  const clone = path.join(base, "clone");

  git(base, "init", "--bare", "--initial-branch=main", origin);
  git(base, "clone", origin, seed);
  git(seed, "config", "user.email", "test@example.com");
  git(seed, "config", "user.name", "Test");
  writeFileSync(path.join(seed, "README.md"), "one\n");
  git(seed, "add", ".");
  git(seed, "commit", "-m", "one");
  git(seed, "push", "origin", "main");
  git(base, "clone", origin, clone);
  git(clone, "config", "user.email", "test@example.com");
  git(clone, "config", "user.name", "Test");

  return { origin, clone };
}

function pushCommit(origin, message) {
  const work = path.join(tempDir(), "work");
  git(path.dirname(work), "clone", origin, work);
  git(work, "config", "user.email", "test@example.com");
  git(work, "config", "user.name", "Test");
  writeFileSync(path.join(work, `${message}.txt`), `${message}\n`);
  git(work, "add", ".");
  git(work, "commit", "-m", message);
  git(work, "push", "origin", "main");
}

describe("parseArgs", () => {
  it("defaults to syncing both repositories", () => {
    assert.deepEqual(parseArgs([]), { syncPrivate: true });
  });

  it("can skip the private companion", () => {
    assert.deepEqual(parseArgs(["--skip-private"]), { syncPrivate: false });
  });

  it("rejects unknown arguments", () => {
    assert.throws(() => parseArgs(["--pull"]), /Unknown argument/u);
  });
});

describe("small helpers", () => {
  it("treats only non-empty porcelain output as dirty", () => {
    assert.equal(isDirty(""), false);
    assert.equal(isDirty("\n"), false);
    assert.equal(isDirty(" M README.md\n"), true);
  });

  it("prefixes a result with its outcome", () => {
    assert.equal(
      describeResult({ label: "private", status: "updated", detail: "done" }),
      "+ private: done",
    );
  });
});

describe("syncRepository", () => {
  it("skips a directory that is not a Git repository", () => {
    const result = syncRepository(tempDir(), "companion");
    assert.equal(result.status, "skipped");
    assert.match(result.detail, /no Git repository/u);
  });

  it("fast-forwards main and reports the new commits", () => {
    const { origin, clone } = repoPair();
    pushCommit(origin, "two");

    const result = syncRepository(clone, "clone");

    assert.equal(result.status, "updated");
    assert.match(result.detail, /main now at/u);
    assert.match(result.detail, /1 new commit\(s\)/u);
    assert.equal(git(clone, "branch", "--show-current").trim(), "main");
  });

  it("switches a clean feature checkout back to main", () => {
    const { clone } = repoPair();
    git(clone, "switch", "-c", "feature/thing");

    const result = syncRepository(clone, "clone");

    assert.equal(result.status, "current");
    assert.match(result.detail, /was on feature\/thing/u);
    assert.equal(git(clone, "branch", "--show-current").trim(), "main");
  });

  it("refuses uncommitted changes without switching branches", () => {
    const { clone } = repoPair();
    git(clone, "switch", "-c", "feature/thing");
    writeFileSync(path.join(clone, "README.md"), "edited\n");

    const result = syncRepository(clone, "clone");

    assert.equal(result.status, "failed");
    assert.match(result.detail, /uncommitted changes/u);
    assert.equal(
      git(clone, "branch", "--show-current").trim(),
      "feature/thing",
    );
  });

  it("refuses a diverged main without creating a merge commit", () => {
    const { origin, clone } = repoPair();
    writeFileSync(path.join(clone, "local.txt"), "local\n");
    git(clone, "add", ".");
    git(clone, "commit", "-m", "local only");
    pushCommit(origin, "remote only");

    const result = syncRepository(clone, "clone");

    assert.equal(result.status, "failed");
    assert.match(git(clone, "log", "--oneline", "-1"), /local only/u);
  });
});
