import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  addedVersions,
  checkCut,
  checkPerMerge,
  checkStampOnTag,
  documentedVersions,
  ignorePatterns,
  isFragmentPath,
  main,
  parseArgs,
  parseFragment,
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

test("a fragment holds ### categories and entries, never a version heading", () => {
  const ok = parseFragment("### Added\r\n\r\n- A thing.\r\n  More.\r\n\r\n### Fixed\r\n- A fix.\r\n");
  assert.deepEqual(ok.errors, []);
  assert.deepEqual([...ok.categories], [["Added", ["- A thing.", "  More."]], ["Fixed", ["- A fix."]]]);
  assert.match(parseFragment("## [1.2.3] - 2026-11-18\n### Added\n- A.\n").errors[0], /line 1/);
  assert.match(parseFragment("- A.\n").errors[0], /before the first ### heading/);
  assert.match(parseFragment("### Added\n\n").errors[0], /### Added has no entries/);
  assert.match(parseFragment("").errors[0], /no entries/);
});

test("a fragment path is a direct child of changelog.d, and never its README", () => {
  assert.equal(isFragmentPath("changelog.d/feat-12-x.md"), true);
  assert.equal(isFragmentPath("changelog.d/README.md"), false);
  assert.equal(isFragmentPath("changelog.d/old/x.md"), false);
  assert.equal(isFragmentPath("docs/changelog.d/x.md"), false);
  assert.equal(isFragmentPath("changelogXd/x.md"), false);
});

test("a fragment may start with a byte-order mark", () => {
  assert.deepEqual(parseFragment("\uFEFF### Added\n- A.\n").errors, []);
});

test("stamp-on-tag lets a branch empty Unreleased, and rejects edits to another merge's fragment", () => {
  const filled = changelog.replace("No unreleased changes.", "- An old entry.");
  const base = { baseChangelog: filled, changedFiles: [], fragments: [], sources: [] };
  assert.deepEqual(checkStampOnTag({ ...base, changelog }), []);
  assert.match(
    checkStampOnTag({ ...base, changelog: filled, pending: ["changelog.d/feat-x.md"] })[0],
    /^changelog\.d\/feat-x\.md: this branch edits or deletes a fragment another merge added/,
  );
});

test("stamp-on-tag rejects a branch's version sections and Unreleased edits, not other prose", () => {
  const base = { baseChangelog: changelog, changedFiles: [], fragments: [], sources: [] };
  assert.deepEqual(checkStampOnTag({ ...base, changelog: changelog.replace("# Changelog", "# Changes") }), []);
  assert.match(
    checkStampOnTag({ ...base, changelog: changelog.replace("## [Unreleased]\n", "## [Unreleased]\n\n## [1.2.13] - 2026-11-18\n") })[0],
    /renumbers a version section/,
  );
  assert.match(
    checkStampOnTag({ ...base, changelog: changelog.replace("No unreleased changes.", "- An entry.") })[0],
    /edits \[Unreleased\]/,
  );
});

test("addedVersions lists insertions, and refuses removals, reorders and repeats", () => {
  assert.deepEqual(addedVersions(["1.2.12", "1.2.11"], ["1.2.12", "1.2.11"]), []);
  assert.deepEqual(addedVersions(["1.2.13", "1.2.12", "1.2.11"], ["1.2.12", "1.2.11"]), ["1.2.13"]);
  assert.deepEqual(addedVersions(["1.2.12", "1.2.5", "1.2.11"], ["1.2.12", "1.2.11"]), ["1.2.5"]);
  assert.equal(addedVersions(["1.2.12"], ["1.2.12", "1.2.11"]), null);
  assert.equal(addedVersions(["1.2.11", "1.2.12"], ["1.2.12", "1.2.11"]), null);
  assert.equal(addedVersions(["1.2.12", "1.2.12", "1.2.11"], ["1.2.12", "1.2.11"]), null);
});

test("stamp-on-tag lets a branch backfill a tagged version with no section, and nothing else", () => {
  const base = { baseChangelog: changelog, changedFiles: [], fragments: [], sources: [] };
  const backfilled = changelog.replace(
    "## [1.2.12]",
    "## [1.2.13] - 2026-07-17\n\n### Changed\n\n- Bump x (#5)\n\n## [1.2.12]",
  );
  assert.deepEqual(checkStampOnTag({ ...base, changelog: backfilled, tagged: ["1.2.13"] }), []);
  assert.match(checkStampOnTag({ ...base, changelog: backfilled, tagged: [] })[0], /as a backfill/);
  assert.match(
    checkStampOnTag({ ...base, changelog: backfilled.replace("## [1.2.11]", "## [1.2.10]"), tagged: ["1.2.10", "1.2.13"] })[0],
    /renumbers a version section/,
  );
});

test("stamp-on-tag needs a fragment for a source change only when sources are named", () => {
  const base = { changelog, baseChangelog: changelog, changedFiles: ["src/a.txt"], fragments: [] };
  assert.deepEqual(checkStampOnTag({ ...base, sources: [] }), []);
  assert.match(checkStampOnTag({ ...base, sources: ["src"] })[0], /no fragment in changelog\.d/);
  const fragment = { file: "changelog.d/x.md", text: "### Added\n- A.\n" };
  assert.deepEqual(checkStampOnTag({ ...base, sources: ["src"], fragments: [fragment] }), []);
  assert.match(
    checkStampOnTag({ ...base, sources: [], fragments: [{ file: "changelog.d/x.md", text: "- A.\n" }] })[0],
    /^changelog\.d\/x\.md: line 1/,
  );
});

test("stamp-on-tag reads the branch's own fragments, tracked or new", () => {
  withRepository((root) => {
    git(root, "init", "-b", "main");
    write(root, "docs/agents/releases.md", "## Scheme\n\nstamp-on-tag\n");
    write(root, "package.json", '{"version":"1.2.0"}\n');
    write(root, "CHANGELOG.md", changelog);
    write(root, "changelog.d/README.md", "Fragments.\n");
    write(root, "src/a.txt", "one\n");
    commitAll(root);
    git(root, "switch", "-c", "feature");
    write(root, "src/a.txt", "two\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
    write(root, "changelog.d/feature.md", "### Changed\n\n- A is two.\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
    commitAll(root);
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
    write(root, "changelog.d/feature.md", "- A is two.\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
  });
});

test("stamp-on-tag accepts a backfill only for a version the repository has tagged", () => {
  withRepository((root) => {
    git(root, "init", "-b", "main");
    write(root, "docs/agents/releases.md", "## Scheme\n\nstamp-on-tag\n");
    write(root, "package.json", '{"version":"1.2.0"}\n');
    write(root, "CHANGELOG.md", changelog);
    commitAll(root);
    git(root, "tag", "v1.2.13");
    git(root, "switch", "-c", "feature");
    const section = (version) => `## [${version}] - 2026-07-17\n\n### Changed\n\n- Bump x.\n\n## [1.2.12]`;
    write(root, "CHANGELOG.md", changelog.replace("## [1.2.12]", section("1.2.13")));
    assert.equal(main(["--base", "main"], root), 0);
    write(root, "CHANGELOG.md", changelog.replace("## [1.2.12]", section("1.2.14")));
    assert.equal(main(["--base", "main"], root), 1);
  });
});

test("stamp-on-tag fails a branch that edits a pending fragment, and sees non-ASCII names", () => {
  withRepository((root) => {
    git(root, "init", "-b", "main");
    write(root, "docs/agents/releases.md", "## Scheme\n\nstamp-on-tag\n");
    write(root, "package.json", '{"version":"1.2.0"}\n');
    write(root, "CHANGELOG.md", changelog);
    write(root, "changelog.d/feat-x.md", "### Added\n\n- X.\n");
    commitAll(root);
    git(root, "switch", "-c", "feature");
    write(root, "src/café.txt", "one\n");
    write(root, "changelog.d/café.md", "### Added\n\n- Café.\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 0);
    write(root, "changelog.d/feat-x.md", "### Added\n\n- X.\n\n### Fixed\n\n- X no longer crashes.\n");
    assert.equal(main(["--source", "src", "--base", "main"], root), 1);
  });
});
