// LeaseBook's own documentation rules: lifecycle metadata and review dates, copied commands,
// authority claims and the ADR index. Links, anchors and Doc map coverage are the canonical
// scripts/check-docs.mjs, which `npm run docs:check` runs after this.
import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

const metadataFields = ["Audience", "Status", "Owner", "Last reviewed"];
const commandFingerprints = [
  "dotnet build LeaseBook.slnx",
  "dotnet test --solution LeaseBook.slnx",
  "npm run typecheck",
  "check-invariants --org demo",
];

function normalizePath(value) {
  return value.replaceAll("\\", "/");
}

export function stripFencedCode(markdown) {
  const output = [];
  let fence = null;

  for (const line of markdown.split(/\r?\n/)) {
    const match = line.match(/^\s*(`{3,}|~{3,})/);
    if (match) {
      if (fence === null) {
        fence = match[1][0];
      } else if (match[1][0] === fence) {
        fence = null;
      }
      output.push("");
      continue;
    }

    output.push(fence === null ? line : "");
  }

  return output.join("\n");
}

export function parseAdr(markdown) {
  const heading = markdown.match(/^# ADR-(?<number>\d{3}): (?<title>.+)$/m);
  const status = markdown.match(/^- \*\*Status:\*\* (?<value>.+)$/m);
  const date = markdown.match(/^- \*\*Date:\*\* (?<value>\d{4}-\d{2}-\d{2})$/m);
  // An ADR may be amended more than once, and the header then wraps: the second link sits on a
  // continuation line, and prose naming the same ADRs follows the last one. Capture only the
  // leading comma-separated run of links, across line breaks, so the index row can name every
  // amender (the legend's `Accepted (amended by ADR-037, ADR-039)` form) instead of just the first.
  const amendedBy = markdown.match(
    /^- \*\*Amended by:\*\* (?<value>\[ADR-\d{3}\]\([^)]+\)(?:,\s+\[ADR-\d{3}\]\([^)]+\))*)/m,
  );

  if (!heading || !status || !date) {
    return null;
  }

  const adr = {
    number: heading.groups.number,
    title: heading.groups.title.trim(),
    status: status.groups.value.trim(),
    date: date.groups.value,
  };
  if (amendedBy) {
    // Read the link labels only; the target file name repeats the number.
    adr.amendedBy = [...amendedBy.groups.value.matchAll(/\[(ADR-\d{3})\]/g)]
      .map((match) => match[1])
      .join(", ");
  }
  return adr;
}

export function missingMetadata(markdown) {
  return metadataFields.filter(
    (field) => !markdown.includes(`- **${field}:**`),
  );
}

/**
 * Documents the docs-updater skill is expected to know about: every committed markdown at the repo
 * root, under `docs/`, under `infra/`, or in `.claude/agents/`. Individual ADR records are excluded
 * because the ADR-index rule below already reconciles them one by one, and listing 40+ of them in the
 * Doc map would bury the rest.
 */
/**
 * Canonical docs absent from the Doc map. An omission there is invisible in a way a missing file is
 * not: docs-updater simply never audits what it was not told exists. `diagnostics.md` sat outside
 * the old topology block for months and every audit skipped it.
 */
/** The Doc map: everything between its heading and the next `## ` heading. */
/**
 * True when a document was edited after the date it claims to have been reviewed.
 *
 * One day of tolerance, deliberately: a commit authored either side of midnight UTC would otherwise
 * fail a document whose author did review it. The gaps worth catching are weeks wide — this rule
 * exists because six living documents were edited between one and six weeks after their stated
 * review, two of which were describing behaviour that had since changed.
 */
export function reviewIsStale(reviewedOn, lastCommitOn, toleranceDays = 1) {
  if (!reviewedOn || !lastCommitOn) {
    return false;
  }

  const day = 24 * 60 * 60 * 1000;
  const drift = (Date.parse(lastCommitOn) - Date.parse(reviewedOn)) / day;
  return Number.isFinite(drift) && drift > toleranceDays;
}

/**
 * Documents whose `Last reviewed` tracks something other than an engineering edit, so the rule above
 * does not apply. Both compliance drafts are dated by the EXTERNAL review that gates them; bumping
 * them because a link or a table cell changed would overstate their standing.
 */
const reviewDateExempt = new Set([
  "docs/compliance/data-handling.md",
  "docs/compliance/privacy-notice-draft.md",
  // A locked pre-M0 baseline that accepted ADRs supersede. The repository has deliberately decided
  // NOT to refresh it for later changes — its `ITenantContext` mention survives #194 on purpose — so
  // a review date tracking engineering edits would only ever nag toward the wrong action.
  "docs/blueprint.md",
]);

function lastCommitDate(root, file) {
  try {
    const out = execFileSync("git", ["log", "-1", "--format=%cs", "--", file], {
      cwd: root,
      encoding: "utf8",
    }).trim();
    return out || null;
  } catch {
    return null;
  }
}

function listMarkdownFiles(root) {
  const result = execFileSync(
    "git",
    ["ls-files", "--cached", "--others", "--exclude-standard", "--", "*.md"],
    { cwd: root, encoding: "utf8" },
  );

  return result.split(/\r?\n/).map(normalizePath).filter(Boolean).sort();
}

/**
 * The settings files the skills installed from jwh3times/skills read (`docs/agents/<topic>.md`).
 * They are configuration, not living documents: a skill's first run appends settings to them and
 * the docs-updater skill edits them, and neither knows this repository's lifecycle metadata or
 * review-date rule, so holding them to it would fail CI a day after a correct edit. They are still
 * authority documents (see `isAuthorityDoc`) and still covered by the Doc map.
 */
const skillSettings = new Set(
  [
    "workspace",
    "issue-tracker",
    "human-follow-up",
    "docs",
    "releases",
    "ship",
    "handoffs",
  ].map((topic) => `docs/agents/${topic}.md`),
);

function isLivingDoc(file) {
  return (
    file.startsWith("docs/") &&
    !/^docs\/adr\/ADR-\d{3}-.+\.md$/.test(file) &&
    file !== "docs/adr/template.md" &&
    !skillSettings.has(file)
  );
}

/**
 * Documents whose authority claims are load-bearing. Wider than `isLivingDoc` on purpose: the
 * root contract files and the agent/skill instructions are read as binding, so a stale
 * "X is canonical" line does the same damage there as it does under `docs/` — arguably more, since
 * an agent acts on it without a human reading the sentence.
 *
 * This scope is not hypothetical. `AGENTS.md` carried a live
 * "private/TODO.md ... canonical where they disagree" claim that the obsolete-claim patterns below
 * were written to catch, and the gate stayed green because AGENTS.md is not under `docs/`.
 *
 * Generated mirrors (`.claude/skills/`, `.codex/`) are excluded: they are byte-identical to the
 * authored sources already scanned, so including them would double-report one defect.
 */
function isAuthorityDoc(file) {
  return (
    isLivingDoc(file) ||
    skillSettings.has(file) ||
    file === "AGENTS.md" ||
    file === "CLAUDE.md" ||
    file === "README.md" ||
    file === "CONTRIBUTING.md" ||
    file === "CONTEXT.md" ||
    file.startsWith(".claude/agents/") ||
    file.startsWith(".agents/skills/")
  );
}

function commandCopyAllowed(file) {
  return (
    file === "AGENTS.md" ||
    file === "CONTRIBUTING.md" ||
    file === "docs/runbooks/local-dev.md" ||
    file.startsWith(".claude/agents/") ||
    // `.claude/skills/` is generated from the allow-listed `.agents/skills/` sources by
    // scripts/sync-agents.mjs; neither tree is hand-edited to duplicate a canonical command.
    file.startsWith(".claude/skills/") ||
    file.startsWith(".agents/skills/") ||
    file.startsWith("docs/adr/")
  );
}

function lineOf(content, index) {
  return content.slice(0, index).split(/\r?\n/).length;
}

export function validateRepository(root = defaultRoot, suppliedFiles) {
  const files = suppliedFiles ?? listMarkdownFiles(root);
  const errors = [];
  const contents = new Map();

  for (const file of files) {
    const absolute = path.join(root, file);
    if (!existsSync(absolute)) {
      errors.push({
        file,
        line: 1,
        message: "Document is listed but missing.",
      });
      continue;
    }
    contents.set(file, readFileSync(absolute, "utf8"));
  }

  for (const [file, content] of contents) {
    if (isLivingDoc(file)) {
      for (const field of missingMetadata(content)) {
        errors.push({
          file,
          line: 1,
          message: `Living document is missing lifecycle metadata: ${field}.`,
        });
      }
    }

    if (!commandCopyAllowed(file)) {
      for (const command of commandFingerprints) {
        const index = content.indexOf(command);
        if (index >= 0) {
          errors.push({
            file,
            line: lineOf(content, index),
            message: `Mutable command duplicates the canonical runbook: ${command}.`,
          });
        }
      }
    }
  }

  const authorityText = [...contents]
    .filter(([file]) => isAuthorityDoc(file))
    .map(([file, content]) => ({ file, content: stripFencedCode(content) }));
  const obsoleteClaims = [
    /CLAUDE\.md.{0,80}(authoritative|binding|canonical)/gi,
    /(authoritative|binding|canonical).{0,80}CLAUDE\.md/gi,
    /private\/(TODO|roadmap)\.md.{0,80}(authoritative|binding|canonical)/gi,
  ];

  for (const { file, content } of authorityText) {
    for (const pattern of obsoleteClaims) {
      for (const match of content.matchAll(pattern)) {
        errors.push({
          file,
          line: lineOf(content, match.index),
          message:
            "Authoritative document contains an obsolete canonical-authority claim.",
        });
      }
    }
  }

  // ── Review-date staleness (a doc edited after it was last reviewed) ─────────
  for (const [file, content] of contents) {
    if (!isLivingDoc(file) || reviewDateExempt.has(file)) {
      continue;
    }

    const reviewed = content.match(
      /- \*\*Last reviewed:\*\*\s*(\d{4}-\d{2}-\d{2})/,
    );
    if (!reviewed) {
      continue; // absence is already reported by the metadata rule above
    }

    const committed = lastCommitDate(root, file);
    if (reviewIsStale(reviewed[1], committed)) {
      errors.push({
        file,
        line: lineOf(content, reviewed.index),
        message:
          `Document was edited on ${committed} but claims it was last reviewed on ${reviewed[1]}. ` +
          "Re-read it against what changed, then bump the date — or add it to reviewDateExempt in " +
          "scripts/check-docs-policy.mjs if its review date tracks something other than an engineering edit.",
      });
    }
  }

  const adrFiles = files.filter((file) =>
    /^docs\/adr\/ADR-\d{3}-.+\.md$/.test(file),
  );
  const indexFile = "docs/adr/README.md";
  const indexContent = contents.get(indexFile);
  if (!indexContent) {
    errors.push({ file: indexFile, line: 1, message: "ADR index is missing." });
  } else {
    const rows = new Map();
    const rowPattern =
      /^\|\s+\[(?<number>\d{3})\]\((?<file>[^)]+)\)\s+\|\s+(?<title>.*?)\s+\|\s+(?<status>.*?)\s+\|\s+(?<date>\d{4}-\d{2}-\d{2})\s+\|$/gm;
    for (const row of indexContent.matchAll(rowPattern)) {
      if (rows.has(row.groups.number)) {
        errors.push({
          file: indexFile,
          line: lineOf(indexContent, row.index),
          message: `ADR-${row.groups.number} appears more than once in the index.`,
        });
        continue;
      }
      rows.set(row.groups.number, {
        number: row.groups.number,
        file: row.groups.file.trim(),
        title: row.groups.title.trim(),
        status: row.groups.status.trim(),
        date: row.groups.date,
      });
    }

    for (const file of adrFiles) {
      const adr = parseAdr(contents.get(file));
      if (!adr) {
        errors.push({
          file,
          line: 1,
          message: "ADR must include a numbered heading, status, and ISO date.",
        });
        continue;
      }

      const row = rows.get(adr.number);
      if (!row) {
        errors.push({
          file: indexFile,
          line: 1,
          message: `ADR-${adr.number} is missing from the index.`,
        });
        continue;
      }

      const expectedFile = path.basename(file);
      const expectedStatus = adr.amendedBy
        ? `${adr.status} (amended by ${adr.amendedBy})`
        : adr.status;
      for (const [field, actual, expected] of [
        ["file", row.file, expectedFile],
        ["title", row.title, adr.title],
        ["status", row.status, expectedStatus],
        ["date", row.date, adr.date],
      ]) {
        if (actual !== expected) {
          errors.push({
            file: indexFile,
            line: 1,
            message: `ADR-${adr.number} index ${field} is '${actual}', expected '${expected}'.`,
          });
        }
      }
    }

    for (const number of rows.keys()) {
      if (!adrFiles.some((file) => file.includes(`ADR-${number}-`))) {
        errors.push({
          file: indexFile,
          line: 1,
          message: `ADR-${number} is indexed but its file is missing.`,
        });
      }
    }
  }

  return errors;
}

function printErrors(errors) {
  for (const error of errors) {
    console.error(`${error.file}:${error.line}: ${error.message}`);
  }
}

const isMain =
  process.argv[1] && path.resolve(process.argv[1]) === path.resolve(scriptPath);
if (isMain) {
  const errors = validateRepository();
  if (errors.length > 0) {
    printErrors(errors);
    process.exitCode = 1;
  } else {
    console.log("Documentation policy check passed.");
  }
}
