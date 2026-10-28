import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { lintNeutrality } from "./lint-agent-neutrality.mjs";

function withFixture(files, fn) {
  const root = mkdtempSync(path.join(tmpdir(), "lint-agent-neutrality-"));
  try {
    for (const [rel, content] of Object.entries(files)) {
      mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
      writeFileSync(path.join(root, rel), content);
    }
    fn(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

test("clean sources pass, including links that resolve from both locations", () => {
  withFixture(
    {
      ".github/workflows/version.yml": "on: push\n",
      ".agents/skills/ship/SKILL.md": "See [version](../../../.github/workflows/version.yml).\n",
      ".agents/skills/teach/SKILL.md": "See [format](./FORMAT.md).\n",
      ".agents/skills/teach/FORMAT.md": "Format.\n",
      ".claude/agents/docs.md": "See [missing](./nowhere.md) — not a real file, so not flagged.\n",
    },
    (root) => assert.deepEqual(lintNeutrality(root), []),
  );
});

test("substitution artifacts are reported", () => {
  withFixture(
    {
      ".claude/agents/docs.md": "Read .Codex/settings.json and https://code.Codex.com/docs.\n",
      ".agents/skills/x/notes.md": "Put it in .Codex/skills.\n",
    },
    (root) => {
      const problems = lintNeutrality(root);
      assert.equal(problems.length, 3);
      assert.match(problems.join("\n"), /\.claude\/agents\/docs\.md: contains "\.Codex\/"/);
      assert.match(problems.join("\n"), /code\.Codex\.com/);
      assert.match(problems.join("\n"), /\.agents\/skills\/x\/notes\.md/);
    },
  );
});

test("bare positional parameters in a skill body are reported", () => {
  withFixture(
    {
      ".agents/skills/x/SKILL.md": "---\nname: x\n---\n\n```bash\nset -- $pr\n[ \"$3\" = a ] && echo ${1:-none}\n```\n",
      ".agents/skills/x/scripts/run.sh": "echo \"$1\"\n",
      ".claude/agents/docs.md": "Run `awk '{print $2}'`.\n",
    },
    (root) => {
      const problems = lintNeutrality(root);
      assert.equal(problems.length, 1);
      assert.match(problems[0], /\.agents\/skills\/x\/SKILL\.md:7: bare `\$3`/);
    },
  );
});

test("a link that breaks once mirrored is reported", () => {
  withFixture(
    {
      ".claude/skills/ship/SKILL.md": "---\nname: ship\n---\n",
      ".claude/agents/docs.md": "Use [ship](../skills/ship/SKILL.md).\n",
      ".claude/agents/other.md": "See [peer](./docs.md).\n",
    },
    (root) => {
      const problems = lintNeutrality(root);
      assert.equal(problems.length, 2);
      assert.match(problems[0], /docs\.md: relative link `\.\.\/skills\/ship\/SKILL\.md`/);
      assert.match(problems[1], /other\.md: relative link `\.\/docs\.md`/);
    },
  );
});

test("published skills get the same checks, and links must stay inside their skill folder", () => {
  withFixture(
    {
      "docs/agents/ship.md": "Settings.\n",
      "skills/ship/SKILL.md":
        "See [settings](./settings/ship.md) and [repo](../../docs/agents/ship.md).\n\n```bash\necho \"$1\"\n```\n",
      "skills/ship/settings/ship.md": "Back to [skill](../SKILL.md); see .Codex/x.\n",
    },
    (root) => {
      const problems = lintNeutrality(root);
      assert.equal(problems.length, 3);
      assert.match(problems[0], /skills\/ship\/SKILL\.md:4: bare `\$1`/);
      assert.match(problems[1], /skills\/ship\/SKILL\.md: relative link `\.\.\/\.\.\/docs\/agents\/ship\.md` leaves skills\/ship\//);
      assert.match(problems[2], /skills\/ship\/settings\/ship\.md: contains "\.Codex\/"/);
    },
  );
});

test("the CLI lints the root it is given and names it", () => {
  const script = fileURLToPath(new URL("./lint-agent-neutrality.mjs", import.meta.url));
  withFixture({ ".agents/skills/x/SKILL.md": "Clean.\n" }, (root) => {
    const run = spawnSync(process.execPath, [script, root], { encoding: "utf8" });
    assert.equal(run.status, 0);
    assert.ok(run.stdout.includes(`in ${root} are tool-neutral`), run.stdout);
  });
  withFixture({ ".agents/skills/x/SKILL.md": "See .Codex/x.\n" }, (root) => {
    const run = spawnSync(process.execPath, [script, root], { encoding: "utf8" });
    assert.equal(run.status, 1);
    assert.match(run.stderr, /lint failed in /);
    assert.match(run.stderr, /contains "\.Codex\/"/);
  });
});
