import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  checkCut,
  checkPerMerge,
  documentedVersions,
  ignorePatterns,
  main,
  parseArgs,
  releasedVersions,
  touchesSource,
  unreleasedSection,
} from "./check-changelog.mjs";

const changelog = `# Changelog

## [Unreleased]

No unreleased changes.

## [1.2.12] - 2026-07-16

- A thing.

## [1.2.11] - 2026-07-15

- Another thing.
`;

function withRepository(run) {
  const root = mkdtempSync(path.join(tmpdir(), "check-changelog-"));
  const originalLog = console.log;
  console.log = () => {};
  try {
    return run(root);
  } finally {
    console.log = originalLog;
    rmSync(root, { recursive: true, force: true });
  }
}

function write(root, rel, content) {
  mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  writeFileSync(path.join(root, rel), content);
}

function git(root, ...args) {
  execFileSync(
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
    { stdio: "ignore" },
  );
}

function commitAll(root) {
  git(root, "add", "-A");
  git(root, "commit", "-m", "change");
}

test("parses repeated sources and a base, and rejects anything else", () => {
  assert.deepEqual(parseArgs(["--source", "src/", "--source", "web/src", "--base", "abc"]), {
    sources: ["src", "web/src"],
    base: "abc",
  });
  assert.throws(() => parseArgs(["--nope"]), /Unknown argument/);
  assert.throws(() => parseArgs(["--source"]), /requires a value/);
});

test("released versions are three-part release tags", () => {
  assert.deepEqual(releasedVersions(["v1.2.0", "v1.1.1.29", "nightly", "v1.2.0-rc1"]), ["1.2.0"]);
});

test("documented versions are headings, newest first, and never Unreleased", () => {
  assert.deepEqual(documentedVersions(changelog), ["1.2.12", "1.2.11"]);
  assert.deepEqual(documentedVersions("see the `## [9.9.9]` section\n"), []);
});

test("reads backticked Ignore patterns, and nothing else in the setting", () => {
  const value =
    "backfill — a `v9.9.9` mention before the list.\n\n" +
    "- **Ignore:** `v0.*`, `v1.0.*` and `v2.1.3` — archived in `docs/changelog/` (`0.x.md`).";
  const patterns = ignorePatterns(value);
  assert.equal(patterns.length, 3);
  assert.ok(patterns[0].test("v0.3.1"));
  assert.ok(patterns[1].test("v1.0.4"));
  assert.ok(!patterns[1].test("v1.01.4"));
  assert.ok(patterns[2].test("v2.1.3"));
  assert.deepEqual(ignorePatterns("backfill"), []);
  assert.deepEqual(ignorePatterns(null), []);
});

test("per-merge passes when the newest section names the next version", () => {
  assert.deepEqual(
    checkPerMerge({ changelog, tags: ["v1.2.11"], next: "1.2.12", skippedTags: null }),
    [],
  );
});

test("per-merge fails a branch that adds no section, or an overtaken one", () => {
  const [error] = checkPerMerge({ changelog, tags: ["v1.2.11"], next: "1.2.13", skippedTags: null });
  assert.match(error, /will mint v1\.2\.13.*newest section is \[1\.2\.12\]/);
});

test("per-merge fails when only an older section names the next version", () => {
  const errors = checkPerMerge({ changelog, tags: [], next: "1.2.11", skippedTags: "backfill" });
  assert.equal(errors.length, 1);
});

test("backfill reports released tags with no section, oldest first by number", () => {
  const errors = checkPerMerge({
    changelog,
    tags: ["v1.2.11", "v1.2.13", "v1.2.9"],
    next: "1.2.12",
    skippedTags: null,
  });
  assert.equal(errors.length, 1);
  assert.match(errors[0], /Released with no section: 1\.2\.9, 1\.2\.13\./);
});

test("backfill honors Ignore patterns", () => {
  assert.deepEqual(
    checkPerMerge({
      changelog,
      tags: ["v0.1.1", "v1.2.11"],
      next: "1.2.12",
      skippedTags: "backfill\n\n- **Ignore:** `v0.*`",
    }),
    [],
  );
});

test("roll up and none never report a skipped tag", () => {
  for (const skippedTags of ["roll up — dependency bumps", "none"]) {
    assert.deepEqual(
      checkPerMerge({ changelog, tags: ["v1.2.9"], next: "1.2.12", skippedTags }),
      [],
    );
  }
});

test("a cut needs a dated section", () => {
  assert.deepEqual(checkCut({ changelog: "## [1.3.0] - 2026-10-31\n", version: "1.3.0" }), []);
  assert.match(checkCut({ changelog: "## [1.3.0]\n", version: "1.3.0" })[0], /no '## \[1\.3\.0\]/);
  assert.equal(checkCut({ changelog: "## [1.3.01] - 2026-10-31\n", version: "1.3.0" }).length, 1);
});

test("a cut in a file with a link footer needs both links", () => {
  const body = "## [1.3.0] - 2026-10-31\n\n";
  const compare = "[Unreleased]: https://github.com/o/r/compare/v1.3.0...HEAD\n";
  const release = "[1.3.0]: https://github.com/o/r/releases/tag/v1.3.0\n";
  assert.deepEqual(checkCut({ changelog: body + compare + release, version: "1.3.0" }), []);
  assert.equal(checkCut({ changelog: body + compare, version: "1.3.0" }).length, 1);
  const stale = "[Unreleased]: https://github.com/o/r/compare/v1.2.0...HEAD\n";
  assert.equal(checkCut({ changelog: body + stale + release, version: "1.3.0" }).length, 1);
});

test("Unreleased is compared without regard to line endings, up to the next section", () => {
  assert.equal(unreleasedSection(changelog), "No unreleased changes.");
  assert.equal(
    unreleasedSection(changelog.replaceAll("\n", "\r\n")),
    "No unreleased changes.",
  );
  assert.equal(unreleasedSection("# Changelog\n"), null);
});

test("a source path matches itself and what is under it, not a shared prefix", () => {
  assert.ok(touchesSource(["src/a.cs"], ["src"]));
  assert.ok(touchesSource(["web/src/a.ts"], ["src", "web/src"]));
  assert.ok(!touchesSource(["srcfoo/a.cs", "docs/src/a.md"], ["src"]));
});

test("main fails closed without a Scheme, and on an unknown one", () => {
  withRepository((root) => {
    assert.throws(() => main([], root), /no `Scheme`/);
    write(root, "docs/agents/releases.md", "## Scheme\n\nweekly\n");
    write(root, "CHANGELOG.md", changelog);
    assert.throws(() => main([], root), /Unknown Scheme/);
  });
});

test("main passes a Scheme of none without a changelog", () => {
  withRepository((root) => {
    write(root, "docs/agents/releases.md", "## Scheme\n\nnone — the PR body is the record\n");
    assert.equal(main([], root), 0);
  });
});

test("main applies the per-merge rules to the repository's own tags and version file", () => {
  withRepository((root) => {
    git(root, "init", "-b", "main");
    write(root, "docs/agents/releases.md", "## Scheme\n\nper-merge\n\n## Version file\n\n`VERSION`\n");
    write(root, "VERSION", "1.2.0\n");
    write(root, "CHANGELOG.md", changelog);
    commitAll(root);
    git(root, "tag", "v1.2.11");
    assert.equal(main([], root), 0);
    git(root, "tag", "v1.2.12");
    assert.equal(main([], root), 1);
  });
});

function cutOnLineRepository(root) {
  git(root, "init", "-b", "main");
  write(root, "docs/agents/releases.md", "## Scheme\n\ncut-on-line\n\n## Version file\n\n`VERSION`\n");
  write(root, "VERSION", "1.2.0\n");
  write(root, "CHANGELOG.md", changelog);
  write(root, "src/a.txt", "one\n");
  commitAll(root);
  git(root, "switch", "-c", "feature");
}

test("cut-on-line requires a source path to compare", () => {
  withRepository((root) => {
    cutOnLineRepository(root);
    assert.throws(() => main([], root), /--source/);
  });
});

test("cut-on-line passes a change outside the source paths", () => {
  withRepository((root) => {
    cutOnLineRepository(root);
    write(root, "docs/a.md", "docs\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
  });
});

test("cut-on-line needs Unreleased to change with a source change, tracked or new", () => {
  withRepository((root) => {
    cutOnLineRepository(root);
    write(root, "src/new.txt", "new\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
    commitAll(root);
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
    write(root, "CHANGELOG.md", changelog.replace("No unreleased changes.", "- An entry."));
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
  });
});

test("cut-on-line reads a version change by value, so another package.json edit is no cut", () => {
  withRepository((root) => {
    git(root, "init", "-b", "main");
    write(root, "docs/agents/releases.md", "## Scheme\n\ncut-on-line\n");
    write(root, "package.json", '{"version":"1.2.0"}\n');
    write(root, "CHANGELOG.md", changelog);
    commitAll(root);
    git(root, "switch", "-c", "feature");
    write(root, "package.json", '{"version":"1.2.0","private":true}\n');
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
    write(root, "package.json", '{"version":"1.3.0"}\n');
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
    write(root, "CHANGELOG.md", `${changelog}\n## [1.3.0] - 2026-10-31\n`);
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
  });
});
