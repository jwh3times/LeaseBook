import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  rmSync,
  writeFileSync,
  readFileSync,
  symlinkSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import { injectSkillBanner, syncMirrors } from "./sync-agent-mirrors.mjs";

function makeFixtureRoot() {
  const root = mkdtempSync(path.join(tmpdir(), "leasebook-sync-"));
  mkdirSync(path.join(root, ".claude", "agents"), { recursive: true });
  mkdirSync(path.join(root, ".agents", "skills", "demo", "agents"), {
    recursive: true,
  });
  writeFileSync(
    path.join(root, ".agents", "skills", "demo", "SKILL.md"),
    [
      "---",
      "name: demo",
      "description: A demo skill.",
      "---",
      "",
      "Body.",
    ].join("\n"),
  );
  writeFileSync(
    path.join(root, ".agents", "skills", "demo", "agents", "openai.yaml"),
    "instructions: do the thing\n",
  );
  return root;
}

test("injectSkillBanner inserts a YAML-comment banner as line 2, keeping frontmatter valid", () => {
  const raw = [
    "---",
    "name: demo",
    "description: Demo.",
    "---",
    "",
    "Body.",
  ].join("\n");
  const result = injectSkillBanner(".agents/skills/demo/SKILL.md", raw);
  const lines = result.split("\n");
  assert.equal(lines[0], "---");
  assert.match(lines[1], /^# GENERATED — do not edit/);
  assert.equal(lines[2], "name: demo");
  assert.match(result, /---\n[\s\S]*name: demo/);
});

test("syncMirrors generates .claude/skills from .agents/skills, copying non-SKILL.md files byte-for-byte", () => {
  const root = makeFixtureRoot();
  try {
    syncMirrors(root, { check: false });
    const skillMd = readFileSync(
      path.join(root, ".claude", "skills", "demo", "SKILL.md"),
      "utf8",
    );
    assert.match(skillMd, /^---\n# GENERATED/);
    assert.match(skillMd, /name: demo/);

    const yaml = readFileSync(
      path.join(root, ".claude", "skills", "demo", "agents", "openai.yaml"),
      "utf8",
    );
    assert.equal(yaml, "instructions: do the thing\n");
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("syncMirrors --check reports drift without writing, and non-check mode prunes orphans", () => {
  const root = makeFixtureRoot();
  try {
    syncMirrors(root, { check: false });

    // Remove the source file; the generated copy is now orphaned.
    rmSync(
      path.join(root, ".agents", "skills", "demo", "agents", "openai.yaml"),
    );

    const checked = syncMirrors(root, { check: true });
    assert.ok(
      checked.stale.some((f) => f.includes("openai.yaml")),
      "expected orphaned generated file to be reported as stale",
    );

    syncMirrors(root, { check: false });
    assert.throws(() =>
      readFileSync(
        path.join(root, ".claude", "skills", "demo", "agents", "openai.yaml"),
      ),
    );
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("syncMirrors never treats a symlinked skill entry as a directory (regression guard)", () => {
  const root = makeFixtureRoot();
  mkdirSync(path.join(root, ".claude", "skills"), { recursive: true });
  symlinkSync(
    path.join(root, ".agents", "skills", "demo"),
    path.join(root, ".claude", "skills", "stray-symlink"),
    "junction",
  );
  try {
    // Regenerating must not throw on a stray symlink sitting in the generated tree,
    // and must not treat it as a valid generated entry to leave alone.
    assert.doesNotThrow(() => syncMirrors(root, { check: false }));
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});
