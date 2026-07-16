import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  extractMarkdownLinks,
  missingMetadata,
  parseAdr,
  stripFencedCode,
  validateRepository,
} from "./check-docs.mjs";

test("stripFencedCode excludes example links from policy checks", () => {
  const markdown =
    "[kept](docs/README.md)\n```markdown\n[ignored](private/TODO.md)\n```";
  assert.equal(stripFencedCode(markdown).includes("private/TODO.md"), false);
  assert.deepEqual(extractMarkdownLinks(markdown), [
    { target: "docs/README.md", line: 1 },
  ]);
});

test("missingMetadata reports only absent lifecycle fields", () => {
  const markdown = [
    "- **Audience:** Contributors",
    "- **Status:** Living guide",
    "- **Owner:** Maintainers",
  ].join("\n");
  assert.deepEqual(missingMetadata(markdown), ["Last reviewed"]);
});

test("parseAdr returns index metadata from a valid ADR", () => {
  const markdown = [
    "# ADR-024: Documentation policy checks",
    "",
    "- **Status:** Accepted",
    "- **Date:** 2026-07-10",
  ].join("\n");
  assert.deepEqual(parseAdr(markdown), {
    number: "024",
    title: "Documentation policy checks",
    status: "Accepted",
    date: "2026-07-10",
  });
});

test("validateRepository rejects links to private content", (context) => {
  const root = mkdtempSync(path.join(tmpdir(), "leasebook-docs-"));
  context.after(() => rmSync(root, { recursive: true, force: true }));
  mkdirSync(path.join(root, "docs", "adr"), { recursive: true });
  writeFileSync(
    path.join(root, "docs", "README.md"),
    [
      "# Docs",
      "",
      "- **Audience:** Contributors",
      "- **Status:** Living index",
      "- **Owner:** Maintainers",
      "- **Last reviewed:** 2026-07-10",
      "",
      "[internal](../private/TODO.md)",
    ].join("\n"),
  );
  writeFileSync(
    path.join(root, "docs", "adr", "README.md"),
    [
      "# ADRs",
      "",
      "- **Audience:** Contributors",
      "- **Status:** Living index",
      "- **Owner:** Maintainers",
      "- **Last reviewed:** 2026-07-10",
    ].join("\n"),
  );

  const errors = validateRepository(root, [
    "docs/README.md",
    "docs/adr/README.md",
  ]);
  assert.equal(
    errors.some((error) =>
      error.message.includes("links to ignored/private content"),
    ),
    true,
  );
});

test("skill files may name canonical commands like agent files", (context) => {
  const root = mkdtempSync(path.join(tmpdir(), "leasebook-docs-"));
  context.after(() => rmSync(root, { recursive: true, force: true }));
  const skill = ".claude/skills/ship/SKILL.md";
  mkdirSync(path.join(root, ".claude", "skills", "ship"), { recursive: true });
  writeFileSync(
    path.join(root, skill),
    ["# Ship", "", "Run `npm run typecheck` before pushing."].join("\n"),
  );

  const errors = validateRepository(root, [skill]);
  assert.equal(
    errors.some(
      (error) =>
        error.file === skill && error.message.includes("Mutable command"),
    ),
    false,
  );
});
