import assert from "node:assert/strict";
import {
  existsSync,
  lstatSync,
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  symlinkSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  hookTouchesAuthoredTree,
  injectSkillBanner,
  renderCodexAgent,
  renderSharedBlocks,
  splitFrontmatter,
  stripSkillBanner,
  syncMirrors,
  takenBlocks,
  tomlMultiline,
} from "./sync-agents.mjs";

function write(root, rel, content) {
  mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
  writeFileSync(path.join(root, rel), content);
}

function withFixture(fn, { agents = true } = {}) {
  const root = mkdtempSync(path.join(tmpdir(), "sync-agents-"));
  try {
    write(
      root,
      ".agents/skills/demo/SKILL.md",
      [
        "---",
        "name: demo",
        "description: A demo skill.",
        "---",
        "",
        "Body.",
      ].join("\n"),
    );
    write(
      root,
      ".agents/skills/demo/agents/openai.yaml",
      "instructions: do the thing\n",
    );
    if (agents) {
      write(
        root,
        ".claude/agents/reviewer.md",
        [
          "---",
          "name: reviewer",
          'description: "Reviews a diff: finds bugs."',
          "tools: Read, Grep, Glob",
          "model: opus",
          "---",
          "",
          "Review carefully.",
        ].join("\n"),
      );
    }
    fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const read = (root, rel) => readFileSync(path.join(root, rel), "utf8");

test("SKILL.md banner is line 2 and strips back to the source", () => {
  const raw = "---\nname: demo\ndescription: Demo.\n---\n\nBody.";
  const result = injectSkillBanner(".agents/skills/demo/SKILL.md", raw);
  const lines = result.split("\n");
  assert.equal(lines[0], "---");
  assert.match(
    lines[1],
    /^# GENERATED — do not edit\. Source: \.agents\/skills\/demo\/SKILL\.md/,
  );
  assert.equal(lines[2], "name: demo");
  assert.equal(stripSkillBanner(result), raw);
});

test("frontmatter handles quoting, lists, continuations, and comments", () => {
  const { data, body } = splitFrontmatter(
    [
      "---",
      "# a comment",
      "name: 'it''s'",
      'description: "say \\"hi\\""',
      "summary: first",
      "  second",
      "tools:",
      "  - Read",
      "  - Grep",
      "---",
      "Body",
    ].join("\r\n"),
    "x.md",
  );
  assert.equal(data.get("name"), "it's");
  assert.equal(data.get("description"), 'say "hi"');
  assert.equal(data.get("summary"), "first second");
  assert.deepEqual(data.get("tools"), ["Read", "Grep"]);
  assert.equal(body, "Body");
});

test("frontmatter rejects block scalars and unparseable lines", () => {
  assert.throws(
    () => splitFrontmatter("---\ndescription: |\n  x\n---\n", "x.md"),
    /block scalar/,
  );
  assert.throws(
    () => splitFrontmatter("---\n- stray\n---\n", "x.md"),
    /cannot parse/,
  );
  assert.throws(
    () => splitFrontmatter("no frontmatter", "x.md"),
    /expected YAML frontmatter/,
  );
});

test("Codex agent drops Claude-only fields and marks read-only tool lists", () => {
  withFixture((root) => {
    syncMirrors(root);
    const toml = read(root, ".codex/agents/reviewer.toml");
    assert.match(
      toml,
      /^# GENERATED — do not edit\. Source: \.claude\/agents\/reviewer\.md/,
    );
    assert.match(toml, /^description = "Reviews a diff: finds bugs\."$/m);
    assert.match(toml, /^sandbox_mode = "read-only"$/m);
    assert.match(
      toml,
      /^developer_instructions = '''\nReview carefully\.'''\n$/m,
    );
    assert.doesNotMatch(toml, /model|tools/);
  });
});

const md = (tools) => `---\nname: a\ndescription: d\n${tools}---\nBody`;

test("an agent with a writing tool or no tool list gets no sandbox line", () => {
  assert.doesNotMatch(
    renderCodexAgent("a.md", md("tools: Read, Edit\n")),
    /sandbox_mode/,
  );
  assert.doesNotMatch(renderCodexAgent("a.md", md("")), /sandbox_mode/);
  assert.match(
    renderCodexAgent("a.md", md("tools: [Read, Grep]\n")),
    /sandbox_mode/,
  );
});

test("an agent name must match its filename", () => {
  assert.throws(
    () =>
      renderCodexAgent(
        ".claude/agents/a.md",
        "---\nname: b\ndescription: d\n---\nBody",
      ),
    /must match the filename 'a'/,
  );
});

test("a body a literal string cannot hold falls back to an escaped basic string", () => {
  assert.equal(tomlMultiline("plain \\ path"), "'''\nplain \\ path'''");
  assert.equal(
    tomlMultiline("has ''' and \"q\" \\"),
    '"""\nhas \'\'\' and \\"q\\" \\\\"""',
  );
  assert.match(tomlMultiline("ends with '"), /^"""/);
});

test("skills are mirrored whole; non-SKILL.md files are copied byte-for-byte", () => {
  withFixture((root) => {
    const binary = Buffer.from([0x89, 0x50, 0x00, 0x0d, 0x0a, 0xff]);
    write(root, ".agents/skills/demo/icon.png", binary);
    syncMirrors(root);
    assert.match(
      read(root, ".claude/skills/demo/SKILL.md"),
      /^---\n# GENERATED/,
    );
    assert.equal(
      read(root, ".claude/skills/demo/agents/openai.yaml"),
      "instructions: do the thing\n",
    );
    assert.ok(
      readFileSync(path.join(root, ".claude/skills/demo/icon.png")).equals(
        binary,
      ),
    );
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});

test("--check tolerates CRLF in generated text but not other changes", () => {
  withFixture((root) => {
    syncMirrors(root);
    const target = ".claude/skills/demo/SKILL.md";
    write(root, target, read(root, target).replaceAll("\n", "\r\n"));
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
    write(root, target, `${read(root, target)}edited`);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, [target]);
  });
});

test("--check reports missing and orphaned files without writing; sync prunes them", () => {
  withFixture((root) => {
    syncMirrors(root);
    rmSync(path.join(root, ".agents/skills/demo"), { recursive: true });
    rmSync(path.join(root, ".codex/agents/reviewer.toml"));

    const { stale } = syncMirrors(root, { check: true });
    assert.ok(stale.includes(".codex/agents/reviewer.toml (missing)"));
    assert.ok(stale.includes(".claude/skills/demo/SKILL.md (orphaned)"));
    assert.equal(
      existsSync(path.join(root, ".codex/agents/reviewer.toml")),
      false,
    );

    syncMirrors(root);
    assert.equal(existsSync(path.join(root, ".claude/skills/demo")), false);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});

test("a stray symlink in a generated tree is reported, then removed without following it", () => {
  withFixture((root) => {
    mkdirSync(path.join(root, ".claude/skills"), { recursive: true });
    const stray = path.join(root, ".claude/skills/stray");
    symlinkSync(path.join(root, ".agents/skills/demo"), stray, "junction");

    assert.ok(
      syncMirrors(root, { check: true }).stale.includes(
        ".claude/skills/stray (orphaned)",
      ),
    );
    syncMirrors(root);
    assert.throws(() => lstatSync(stray));
    assert.ok(existsSync(path.join(root, ".agents/skills/demo/SKILL.md")));
  });
});

test("one sync replaces a skill directory linked to its authored source", () => {
  withFixture((root) => {
    // `npx skills update` re-installs without --copy, linking .claude/skills/<name> to the source.
    mkdirSync(path.join(root, ".claude/skills"), { recursive: true });
    const linked = path.join(root, ".claude/skills/demo");
    symlinkSync(path.join(root, ".agents/skills/demo"), linked, "junction");

    syncMirrors(root);
    assert.ok(lstatSync(linked).isDirectory());
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
    assert.equal(
      read(root, ".agents/skills/demo/SKILL.md"),
      "---\nname: demo\ndescription: A demo skill.\n---\n\nBody.",
    );
  });
});

test("a symlink in an authored tree is an error", () => {
  withFixture((root) => {
    const target = mkdtempSync(path.join(tmpdir(), "sync-agents-link-"));
    try {
      symlinkSync(target, path.join(root, ".agents/skills/linked"), "junction");
      assert.throws(() => syncMirrors(root), /symlinks are not allowed/);
    } finally {
      rmSync(target, { recursive: true, force: true });
    }
  });
});

test("the skills direction runs when there are no agents", () => {
  withFixture(
    (root) => {
      syncMirrors(root);
      assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
    },
    { agents: false },
  );
});

const payload = (file) => ({ tool_input: { file_path: file } });

test("the hook fires only for edits under an authored tree", () => {
  const root = path.resolve("/repo");
  assert.ok(
    hookTouchesAuthoredTree(
      payload(path.join(root, ".agents/skills/x/SKILL.md")),
      root,
    ),
  );
  assert.ok(hookTouchesAuthoredTree(payload(".claude/agents/a.md"), root));
  assert.equal(
    hookTouchesAuthoredTree(
      payload(path.join(root, ".claude/skills/x/SKILL.md")),
      root,
    ),
    false,
  );
  assert.equal(
    hookTouchesAuthoredTree(payload(path.join(root, "src/app.ts")), root),
    false,
  );
  assert.equal(hookTouchesAuthoredTree(null, root), false);
});

const blockFile = ".agents/skills/ship/settings/agents-md.md";
const blockSource = [
  "# Topic",
  "",
  "<!-- shared:flow -->",
  "The shared flow costs $1 and `$&` stays literal.",
  "<!-- /shared:flow -->",
  "",
  "<!-- shared:skills -->",
  "The shared skills.",
  "<!-- /shared:skills -->",
  "",
].join("\n");
const agentsWithMarkers = [
  "# Rules",
  "",
  "<!-- shared:flow -->",
  "stale",
  "<!-- /shared:flow -->",
  "",
  "Our own rule.",
  "",
  "<!-- shared:skills -->",
  "<!-- /shared:skills -->",
  "",
].join("\n");

test("the shared blocks are written between their markers, and nothing else changes", () => {
  withFixture((root) => {
    write(root, blockFile, blockSource);
    write(root, "AGENTS.md", agentsWithMarkers);

    assert.ok(syncMirrors(root, { check: true }).stale.includes("AGENTS.md"));
    syncMirrors(root);
    const written = read(root, "AGENTS.md");
    assert.ok(written.includes("costs $1 and `$&` stays literal.\n<!-- /shared:flow -->"));
    assert.ok(written.includes("The shared skills.\n<!-- /shared:skills -->"));
    assert.ok(written.includes("\nOur own rule.\n"));
    assert.ok(!written.includes("stale"));
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);

    write(root, "AGENTS.md", written.replace("The shared skills.", "Edited here."));
    assert.ok(syncMirrors(root, { check: true }).stale.includes("AGENTS.md"));
  });
});

test("a repository with no block file keeps its AGENTS.md untouched", () => {
  withFixture((root) => {
    write(root, "AGENTS.md", "# Rules\n");
    assert.deepEqual(syncMirrors(root, { check: true }).stale.filter((f) => f === "AGENTS.md"), []);
    syncMirrors(root);
    assert.equal(read(root, "AGENTS.md"), "# Rules\n");
  });
});

test("a missing or repeated block is an error naming the markers", () => {
  const source = [{ name: "flow", body: "Text." }];
  assert.throws(() => renderSharedBlocks("# Rules\n", source, ["flow"]), /<!-- shared:flow -->/);
  const twice = "<!-- shared:flow -->\na\n<!-- /shared:flow -->\n\n<!-- shared:flow -->\nb\n<!-- /shared:flow -->\n";
  assert.throws(() => renderSharedBlocks(twice, source, ["flow"]), /found 2/);
});

test("markers of a block the repository does not take are an error", () => {
  const source = [{ name: "flow", body: "Text." }];
  const raw = "<!-- shared:other -->\na\n<!-- /shared:other -->\n";
  assert.throws(() => renderSharedBlocks(raw, source, []), /block 'other'/);
});

test("the setting takes every block by default, none, or the names it lists", () => {
  const all = ["flow", "skills"];
  assert.deepEqual(takenBlocks("# Workspace\n", all), all);
  assert.deepEqual(takenBlocks("## AGENTS.md blocks\n\nnone\n", all), []);
  assert.deepEqual(takenBlocks("## AGENTS.md blocks\r\n\r\n`skills`\r\n\r\n## Next\r\n\r\n`flow`\r\n", all), ["skills"]);
  assert.throws(() => takenBlocks("## AGENTS.md blocks\n\n`nope`\n", all), /must be 'none'/);
});

test("the setting's none leaves AGENTS.md alone, whatever the block file holds", () => {
  withFixture((root) => {
    write(root, blockFile, blockSource);
    write(root, "docs/agents/workspace.md", "## AGENTS.md blocks\n\nnone\n");
    write(root, "AGENTS.md", "# Rules\n");
    syncMirrors(root);
    assert.equal(read(root, "AGENTS.md"), "# Rules\n");
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});
