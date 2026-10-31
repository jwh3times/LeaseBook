import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  anchorsOf,
  docMapBody,
  docMapPaths,
  entryMatcher,
  headingSlug,
  lineTargets,
  validateRepository,
  visibleLines,
} from "./check-docs.mjs";

const MAP = "# Docs\n\n## Doc map\n\n- `README.md`, `docs/**` — everything.\n";

// A repository holding `files`, each tracked unless the path is under `ignored/`.
function withRepository(files, run) {
  const root = mkdtempSync(path.join(tmpdir(), "check-docs-"));
  try {
    execFileSync("git", ["-C", root, "init", "-q", "-b", "main"]);
    for (const [rel, content] of Object.entries({
      ".gitignore": "ignored/\n",
      "docs/agents/docs.md": MAP,
      ...files,
    })) {
      mkdirSync(path.dirname(path.join(root, rel)), { recursive: true });
      writeFileSync(path.join(root, rel), content);
    }
    return run(root);
  } finally {
    rmSync(root, { recursive: true, force: true });
  }
}

const problemsIn = (files) => withRepository(files, (root) => validateRepository(root).problems);

test("blanks fenced code and front matter, keeping line numbers", () => {
  assert.deepEqual(visibleLines("---\ntitle: x\n---\na\n```\nb\n```\nc"), [
    "",
    "",
    "",
    "a",
    "",
    "",
    "",
    "c",
  ]);
});

test("a fence closes only on the same character, at least as long, with nothing after it", () => {
  assert.deepEqual(visibleLines("````\n```\n~~~~\n```` x\n````\na"), ["", "", "", "", "", "a"]);
});

test("finds inline links, images, titles, angle targets and reference definitions", () => {
  assert.deepEqual(lineTargets('[a](b.md) ![c](d.png "t") [e](<f g.md>)'), ["b.md", "d.png", "f g.md"]);
  assert.deepEqual(lineTargets("[![badge](a.svg)](b.md)"), ["a.svg", "b.md"]);
  assert.deepEqual(lineTargets("[ref]: docs/x.md"), ["docs/x.md"]);
});

test("ignores links in inline code", () => {
  assert.deepEqual(lineTargets("write `[a](b.md)` or ``[c](d.md)``"), []);
});

test("slugs headings the way GitHub does", () => {
  assert.equal(headingSlug("`vlab merge`: the Tool"), "vlab-merge-the-tool");
  assert.equal(headingSlug("[Guide](guide.md) & more"), "guide--more");
  assert.equal(headingSlug("snake_case _stays_"), "snake_case-stays");
  assert.equal(headingSlug("Press <kbd>Enter</kbd> now"), "press-enter-now");
});

test("anchors number repeats, and include setext headings and explicit ids", () => {
  const anchors = anchorsOf('# A\n\n# A\n\nTitle\n=====\n\n<a name="here"></a>\n\n```\n# fenced\n```\n');
  assert.deepEqual([...anchors].sort(), ["a", "a-1", "here", "title"]);
});

test("reads the Doc map up to the next heading", () => {
  assert.equal(docMapBody("## Doc map\r\n\r\n- `a`\r\n\r\n## Next\r\n- `b`").trim(), "- `a`");
  assert.equal(docMapBody("# Docs\n"), null);
});

test("a Doc map item lists only the names it opens with", () => {
  const body = [
    "- `a.md`, `b.md` and `c/*.md` — owns `not-this.md`.",
    "- `one.md`,",
    "  `two.md` — wrapped.",
    "- `docs/agents/` settings (`workspace`, `ship`) — prose follows the path.",
    "  - `nested.md` — never counts.",
    "- links to `docs/plans/` stay out of public docs",
    "- The wiki's pages (`https://example.com`) — no path.",
  ].join("\n");
  assert.deepEqual(docMapPaths(body), [
    "a.md",
    "b.md",
    "c/*.md",
    "one.md",
    "two.md",
    "docs/agents/",
  ]);
});

test("an entry matches by exact path, folder or glob", () => {
  assert.ok(entryMatcher("README.md")("README.md"));
  assert.ok(!entryMatcher("README.md")("web/README.md"));
  assert.ok(entryMatcher("docs/schemas/")("docs/schemas/a/b.md"));
  assert.ok(!entryMatcher("docs/schemas")("docs/schemas2/b.md"));
  assert.ok(entryMatcher("docs/adr/*.md")("docs/adr/1.md"));
  assert.ok(!entryMatcher("docs/adr/*.md")("docs/adr/old/1.md"));
  assert.ok(entryMatcher("docs/**/*.md")("docs/a.md"));
  assert.ok(entryMatcher("docs/**/*.md")("docs/a/b/c.md"));
  assert.ok(entryMatcher(".agents/skills/*/**")(".agents/skills/x/SKILL.md"));
  assert.ok(entryMatcher(".agents/skills/*/")(".agents/skills/x/a/SKILL.md"));
  assert.ok(entryMatcher("docs/{a,b}.md")("docs/b.md"));
});

test("passes links that resolve, in every form", () => {
  assert.deepEqual(
    problemsIn({
      "README.md":
        "# Top\n\n[a](docs/a.md) [folder](docs/) [root](/docs/a.md#section) [self](#top)\n" +
        "[url](https://example.com/x.md) [mail](mailto:a@example.com) [line](docs/a.md#L12-L20)\n" +
        "[query](docs/a.md?plain=1) [space](docs/a%20b.md) [any fragment](data.json#x)\n",
      "docs/a.md": "## Section\n\n[back](../README.md#top)\n",
      "docs/a b.md": "text\n",
      "data.json": "{}\n",
    }),
    [],
  );
});

test("reports a missing file, a wrong case, an escape and a local absolute path", () => {
  const problems = problemsIn({
    "README.md":
      "[a](missing.md)\n[b](docs/A.md)\n[c](../outside.md)\n[d](C:/Users/me/x.md)\n[e](%zz.md)\n",
    "docs/a.md": "text\n",
  });
  assert.deepEqual(problems, [
    "README.md:1: link target has no tracked file or folder: missing.md",
    "README.md:2: link target has no tracked file or folder: docs/A.md",
    "README.md:3: link target has escapes the repository: ../outside.md",
    "README.md:4: link target has a local absolute path: C:/Users/me/x.md",
    "README.md:5: link target has invalid URL encoding: %zz.md",
  ]);
});

test("reports a link to an ignored path, which only resolves on this machine", () => {
  const problems = problemsIn({
    "README.md": "[a](ignored/secret.md)\n",
    "ignored/secret.md": "secret\n",
  });
  assert.deepEqual(problems, ["README.md:1: link target has no tracked file or folder: ignored/secret.md"]);
});

test("reports a fragment no heading answers to, in this file or another", () => {
  const problems = problemsIn({
    "README.md": "[a](#nope)\n[b](docs/a.md#nope)\n",
    "docs/a.md": "# Other\n",
  });
  assert.deepEqual(problems, [
    "README.md:1: link target has no heading or anchor '#nope' in README.md: #nope",
    "README.md:2: link target has no heading or anchor '#nope' in docs/a.md: docs/a.md#nope",
  ]);
});

test("reports a document the Doc map omits, and an entry that matches nothing", () => {
  const problems = problemsIn({
    "README.md": "text\n",
    "web/README.md": "text\n",
    "docs/agents/docs.md": "## Doc map\n\n- `README.md`, `docs/**` — a.\n- `gone.md` — b.\n",
  });
  assert.deepEqual(problems, [
    "docs/agents/docs.md:1: the Doc map does not list web/README.md",
    "docs/agents/docs.md:1: the Doc map lists gone.md, which matches no file",
  ]);
});

test("never asks a companion path or a URL to exist", () => {
  assert.deepEqual(
    problemsIn({
      "README.md": "text\n",
      "docs/agents/docs.md":
        "## Doc map\n\n- `README.md`, `docs/**` — a.\n- `private/notes.md` — b.\n" +
        "- `https://example.com/wiki` — c.\n",
    }),
    [],
  );
});

test("fails closed when the Doc map is missing", () => {
  assert.deepEqual(problemsIn({ "docs/agents/docs.md": "# Docs\n" }), [
    "docs/agents/docs.md:1: the `## Doc map` setting is missing",
  ]);
});

test("leaves installed skills and their mirrors out", () => {
  assert.deepEqual(
    problemsIn({
      "README.md": "text\n",
      ".agents/skills/x/SKILL.md": "[a](missing.md)\n",
      ".claude/skills/x/SKILL.md": "[a](missing.md)\n",
    }),
    [],
  );
});
