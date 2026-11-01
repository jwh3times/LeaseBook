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

function repository(t, { skills, lock }) {
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
  write("skills-lock.json", JSON.stringify({ version: 1, skills: lock }));
  return root;
}

const skillFiles = {
  "SKILL.md": "# A skill\n",
  "agents/openai.yaml": "a: 1\n",
};
const shipFiles = { "SKILL.md": "# Ship\n", "settings/agents-md.md": blocks };

function goodLock(overrides = {}) {
  return {
    ship: { source: "jwh3times/skills", computedHash: cliHash(shipFiles) },
    tdd: {
      source: "mattpocock/skills",
      ref: "abc123",
      computedHash: cliHash(skillFiles),
    },
    wizard: {
      source: "mattpocock/skills",
      ref: "abc123",
      computedHash: cliHash(skillFiles),
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
