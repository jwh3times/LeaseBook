import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  DEFAULT_TARGET,
  describeResult,
  isBranchCheckedOut,
  isDirty,
  main,
  parseArgs,
  syncRepositories,
} from "./sync-main.mjs";

function git(root, ...args) {
  return execFileSync(
    "git",
    [
      "-C",
      root,
      "-c",
      "user.name=t",
      "-c",
      "user.email=t@example.com",
      "-c",
      "commit.gpgsign=false",
      ...args,
    ],
    { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] },
  ).trim();
}

function commit(root, name) {
  writeFileSync(path.join(root, name), `${name}\n`);
  git(root, "add", "-A");
  git(root, "commit", "-m", name);
}

// A bare remote with one commit on main, and a helper that clones it.
function withRemote(run) {
  const dir = mkdtempSync(path.join(tmpdir(), "sync-main-"));
  const originalLog = console.log;
  console.log = () => {};
  try {
    const remote = path.join(dir, "remote.git");
    mkdirSync(remote);
    git(remote, "init", "--bare", "-b", "main");
    const clone = (name) => {
      const target = path.join(dir, name);
      execFileSync("git", ["clone", "-q", remote, target], { stdio: "ignore" });
      return target;
    };
    const publisher = clone("publisher");
    git(publisher, "switch", "-c", "main");
    commit(publisher, "seed");
    git(publisher, "push", "-u", "origin", "main");
    const publish = (name) => {
      commit(publisher, name);
      git(publisher, "push", "origin", "main");
    };
    return run({ dir, clone, publish });
  } finally {
    console.log = originalLog;
    rmSync(dir, { recursive: true, force: true });
  }
}

const publicRepository = (root) => ({ root, label: "public checkout", companion: false });
const companion = (root) => ({ root, label: "private companion", companion: true });

test("parses the flags and rejects anything else", () => {
  assert.deepEqual(parseArgs([]), { syncPrivate: true, target: DEFAULT_TARGET });
  assert.deepEqual(parseArgs(["--skip-private", "--target", "../ops"]), {
    syncPrivate: false,
    target: "../ops",
  });
  assert.throws(() => parseArgs(["--nope"]), /Unknown argument/);
  assert.throws(() => parseArgs(["--target"]), /requires a value/);
});

test("reads Git's plumbing output", () => {
  assert.equal(isDirty(""), false);
  assert.equal(isDirty(" M a\n"), true);
  assert.equal(isBranchCheckedOut("worktree /x\r\nbranch refs/heads/main\r\n"), true);
  assert.equal(isBranchCheckedOut("branch refs/heads/mainline\n"), false);
  assert.equal(
    describeResult({ label: "x", status: "updated", detail: "moved" }),
    "+ x: moved",
  );
});

test("fast-forwards main and reports the commits", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("work");
    publish("next");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.equal(result.status, "updated");
    assert.match(result.detail, /^main now at [0-9a-f]{7}, 1 new commit\(s\)$/);
    assert.equal(git(root, "rev-parse", "HEAD"), git(root, "rev-parse", "origin/main"));
  });
});

test("reports a checkout already current", () => {
  withRemote(({ clone }) => {
    const [result] = syncRepositories([publicRepository(clone("work"))]);
    assert.equal(result.status, "current");
  });
});

test("moves from another branch to main, and says so", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("work");
    git(root, "switch", "-c", "feature");
    publish("next");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.equal(result.status, "updated");
    assert.match(result.detail, /\(was on feature\)$/);
    assert.equal(git(root, "branch", "--show-current"), "main");
  });
});

test("creates a local main that is missing", () => {
  withRemote(({ clone }) => {
    const root = clone("work");
    git(root, "switch", "-c", "feature");
    git(root, "branch", "-D", "main");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.notEqual(result.status, "failed");
    assert.equal(git(root, "branch", "--show-current"), "main");
  });
});

test("refuses uncommitted changes, including untracked files, and does not fetch", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("work");
    const before = git(root, "rev-parse", "origin/main");
    publish("next");
    writeFileSync(path.join(root, "scratch.txt"), "work\n");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.equal(result.status, "failed");
    assert.match(result.detail, /uncommitted changes/);
    assert.equal(git(root, "rev-parse", "origin/main"), before);
  });
});

test("refuses a detached HEAD", () => {
  withRemote(({ clone }) => {
    const root = clone("work");
    git(root, "switch", "--detach");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.equal(result.status, "failed");
    assert.match(result.detail, /detached HEAD/);
  });
});

test("refuses a main that has diverged", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("work");
    commit(root, "local");
    publish("next");
    const [result] = syncRepositories([publicRepository(root)]);
    assert.equal(result.status, "failed");
    assert.match(result.detail, /^fast-forward from origin\/main failed: /);
  });
});

test("moves nothing when either repository refuses", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("work");
    const private_ = clone("companion");
    const before = git(root, "rev-parse", "HEAD");
    publish("next");
    writeFileSync(path.join(private_, "draft.txt"), "draft\n");
    const results = syncRepositories([publicRepository(root), companion(private_)]);
    assert.equal(results[0].status, "skipped");
    assert.match(results[0].detail, /another repository refused/);
    assert.equal(results[1].status, "failed");
    assert.equal(git(root, "rev-parse", "HEAD"), before);
  });
});

test("with main held by another worktree, fast-forwards a branch with no other commits", () => {
  withRemote(({ dir, clone, publish }) => {
    const root = clone("work");
    const linked = path.join(dir, "linked");
    git(root, "worktree", "add", "-q", "-b", "feature", linked);
    publish("next");
    const [result] = syncRepositories([publicRepository(linked)]);
    assert.equal(result.status, "updated");
    assert.match(result.detail, /^feature now at .*\(main is checked out elsewhere\)$/);
    assert.equal(git(linked, "branch", "--show-current"), "feature");
    assert.equal(git(linked, "rev-parse", "HEAD"), git(linked, "rev-parse", "origin/main"));
  });
});

test("with main held by another worktree, refuses a branch with other commits", () => {
  withRemote(({ dir, clone }) => {
    const root = clone("work");
    const linked = path.join(dir, "linked");
    git(root, "worktree", "add", "-q", "-b", "feature", linked);
    commit(linked, "work");
    const [result] = syncRepositories([publicRepository(linked)]);
    assert.equal(result.status, "failed");
    assert.match(result.detail, /feature has other commits/);
  });
});

test("companion results carry no revision, branch or raw Git error", () => {
  withRemote(({ clone, publish }) => {
    const root = clone("companion");
    git(root, "switch", "-c", "secret-branch");
    publish("next");
    const [updated] = syncRepositories([companion(root)]);
    assert.equal(updated.detail, "main fast-forwarded to origin/main");
    const [current] = syncRepositories([companion(root)]);
    assert.equal(current.detail, "main already matches origin/main");

    commit(root, "local");
    publish("later");
    const [failure] = syncRepositories([companion(root)]);
    assert.equal(failure.status, "failed");
    assert.equal(failure.detail, "fast-forward from origin/main failed; inspect it locally");
  });
});

test("refuses a companion that is a link or a linked worktree", () => {
  withRemote(({ dir, clone }) => {
    const root = clone("companion");
    const linkPath = path.join(dir, "link");
    symlinkSync(root, linkPath, "junction");
    const [viaLink] = syncRepositories([companion(linkPath)]);
    assert.equal(viaLink.status, "failed");
    assert.match(viaLink.detail, /independent Git clone/);

    const linked = path.join(dir, "linked");
    git(root, "worktree", "add", "-q", "-b", "other", linked);
    const [viaWorktree] = syncRepositories([companion(linked)]);
    assert.equal(viaWorktree.status, "failed");
    assert.match(viaWorktree.detail, /independent Git clone/);
  });
});

test("main syncs the companion beside the checkout, or reports it missing", () => {
  withRemote(({ dir, clone, publish }) => {
    const root = clone("work");
    assert.equal(main([], root), 0);

    const companionRoot = clone("work/private");
    git(root, "status");
    writeFileSync(path.join(root, ".git", "info", "exclude"), "private/\n");
    publish("next");
    assert.equal(main([], root), 0);
    assert.equal(git(companionRoot, "rev-parse", "HEAD"), git(root, "rev-parse", "HEAD"));

    publish("later");
    const before = git(companionRoot, "rev-parse", "HEAD");
    assert.equal(main(["--skip-private"], root), 0);
    assert.equal(git(companionRoot, "rev-parse", "HEAD"), before);
    assert.ok(dir);
  });
});

test("main returns a failure code when a repository refuses", () => {
  withRemote(({ clone }) => {
    const root = clone("work");
    writeFileSync(path.join(root, "scratch.txt"), "work\n");
    assert.equal(main(["--skip-private"], root), 1);
  });
});
