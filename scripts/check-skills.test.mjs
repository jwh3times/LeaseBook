import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import {
  mkdirSync,
  mkdtempSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  checkSkills,
  folderHashV2,
  matchesLock,
  parseInstallCommands,
} from "./check-skills.mjs";

const blocks = `# Topic

<!-- shared:shared-skills -->

\`\`\`bash
npx skills add jwh3times/skills -s ship -a codex --copy -y
npx skills add mattpocock/skills#abc123 -s tdd \\
  -s wizard -a codex --copy -y
node scripts/sync-agents.mjs
\`\`\`

<!-- /shared:shared-skills -->
`;

// The hash as the CLI writes it: relative path then bytes, in localeCompare order.
function cliHash(files) {
  const hash = createHash("sha256");
  for (const name of Object.keys(files).sort((a, b) => a.localeCompare(b))) {
    hash.update(name);
    hash.update(files[name]);
  }
  return hash.digest("hex");
}

// The version 2 hash as the CLI's README states it, written apart from the script's.
function cliHashV2(files) {
  const hash = createHash("sha256");
  const entries = Object.entries(files)
    .map(([name, content]) => [Buffer.from(name), Buffer.from(content)])
    .map(([name, content]) => [
      name,
      content.includes(0)
        ? content
        : Buffer.from(
            content.toString("latin1").replaceAll("\r\n", "\n"),
            "latin1",
          ),
    ])
    .sort(([a], [b]) => Buffer.compare(a, b));
  for (const [name, content] of entries) {
    hash.update(
      Buffer.concat([name, Buffer.from(`\0${content.length}\0`), content]),
    );
  }
  return hash.digest("hex");
}

function repository(t, { skills, lock, version = 1 }) {
  const root = mkdtempSync(path.join(tmpdir(), "check-skills-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  const write = (rel, content) => {
    const file = path.join(root, rel);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, content);
  };
  write(".agents/skills/ship/settings/agents-md.md", blocks);
  for (const [name, files] of Object.entries(skills)) {
    for (const [rel, content] of Object.entries(files))
      write(`.agents/skills/${name}/${rel}`, content);
  }
  write("skills-lock.json", JSON.stringify({ version, skills: lock }));
  return root;
}

const skillFiles = {
  "SKILL.md": "# A skill\n",
  "agents/openai.yaml": "a: 1\n",
};
const shipFiles = { "SKILL.md": "# Ship\n", "settings/agents-md.md": blocks };

function goodLock(overrides = {}, hashOf = cliHash) {
  return {
    ship: { source: "jwh3times/skills", computedHash: hashOf(shipFiles) },
    tdd: {
      source: "mattpocock/skills",
      ref: "abc123",
      computedHash: hashOf(skillFiles),
    },
    wizard: {
      source: "mattpocock/skills",
      ref: "abc123",
      computedHash: hashOf(skillFiles),
    },
    ...overrides,
  };
}

const goodSkills = { ship: shipFiles, tdd: skillFiles, wizard: skillFiles };

test("reads each install command's source, ref and names, across a continued line", () => {
  assert.deepEqual(parseInstallCommands(blocks), [
    { source: "jwh3times/skills", ref: null, names: ["ship"] },
    { source: "mattpocock/skills", ref: "abc123", names: ["tdd", "wizard"] },
  ]);
  assert.deepEqual(
    parseInstallCommands(blocks.replaceAll("\n", "\r\n"))[1].names,
    ["tdd", "wizard"],
  );
});

test("refuses a block with no install command, or no block", () => {
  assert.throws(
    () => parseInstallCommands("nothing here"),
    /no shared-skills block/,
  );
  assert.throws(
    () =>
      parseInstallCommands(
        "<!-- shared:shared-skills -->\nprose\n<!-- /shared:shared-skills -->",
      ),
    /no install command/,
  );
});

test("the catalog pins one snapshot by commit and lists the agreed set", () => {
  const text = readFileSync(
    new URL("../.agents/skills/ship/settings/agents-md.md", import.meta.url),
    "utf8",
  );
  const [shared, thirdParty, ...rest] = parseInstallCommands(text);
  assert.deepEqual(rest, []);
  assert.equal(shared.source, "jwh3times/skills");
  assert.equal(shared.ref, null);
  assert.equal(thirdParty.source, "mattpocock/skills");
  assert.match(thirdParty.ref, /^[0-9a-f]{40}$/);
  assert.equal(thirdParty.names.length, 23);
  for (const skill of [
    "ask-matt",
    "code-review",
    "implement",
    "triage",
    "wayfinder",
  ]) {
    assert.ok(thirdParty.names.includes(skill), skill);
  }
  // Removed upstream, and replaced by the shared skill of the same name.
  for (const skill of ["resolving-merge-conflicts", "handoff"]) {
    assert.ok(!thirdParty.names.includes(skill), skill);
  }
});

test("passes when every skill matches its lock entry and the commands", (t) => {
  assert.deepEqual(
    checkSkills(repository(t, { skills: goodSkills, lock: goodLock() })),
    [],
  );
});

test("accepts a hash taken from LF or CRLF, whichever the folder holds", (t) => {
  const crlf = Object.fromEntries(
    Object.entries(skillFiles).map(([rel, content]) => [
      rel,
      content.replaceAll("\n", "\r\n"),
    ]),
  );
  const root = repository(t, {
    skills: { ...goodSkills, tdd: crlf },
    lock: goodLock({
      wizard: {
        source: "mattpocock/skills",
        ref: "abc123",
        computedHash: cliHash(crlf),
      },
    }),
  });
  assert.deepEqual(checkSkills(root), []);
  assert.ok(
    matchesLock(path.join(root, ".agents/skills/tdd"), cliHash(skillFiles)),
  );
});

test("fails on an edit, even a formatter's", (t) => {
  const root = repository(t, {
    skills: {
      ...goodSkills,
      tdd: { ...skillFiles, "SKILL.md": "# A  skill\n" },
    },
    lock: goodLock(),
  });
  assert.deepEqual(checkSkills(root), [
    "tdd: .agents/skills/tdd differs from skills-lock.json",
  ]);
});

test("fails on an added or a removed file", (t) => {
  const added = repository(t, {
    skills: { ...goodSkills, tdd: { ...skillFiles, "extra.md": "more\n" } },
    lock: goodLock(),
  });
  assert.deepEqual(checkSkills(added), [
    "tdd: .agents/skills/tdd differs from skills-lock.json",
  ]);
  const removed = repository(t, {
    skills: { ...goodSkills, tdd: { "SKILL.md": skillFiles["SKILL.md"] } },
    lock: goodLock(),
  });
  assert.deepEqual(checkSkills(removed), [
    "tdd: .agents/skills/tdd differs from skills-lock.json",
  ]);
});

test("fails on another snapshot, an unpinned one, or another source", (t) => {
  const hash = cliHash(skillFiles);
  const problems = checkSkills(
    repository(t, {
      skills: goodSkills,
      lock: goodLock({
        tdd: { source: "mattpocock/skills", ref: "def456", computedHash: hash },
        wizard: { source: "someone/else", ref: "abc123", computedHash: hash },
      }),
    }),
  );
  assert.deepEqual(problems, [
    "tdd: locked at def456, expected abc123",
    "wizard: locked from someone/else, expected mattpocock/skills",
  ]);
  const unpinned = checkSkills(
    repository(t, {
      skills: goodSkills,
      lock: goodLock({
        tdd: { source: "mattpocock/skills", computedHash: hash },
      }),
    }),
  );
  assert.deepEqual(unpinned, ["tdd: locked at no ref, expected abc123"]);
});

test("fails on a missing skill, an unlisted one from a named source, and a missing folder", (t) => {
  const hash = cliHash(skillFiles);
  const { wizard, ...withoutWizard } = goodLock({
    teach: { source: "mattpocock/skills", ref: "abc123", computedHash: hash },
    gone: { source: "someone/else", computedHash: hash },
  });
  assert.ok(wizard);
  const problems = checkSkills(
    repository(t, {
      skills: { ...goodSkills, teach: skillFiles },
      lock: withoutWizard,
    }),
  );
  assert.deepEqual(problems, [
    "wizard: not in skills-lock.json, but the shared install commands name it",
    "teach: locked from mattpocock/skills, but the shared install commands do not name it",
    "gone: in skills-lock.json, but .agents/skills/gone is missing",
  ]);
});

test("leaves a repository's own skill, and another source's matching one, alone", (t) => {
  const root = repository(t, {
    skills: { ...goodSkills, "own-release": skillFiles, other: skillFiles },
    lock: goodLock({
      other: { source: "someone/else", computedHash: cliHash(skillFiles) },
    }),
  });
  assert.deepEqual(checkSkills(root), []);
});

test("reports a missing lock or a missing ship skill", (t) => {
  const noLock = repository(t, { skills: goodSkills, lock: goodLock() });
  rmSync(path.join(noLock, "skills-lock.json"));
  assert.deepEqual(checkSkills(noLock), ["skills-lock.json: not found"]);
  const noShip = repository(t, { skills: goodSkills, lock: goodLock() });
  rmSync(path.join(noShip, ".agents/skills/ship/settings/agents-md.md"));
  assert.match(checkSkills(noShip)[0], /install the ship skill/);
});

function folder(t, files) {
  const root = mkdtempSync(path.join(tmpdir(), "check-skills-v2-"));
  t.after(() => rmSync(root, { recursive: true, force: true }));
  for (const [rel, content] of Object.entries(files)) {
    const file = path.join(root, rel);
    mkdirSync(path.dirname(file), { recursive: true });
    writeFileSync(file, content);
  }
  return root;
}

test("a version 2 hash matches the CLI README's test vectors", (t) => {
  assert.equal(
    folderHashV2(folder(t, {})),
    "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
  );
  assert.equal(
    folderHashV2(folder(t, { "SKILL.md": "# hi\n" })),
    "93c36952d8843b33e68ae970ebabcc03e1687c9bb0dd1ecaf357060318952f55",
  );
  assert.equal(
    folderHashV2(folder(t, { "SKILL.md": "a\n", "docs/b.md": "b\n" })),
    "b22c4d22aa319d2924312fa18fcbfedc88fa89c74dbd8f298c683e1caf7b7f24",
  );
});

test("a version 2 lock passes, from a CRLF folder or an LF one", (t) => {
  const crlf = Object.fromEntries(
    Object.entries(skillFiles).map(([rel, content]) => [
      rel,
      content.replaceAll("\n", "\r\n"),
    ]),
  );
  const lock = goodLock({}, cliHashV2);
  assert.equal(cliHashV2(crlf), cliHashV2(skillFiles));
  assert.deepEqual(
    checkSkills(
      repository(t, { skills: { ...goodSkills, tdd: crlf }, lock, version: 2 }),
    ),
    [],
  );
});

test("a version 2 lock is not read as version 1, nor the reverse", (t) => {
  assert.deepEqual(
    checkSkills(
      repository(t, { skills: goodSkills, lock: goodLock(), version: 2 }),
    ),
    [
      "ship: .agents/skills/ship differs from skills-lock.json",
      "tdd: .agents/skills/tdd differs from skills-lock.json",
      "wizard: .agents/skills/wizard differs from skills-lock.json",
    ],
  );
  assert.equal(
    checkSkills(
      repository(t, { skills: goodSkills, lock: goodLock({}, cliHashV2) }),
    ).length,
    3,
  );
});

test("a version 2 hash sorts by bytes, keeps a binary file's CRLF, and skips what the CLI skips", (t) => {
  // localeCompare puts "a.md" first; bytes put "B.md" first.
  const mixed = { "B.md": "b\n", "a.md": "a\n" };
  assert.equal(folderHashV2(folder(t, mixed)), cliHashV2(mixed));
  assert.notEqual(cliHashV2(mixed), cliHash(mixed));

  const binary = { "SKILL.md": "x\n", "icon.bin": "\0\r\n" };
  assert.equal(folderHashV2(folder(t, binary)), cliHashV2(binary));
  assert.notEqual(
    folderHashV2(folder(t, binary)),
    folderHashV2(folder(t, { ...binary, "icon.bin": "\0\n" })),
  );

  const base = { "SKILL.md": "x\n", "node_modules/kept.js": "k\n" };
  assert.equal(
    folderHashV2(
      folder(t, {
        ...base,
        "metadata.json": "{}\n",
        "sub/metadata.json": "{}\n",
        "__pycache__/a.pyc": "c",
        "lib/__pypackages__/b.py": "p",
        ".git/HEAD": "ref\n",
      }),
    ),
    cliHashV2(base),
  );
});

test("reports a lock entry left without a hash, and a lock version it cannot read", (t) => {
  const lock = goodLock({}, cliHashV2);
  lock.tdd.computedHash = "";
  assert.deepEqual(
    checkSkills(repository(t, { skills: goodSkills, lock, version: 2 })),
    [
      "tdd: no hash in skills-lock.json; the CLI could not carry it over from a version 1 lock",
    ],
  );
  assert.deepEqual(
    checkSkills(
      repository(t, { skills: goodSkills, lock: goodLock(), version: 3 }),
    ),
    [
      "skills-lock.json: version 3, which this check-skills.mjs cannot read; take the latest from jwh3times/skills",
    ],
  );
});
