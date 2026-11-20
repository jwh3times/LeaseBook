import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  TRAILER,
  insertSections,
  isStampCommit,
  main,
  mergeCategories,
  parseArgs,
  prTitleEntry,
  renderSection,
  salvageFragment,
  updateFooter,
} from "./stamp-changelog.mjs";
import { parseFragment } from "./check-changelog.mjs";

const changelog = `# Changelog

## [Unreleased]

No unreleased changes.

## [1.2.0] - 2026-11-01

### Added

- The first thing.
`;

test("parses --dry-run and --notes-dir, and rejects anything else", () => {
  assert.deepEqual(parseArgs([]), { dryRun: false, notesDir: null });
  assert.deepEqual(parseArgs(["--dry-run", "--notes-dir", "out"]), { dryRun: true, notesDir: "out" });
  assert.throws(() => parseArgs(["--notes-dir"]), /requires a value/);
  assert.throws(() => parseArgs(["--push"]), /Unknown argument/);
});

test("a stamp commit has the stamp subject and ends in the trailer", () => {
  assert.equal(isStampCommit(`chore(release): v1.2.1 [skip ci]\n\n${TRAILER}\n`), true);
  assert.equal(isStampCommit(`chore(release): v1.2.1, v1.2.2 [skip ci]\r\n\r\n${TRAILER}\r\n`), true);
  assert.equal(isStampCommit("chore(release): cut 1.3.0 — the new line\n"), false);
  // A squash commit quoting a stamp commit among the PR's commits is a merge to release.
  const squash = `Add a (#21)\n\n* chore(release): v1.2.1 [skip ci]\n\n${TRAILER}\n\n* Add a\n`;
  assert.equal(isStampCommit(squash), false);
});

test("a malformed fragment is salvaged under Changed, never blocking a release", () => {
  assert.deepEqual([...salvageFragment("\uFEFF## [1.2.1]\n\n- X.\n  more\n")], [
    ["Changed", ["- X.", "  more"]],
  ]);
  assert.equal(salvageFragment("## Nothing\n\n"), null);
});

test("a PR title entry comes from a merge commit's body or a squash commit's subject", () => {
  assert.equal(
    prTitleEntry("Merge pull request #13 from o/dependabot/npm/vite\n\nBump vite from 7.1.2 to 7.1.4\n"),
    "- Bump vite from 7.1.2 to 7.1.4 (#13)",
  );
  assert.equal(prTitleEntry("Merge pull request #14 from o/x\n"), "- Merge pull request #14 from o/x (#14)");
  assert.equal(prTitleEntry("docs: fix a typo (#15)\r\n\r\nbody\r\n"), "- docs: fix a typo (#15)");
});

test("categories merge across fragments in Keep a Changelog's order, others last", () => {
  const merged = mergeCategories([
    parseFragment("### Fixed\n\n- A fix.\n\n### Notes\n\n- A note.\n").categories,
    parseFragment("### Added\n\n- A feature.\n  continued.\n\n### Fixed\n\n- Another fix.\n").categories,
  ]);
  assert.deepEqual([...merged.keys()], ["Added", "Fixed", "Notes"]);
  assert.deepEqual(merged.get("Fixed"), ["- A fix.", "- Another fix."]);
  assert.equal(
    renderSection({ version: "1.2.1", date: "2026-11-18", categories: merged }),
    "## [1.2.1] - 2026-11-18\n\n### Added\n\n- A feature.\n  continued.\n\n### Fixed\n\n- A fix.\n" +
      "- Another fix.\n\n### Notes\n\n- A note.\n",
  );
});

test("sections go below Unreleased, newest first, keeping CRLF endings", () => {
  const two = ["## [1.2.2] - 2026-11-18\n\n- Two.\n", "## [1.2.1] - 2026-11-18\n\n- One.\n"];
  const result = insertSections(changelog, two);
  assert.match(result, /No unreleased changes\.\n\n## \[1\.2\.2\][^]*- Two\.\n\n## \[1\.2\.1\][^]*- One\.\n\n## \[1\.2\.0\]/);
  const crlf = insertSections(changelog.replace(/\n/g, "\r\n"), two);
  assert.doesNotMatch(crlf, /[^\r]\n/);
  assert.equal(crlf.replace(/\r\n/g, "\n"), result);
  assert.match(insertSections("# Changelog\n\n## [1.0.0] - 2026-01-01\n", two), /^# Changelog\n\n## \[1\.2\.2\]/);
});

test("the link footer compares from the newest version, with a link for each new one", () => {
  const footer = `${changelog}\n[Unreleased]: https://g/o/r/compare/v1.2.0...HEAD\n[1.2.0]: https://g/o/r/releases/tag/v1.2.0\n`;
  const stamped = updateFooter(footer, ["1.2.1", "1.2.2"]);
  assert.match(
    stamped,
    /\[Unreleased\]: https:\/\/g\/o\/r\/compare\/v1\.2\.2\.\.\.HEAD\n\[1\.2\.2\]: https:\/\/g\/o\/r\/compare\/v1\.2\.1\.\.\.v1\.2\.2\n\[1\.2\.1\]: https:\/\/g\/o\/r\/compare\/v1\.2\.0\.\.\.v1\.2\.1\n\[1\.2\.0\]:/,
  );
  assert.equal(updateFooter(changelog, ["1.2.1"]), changelog);
});

function withRepository(run) {
  const root = mkdtempSync(path.join(tmpdir(), "stamp-changelog-"));
  const originalLog = console.log;
  const originalError = console.error;
  const printed = [];
  console.log = (line) => printed.push(String(line));
  console.error = () => {};
  try {
    return run(root, printed);
  } finally {
    console.log = originalLog;
    console.error = originalError;
    rmSync(root, { recursive: true, force: true });
  }
}

function write(root, rel, content) {
  mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  writeFileSync(path.join(root, rel), content);
}

function git(root, ...args) {
  return execFileSync("git", ["-c", "core.quotePath=false", "-C", root, ...args], { encoding: "utf8" }).trim();
}

function repository(root) {
  git(root, "init", "-q", "-b", "main");
  git(root, "config", "user.name", "t");
  git(root, "config", "user.email", "t@example.com");
  git(root, "config", "commit.gpgsign", "false");
  git(root, "config", "tag.gpgsign", "false");
  write(root, "docs/agents/releases.md", "## Scheme\n\nstamp-on-tag\n\n## Version file\n\n`VERSION`\n");
  write(root, "VERSION", "1.2.0\n");
  write(root, "CHANGELOG.md", changelog);
  git(root, "add", "-A");
  git(root, "commit", "-q", "-m", "init");
  git(root, "tag", "-a", "v1.2.0", "-m", "Release v1.2.0");
}

// Merges a branch the way GitHub's merge button does: a merge commit naming the PR, the title as
// its body.
function mergePullRequest(root, number, branch, title, files) {
  git(root, "switch", "-q", "-c", branch);
  for (const [rel, content] of Object.entries(files)) write(root, rel, content);
  git(root, "add", "-A");
  git(root, "commit", "-q", "-m", title);
  git(root, "switch", "-q", "main");
  git(root, "merge", "-q", "--no-ff", branch, "-m", `Merge pull request #${number} from o/${branch}`, "-m", title);
  return git(root, "rev-parse", "HEAD");
}

test("stamps each untagged merge with its own version and tags the merge commit", () => {
  withRepository((root, printed) => {
    repository(root);
    const first = mergePullRequest(root, 12, "feat/x", "Add x", {
      "src/x.txt": "x\n",
      "changelog.d/feat-x.md": "### Added\n\n- X, for everyone.\n",
    });
    const second = mergePullRequest(root, 13, "dependabot/npm/vite", "Bump vite from 7.1.2 to 7.1.4", {
      "package-lock.json": "{}\n",
    });

    assert.equal(main(["--notes-dir", path.join(root, ".git", "notes")], root), 0);
    assert.deepEqual(printed, [`v1.2.1 ${first}`, `v1.2.2 ${second}`]);
    assert.equal(git(root, "rev-parse", "v1.2.1^{commit}"), first);
    assert.equal(git(root, "rev-parse", "v1.2.2^{commit}"), second);

    const stamped = readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
    assert.match(stamped, /## \[1\.2\.2\] - \d{4}-\d{2}-\d{2}\n\n### Changed\n\n- Bump vite from 7\.1\.2 to 7\.1\.4 \(#13\)\n\n## \[1\.2\.1\] - \d{4}-\d{2}-\d{2}\n\n### Added\n\n- X, for everyone\.\n\n## \[1\.2\.0\]/);
    assert.match(stamped, /## \[Unreleased\]\n\nNo unreleased changes\.\n/);
    assert.equal(existsSync(path.join(root, "changelog.d/feat-x.md")), false);
    assert.equal(readFileSync(path.join(root, ".git", "notes", "v1.2.1.md"), "utf8"), "### Added\n\n- X, for everyone.\n");

    const message = git(root, "log", "-1", "--format=%B");
    assert.match(message, /^chore\(release\): v1\.2\.1, v1\.2\.2 \[skip ci\]/);
    assert.equal(message.trimEnd().split("\n").at(-1), TRAILER);
    assert.equal(git(root, "status", "--porcelain"), "");

    printed.length = 0;
    assert.equal(main([], root), 0);
    assert.deepEqual(printed, []);
  });
});

test("walks past the stamp commit to the next untagged merge, and follows a new line", () => {
  withRepository((root, printed) => {
    repository(root);
    mergePullRequest(root, 12, "feat/x", "Add x", { "changelog.d/feat-x.md": "### Added\n\n- X.\n" });
    main([], root);
    const minor = mergePullRequest(root, 14, "feat/y", "Add y", {
      VERSION: "1.3.0\n",
      "changelog.d/feat-y.md": "### Added\n\n- Y.\n",
    });
    printed.length = 0;
    assert.equal(main([], root), 0);
    assert.deepEqual(printed, [`v1.3.0 ${minor}`]);
  });
});

test("a run whose merges added no fragment deletes nothing", () => {
  withRepository((root, printed) => {
    repository(root);
    write(root, "changelog.d/README.md", "Fragments.\n");
    git(root, "add", "-A");
    git(root, "commit", "-q", "-m", "docs: explain fragments");
    git(root, "tag", "-a", "v1.2.1", "-m", "Release v1.2.1");
    const bump = mergePullRequest(root, 13, "dependabot/npm/vite", "Bump vite", { "a.txt": "a\n" });
    assert.equal(main([], root), 0);
    assert.deepEqual(printed, [`v1.2.2 ${bump}`]);
    assert.deepEqual(git(root, "show", "--name-status", "--format=", "HEAD").split("\n"), [
      "M\tCHANGELOG.md",
    ]);
    assert.equal(existsSync(path.join(root, "changelog.d/README.md")), true);
    assert.equal(existsSync(path.join(root, "VERSION")), true);
  });
});

test("a dry run prints the plan and changes nothing", () => {
  withRepository((root, printed) => {
    repository(root);
    const head = mergePullRequest(root, 12, "feat/x", "Add x", { "changelog.d/feat-x.md": "### Fixed\n\n- X.\n" });
    assert.equal(main(["--dry-run"], root), 0);
    assert.equal(printed[0], `v1.2.1 ${head}`);
    assert.match(printed[1], /## \[1\.2\.1\] - [\d-]+\n\n### Fixed\n\n- X\./);
    assert.equal(git(root, "rev-parse", "HEAD"), head);
    assert.equal(git(root, "tag", "--list", "v1.2.1"), "");
  });
});

test("releases a malformed fragment's lines, and refuses a history with no release tag", () => {
  withRepository((root) => {
    repository(root);
    mergePullRequest(root, 12, "feat/x", "Add x", { "changelog.d/feat-x.md": "## [1.2.1]\n\n- X.\n" });
    assert.equal(main(["--dry-run"], root), 0);
    git(root, "tag", "-d", "v1.2.0");
    assert.throws(() => main([], root), /No three-part tag/);
  });
});

test("fragment names are literal, unquoted paths, and a re-added name counts as added", () => {
  withRepository((root, printed) => {
    repository(root);
    write(root, "changelog.d/README.md", "Fragments.\n");
    write(root, "changelog.d/fix-a.md", "### Fixed\n\n- A leftover.\n");
    git(root, "add", "-A");
    git(root, "commit", "-q", "-m", "chore: leftovers");
    git(root, "tag", "-a", "v1.2.1", "-m", "Release v1.2.1");
    mergePullRequest(root, 12, "fix/ab", "Fix a and b", {
      "changelog.d/fix-[ab].md": "### Fixed\n\n- A and b.\n",
      "changelog.d/café.md": "### Added\n\n- Café.\n",
    });
    // A merge that deletes the leftover and adds the same text under a new name: no rename.
    git(root, "switch", "-q", "-c", "chore/rename");
    git(root, "mv", "changelog.d/fix-a.md", "changelog.d/fix-a-again.md");
    git(root, "commit", "-q", "-m", "Rename");
    git(root, "switch", "-q", "main");
    git(root, "merge", "-q", "--no-ff", "chore/rename", "-m", "Merge pull request #13 from o/chore/rename");

    assert.equal(main([], root), 0);
    assert.equal(printed.length, 2);
    const stamped = readFileSync(path.join(root, "CHANGELOG.md"), "utf8");
    assert.match(stamped, /## \[1\.2\.3\][^]*- A leftover\.[^]*## \[1\.2\.2\][^]*- Café\.[^]*- A and b\./);
    const removed = git(root, "show", "--name-status", "--format=", "HEAD").split("\n").sort();
    assert.deepEqual(removed, [
      "D\tchangelog.d/café.md",
      "D\tchangelog.d/fix-[ab].md",
      "D\tchangelog.d/fix-a-again.md",
      "M\tCHANGELOG.md",
    ]);
    assert.equal(existsSync(path.join(root, "changelog.d/README.md")), true);
  });
});
