import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import path from "node:path";
import test from "node:test";

import {
  canonicalDocs,
  docsMissingFromTopology,
  extractMarkdownLinks,
  extractTopology,
  missingMetadata,
  parseAdr,
  reviewIsStale,
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

test("canonicalDocs excludes skills, ADR records, and .github", () => {
  const files = [
    "AGENTS.md",
    "docs/runbooks/diagnostics.md",
    "infra/README.md",
    ".claude/agents/dotnet-api.md",
    "docs/adr/ADR-041-durable-keyring-and-proxy-trust.md",
    "docs/adr/README.md",
    ".agents/skills/ship/SKILL.md",
    ".claude/skills/ship/SKILL.md",
    ".github/pull_request_template.md",
  ];

  assert.deepEqual(canonicalDocs(files), [
    "AGENTS.md",
    "docs/runbooks/diagnostics.md",
    "infra/README.md",
    ".claude/agents/dotnet-api.md",
    "docs/adr/README.md",
  ]);
});

test("docsMissingFromTopology reports a canonical doc the agent was never told about", () => {
  const topology = "## Documentation topology\nAGENTS.md — the contract\n";

  assert.deepEqual(
    docsMissingFromTopology(topology, [
      "AGENTS.md",
      "docs/runbooks/diagnostics.md",
    ]),
    ["docs/runbooks/diagnostics.md"],
  );
});

test("extractTopology stops at the horizontal rule that ends the section", () => {
  const markdown =
    "# Agent\n\n## Documentation topology\n\nAGENTS.md\n\n---\n\n## Audit checklist\n";
  const topology = extractTopology(markdown);

  assert.ok(topology.includes("AGENTS.md"));
  assert.ok(!topology.includes("Audit checklist"));
  assert.equal(extractTopology("# Agent\n\nno topology here\n"), null);
});

test("reviewIsStale flags an edit after the review date but tolerates a one-day skew", () => {
  // The real case: diagnostics.md, reviewed 2026-08-12 and edited six days later.
  assert.equal(reviewIsStale("2026-08-12", "2026-08-18"), true);
  // A commit either side of midnight UTC must not fail a document that was reviewed.
  assert.equal(reviewIsStale("2026-08-18", "2026-08-19"), false);
  // Reviewed after the edit is the healthy direction.
  assert.equal(reviewIsStale("2026-08-19", "2026-08-18"), false);
  // Missing either side is the metadata rule's problem, not this one's.
  assert.equal(reviewIsStale(null, "2026-08-18"), false);
  assert.equal(reviewIsStale("2026-08-18", null), false);
});
