import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  computeNextVersion,
  declaredVersion,
  gitTags,
  parseDeclaredVersion,
  readSetting,
  versionFilePath,
} from "./next-version.mjs";

function withRepository(run) {
  const root = mkdtempSync(path.join(tmpdir(), "next-version-"));
  try {
    return run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

function write(root, rel, content) {
  mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  writeFileSync(path.join(root, rel), content);
}

test("increments from the highest tag on the line, comparing builds as numbers", () => {
  assert.equal(computeNextVersion("1.2.0", ["v1.0.0", "v1.2.0", "v1.2.9", "v1.2.10"]), "1.2.11");
});

test("ignores four-part tags and tags that are not releases", () => {
  assert.equal(computeNextVersion("1.2.0", ["v1.2.0", "v1.2.0.29", "v1.2.5-rc1"]), "1.2.1");
});

test("releases a new line at its declared build", () => {
  assert.equal(computeNextVersion("1.3.0", ["v1.2.0", "v1.2.10"]), "1.3.0");
});

test("treats the declared build as a floor", () => {
  assert.equal(computeNextVersion("1.2.5", ["v1.2.0", "v1.2.1"]), "1.2.5");
  assert.equal(computeNextVersion("1.2.0", ["v1.2.0", "v1.2.9"]), "1.2.10");
});

test("is not fooled by another line sharing a numeric prefix", () => {
  assert.equal(computeNextVersion("1.2.0", ["v1.2.0", "v12.0.5", "v1.20.3"]), "1.2.1");
});

test("rejects a version that is not plain major.minor.build", () => {
  for (const version of ["1.2", "1.2.0-rc1", "01.2.0", undefined]) {
    assert.throws(() => computeNextVersion(version, []));
  }
});

test("reads a setting up to the next heading, with either line ending", () => {
  withRepository((root) => {
    write(root, "docs/agents/releases.md", "# Releases\r\n\r\n## Scheme\r\n\r\nnone\r\n\r\n## Next\r\n");
    assert.equal(readSetting(root, "releases", "Scheme"), "none");
    assert.equal(readSetting(root, "releases", "Absent"), null);
    assert.equal(readSetting(root, "missing", "Scheme"), null);
  });
});

test("defaults the version file to package.json", () => {
  withRepository((root) => {
    assert.equal(versionFilePath(root), "package.json");
    write(root, "docs/agents/releases.md", "## Scheme\n\nper-merge\n");
    assert.equal(versionFilePath(root), "package.json");
  });
});

test("takes the version file from the setting, ignoring its prose", () => {
  withRepository((root) => {
    write(root, "docs/agents/releases.md", "## Version file\n\n`web/package.json` (with a lock)\n");
    assert.equal(versionFilePath(root), "web/package.json");
    write(root, "docs/agents/releases.md", "## Version file\n\nVERSION\n");
    assert.equal(versionFilePath(root), "VERSION");
    write(root, "docs/agents/releases.md", "## Version file\n\nnone\n");
    assert.throws(() => versionFilePath(root), /none/);
  });
});

test("reads the declared version from JSON or plain text", () => {
  assert.equal(parseDeclaredVersion("web/package.json", '{"version":"1.2.0"}'), "1.2.0");
  assert.equal(parseDeclaredVersion("VERSION", "1.2.0\r\n"), "1.2.0");
  withRepository((root) => {
    write(root, "docs/agents/releases.md", "## Version file\n\n`VERSION` — the line\n");
    write(root, "VERSION", "3.4.0\n");
    assert.equal(declaredVersion(root), "3.4.0");
  });
});

test("lists the tags of the repository it is given", () => {
  withRepository((root) => {
    const git = (...args) => execFileSync("git", ["-C", root, ...args], { stdio: "ignore" });
    git("init", "-b", "main");
    git(
      "-c",
      "user.name=t",
      "-c",
      "user.email=t@example.com",
      "-c",
      "commit.gpgsign=false",
      "commit",
      "--allow-empty",
      "-m",
      "seed",
    );
    git("tag", "v1.0.0");
    assert.deepEqual(gitTags(root), ["v1.0.0"]);
  });
});
