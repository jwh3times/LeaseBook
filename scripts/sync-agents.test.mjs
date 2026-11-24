import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  copyFileSync,
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
  checkGuardSettings,
  hookTouchesAuthoredTree,
  injectSkillBanner,
  managedHooks,
  mergeClaudeSettings,
  renderCodexAgent,
  renderSharedBlocks,
  splitFrontmatter,
  stripSkillBanner,
  syncMirrors,
  takenBlocks,
  tomlMultiline,
} from "./sync-agents.mjs";
import * as syncAgents from "./sync-agents.mjs";

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

const blockFile = ".agents/skills/open-pr/settings/agents-md.md";
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

// --- .claude/settings.json --------------------------------------------------------------------

// Node's own `import.meta.dirname`; a runner that leaves it unset (Vitest gives `import.meta.url` a
// non-file scheme) runs from the repository root, where the scripts are in `scripts/`.
const scriptsDir = import.meta.dirname ?? path.resolve("scripts");
const settingsFile = ".claude/settings.json";

function withGuard(fn) {
  withFixture((root) => {
    mkdirSync(path.join(root, "scripts"), { recursive: true });
    copyFileSync(path.join(scriptsDir, "guard.mjs"), path.join(root, "scripts", "guard.mjs"));
    fn(root);
  });
}

const settingsOf = (root) => JSON.parse(read(root, settingsFile));
const guardEntries = (hooks) =>
  hooks.filter((entry) =>
    entry.hooks.some((hook) => JSON.stringify(hook).includes("scripts/guard.mjs")),
  );

test("without scripts/guard.mjs no settings file is written", () => {
  withFixture((root) => {
    syncMirrors(root);
    assert.equal(existsSync(path.join(root, settingsFile)), false);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});

test("a missing settings file is reported, then created with both entries in exec form", () => {
  withGuard((root) => {
    assert.ok(syncMirrors(root, { check: true }).stale.includes(`${settingsFile} (missing)`));
    syncMirrors(root);
    const raw = read(root, settingsFile);
    assert.ok(raw.endsWith("}\n") && !raw.includes("\r"));
    assert.deepEqual(settingsOf(root), { hooks: { PreToolUse: [managedHooks.PreToolUse], PostToolUse: [managedHooks.PostToolUse] } });
    assert.deepEqual(managedHooks.PreToolUse.hooks[0], {
      type: "command",
      command: "node",
      args: ["${CLAUDE_PROJECT_DIR}/scripts/guard.mjs"],
      timeout: 10,
    });
    assert.deepEqual(managedHooks.PostToolUse.hooks[0].args.at(-1), "--post");
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});

test("an empty object or a permissions-only file keeps its keys and gains the hooks", () => {
  assert.deepEqual(JSON.parse(mergeClaudeSettings("{}")), { hooks: { PreToolUse: [managedHooks.PreToolUse], PostToolUse: [managedHooks.PostToolUse] } });
  const merged = JSON.parse(mergeClaudeSettings('{"permissions":{"allow":["Bash(ls:*)"]}}'));
  assert.deepEqual(Object.keys(merged), ["permissions", "hooks"]);
  assert.deepEqual(merged.permissions, { allow: ["Bash(ls:*)"] });
  assert.throws(
    () => mergeClaudeSettings("{ nope"),
    (error) => /settings\.json: invalid JSON/.test(error.message) && error.cause instanceof SyntaxError,
  );
  assert.throws(() => mergeClaudeSettings('{"hooks":{"PreToolUse":{}}}'), /must be an array/);
});

test("valleys' shape: its own hooks stay first, in order, and the guard joins after them", () => {
  const valleys = {
    permissions: { allow: ["Bash(npm test:*)"] },
    hooks: {
      PreToolUse: [
        {
          matcher: "*",
          hooks: [{ type: "command", command: 'node "$CLAUDE_PROJECT_DIR/scripts/require-production-confirmation.ts" || exit 2', timeout: 5 }],
        },
      ],
      PostToolUse: [
        { matcher: "Write|Edit", hooks: [{ type: "command", command: "node scripts/sync-agents.mjs --hook", timeout: 20 }] },
      ],
    },
  };
  const merged = JSON.parse(mergeClaudeSettings(JSON.stringify(valleys, null, 2)));
  assert.deepEqual(merged.hooks.PreToolUse, [valleys.hooks.PreToolUse[0], managedHooks.PreToolUse]);
  assert.deepEqual(merged.hooks.PostToolUse, [valleys.hooks.PostToolUse[0], managedHooks.PostToolUse]);
  assert.deepEqual(merged.permissions, valleys.permissions);
});

test("a current entry with its own formatting is left byte for byte, CRLF included", () => {
  withGuard((root) => {
    const odd = `{ "hooks": { "PreToolUse": [ ${JSON.stringify(managedHooks.PreToolUse)} ],\n"PostToolUse": [${JSON.stringify(managedHooks.PostToolUse)}] } }`;
    write(root, settingsFile, odd);
    syncMirrors(root);
    assert.equal(read(root, settingsFile), odd);
    write(root, settingsFile, odd.replaceAll("\n", "\r\n"));
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
    syncMirrors(root);
    assert.equal(read(root, settingsFile), odd.replaceAll("\n", "\r\n"));
  });
});

test("a stale entry is replaced where it stands, and duplicates collapse into it", () => {
  withGuard((root) => {
    const shellForm = { type: "command", command: 'node "$CLAUDE_PROJECT_DIR/scripts/guard.mjs"', timeout: 5 };
    const other = { matcher: "Bash", hooks: [{ type: "command", command: "echo other" }] };
    const staleSettings = JSON.stringify({
      hooks: {
        PreToolUse: [
          { matcher: "Bash", hooks: [shellForm] },
          other,
          { matcher: "Edit", hooks: [shellForm, { type: "command", command: "echo kept" }] },
        ],
        PostToolUse: [],
      },
    });
    syncMirrors(root);
    write(root, settingsFile, staleSettings);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, [settingsFile]);
    syncMirrors(root);
    const { PreToolUse, PostToolUse } = settingsOf(root).hooks;
    assert.deepEqual(PreToolUse, [
      managedHooks.PreToolUse,
      other,
      { matcher: "Edit", hooks: [{ type: "command", command: "echo kept" }] },
    ]);
    assert.equal(guardEntries(PreToolUse).length, 1);
    assert.deepEqual(PostToolUse, [managedHooks.PostToolUse]);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
    // The rewrite is in the shape Prettier and Biome print, so their checks pass on it.
    const raw = read(root, settingsFile);
    assert.ok(raw.includes('\n            "args": ["${CLAUDE_PROJECT_DIR}/scripts/guard.mjs", "--post"],\n'), raw);
    // An array holding objects stays expanded, one key per line, however short.
    assert.ok(raw.includes('\n            "command": "echo other"\n'), raw);
  });
});

// --- The formatter-compatible JSON writer ---------------------------------------------------------

const FRESH_AT_100 = `{
  "permissions": {
    "allow": ["Bash(ls:*)"]
  },
  "hooks": {
    "PreToolUse": [
      {
        "matcher": "Bash|PowerShell|Edit|Write|MultiEdit|NotebookEdit",
        "hooks": [
          {
            "type": "command",
            "command": "node",
            "args": ["\${CLAUDE_PROJECT_DIR}/scripts/guard.mjs"],
            "timeout": 10
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "Write|Edit|MultiEdit",
        "hooks": [
          {
            "type": "command",
            "command": "node",
            "args": ["\${CLAUDE_PROJECT_DIR}/scripts/guard.mjs", "--post"],
            "timeout": 10
          }
        ]
      }
    ]
  }
}
`;

test("the writer prints short primitive arrays inline and expands objects, empties stay inline", () => {
  assert.equal(typeof syncAgents.formatJson, "function", "formatJson is exported");
  assert.equal(
    syncAgents.formatJson({ a: ["x", 1, true, null], b: {}, c: [], d: [{ e: 1 }], f: [[1]] }, 80),
    '{\n  "a": ["x", 1, true, null],\n  "b": {},\n  "c": [],\n  "d": [\n    {\n      "e": 1\n    }\n  ],\n  "f": [\n    [1]\n  ]\n}\n',
  );
  assert.equal(syncAgents.formatJson([], 80), "[]\n");
});

test("a primitive array goes one element per line once its whole line, comma included, passes the width", () => {
  const { formatJson } = syncAgents;
  assert.equal(typeof formatJson, "function", "formatJson is exported");
  // `  "k": ["aaaa"],` is 16 columns; without the comma (the last key) it is 15.
  assert.equal(formatJson({ k: ["aaaa"], z: 1 }, 16), '{\n  "k": ["aaaa"],\n  "z": 1\n}\n');
  assert.equal(formatJson({ k: ["aaaa"], z: 1 }, 15), '{\n  "k": [\n    "aaaa"\n  ],\n  "z": 1\n}\n');
  assert.equal(formatJson({ z: 1, k: ["aaaa"] }, 15), '{\n  "z": 1,\n  "k": ["aaaa"]\n}\n');
  const allow = ["Bash(npm run build:*)", "Bash(npm run check:*)", "Bash(npm run test:*)", "Bash(git status:*)"];
  assert.match(formatJson({ permissions: { allow } }, 80), /\n {4}"allow": \[\n {6}"Bash\(npm run build:\*\)",\n/);
  assert.match(formatJson({ permissions: { allow } }, 200), /\n {4}"allow": \["Bash\(npm run build:\*\)", /);
});

test("the width comes from the repository's Prettier or Biome config, else 80", () => {
  const { formatWidth } = syncAgents;
  assert.equal(typeof formatWidth, "function", "formatWidth is exported");
  const cases = [
    [{}, 80],
    [{ ".prettierrc": '{ "printWidth": 100, "tabWidth": 2 }' }, 100],
    [{ ".prettierrc": "semi: true\nprintWidth: 120\n" }, 120],
    [{ ".prettierrc.json": '{ "singleQuote": true, "printWidth": 90 }' }, 90],
    [{ "package.json": '{ "name": "x", "prettier": { "printWidth": 110 } }' }, 110],
    [{ "biome.json": '{ "formatter": { "indentStyle": "space", "lineWidth": 100 } }' }, 100],
    [{ "biome.json": '{ "formatter": { "lineWidth": 100 }, "json": { "formatter": { "lineWidth": 120 } } }' }, 120],
    [{ "biome.jsonc": '{\n  // comment\n  "formatter": { "lineWidth": 96 }\n}' }, 96],
    [{ "package.json": '{ "name": "x" }' }, 80],
  ];
  for (const [files, width] of cases) {
    const root = mkdtempSync(path.join(tmpdir(), "sync-agents-width-"));
    try {
      for (const [rel, content] of Object.entries(files)) write(root, rel, content);
      assert.equal(formatWidth(root), width, JSON.stringify(files));
    } finally {
      rmSync(root, { recursive: true, force: true });
    }
  }
});

test("a first registration is written in the shape the repository's formatter prints", () => {
  withGuard((root) => {
    write(root, ".prettierrc", '{ "printWidth": 100 }\n');
    write(root, settingsFile, '{"permissions":{"allow":["Bash(ls:*)"]}}');
    syncMirrors(root);
    assert.equal(read(root, settingsFile), FRESH_AT_100);
    assert.deepEqual(syncMirrors(root, { check: true }).stale, []);
  });
});

test("guard.md is validated with the repository's own guard parser", async () => {
  const root = mkdtempSync(path.join(tmpdir(), "sync-agents-guard-"));
  try {
    assert.deepEqual(await checkGuardSettings(root), []);
    mkdirSync(path.join(root, "scripts"));
    copyFileSync(path.join(scriptsDir, "guard.mjs"), path.join(root, "scripts", "guard.mjs"));
    write(root, "docs/agents/guard.md", "## Deny commands\n\n- `supabase db reset` — wipes data.\n");
    assert.deepEqual(await checkGuardSettings(root), []);
    write(root, "docs/agents/guard.md", "## Deny commands\n\n- supabase db reset, no backticks\n");
    const [problem] = await checkGuardSettings(root);
    assert.match(problem, /docs\/agents\/guard\.md:3:/);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
});

test("spawned: --check fails on a malformed guard.md, and --hook still syncs an authored edit", () => {
  withGuard((root) => {
    copyFileSync(path.join(scriptsDir, "sync-agents.mjs"), path.join(root, "scripts", "sync-agents.mjs"));
    const run = (args, input = "") =>
      spawnSync(process.execPath, [path.join(root, "scripts", "sync-agents.mjs"), ...args], { input, encoding: "utf8" });

    const payload = { tool_input: { file_path: path.join(root, ".agents/skills/demo/SKILL.md") } };
    assert.equal(run(["--hook"], JSON.stringify(payload)).status, 0);
    assert.ok(existsSync(path.join(root, ".claude/skills/demo/SKILL.md")));
    assert.ok(existsSync(path.join(root, settingsFile)));

    assert.equal(run(["--check"]).status, 0);
    write(root, "docs/agents/guard.md", "## Ask commands\n\nnot a bullet\n");
    const failed = run(["--check"]);
    assert.equal(failed.status, 1);
    assert.match(failed.stderr, /guard\.md:3:/);
  });
});
