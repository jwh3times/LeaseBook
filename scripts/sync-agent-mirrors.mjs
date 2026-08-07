// Generates the non-Claude harness copies of the agent instructions, and Claude Code's own copy
// of the skill instructions. Two independent directions, because the two trees have different
// authored sources:
//
//   .claude/agents/  (authored) --> .codex/agents/*.toml   (generated, for Codex)
//   .agents/skills/  (authored) --> .claude/skills/         (generated, for Claude Code)
//
// `.agents/skills/` is authored because that's where the third-party skills installer writes —
// installing or updating a skill has to stay a one-way operation with no manual copying back into
// `.claude/`. Neither generated tree is hand-maintained: both are derived here so the instructions
// cannot drift apart. CI runs this with `--check`, so a change to an authored tree that skips the
// regen fails the build.
//
//   node scripts/sync-agent-mirrors.mjs          # rewrite the mirrors
//   node scripts/sync-agent-mirrors.mjs --check  # verify only; exit 1 if stale

import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

const agentSourceDir = ".claude/agents";
const codexAgentDir = ".codex/agents";

const skillSourceDir = ".agents/skills";
const skillTargetDir = ".claude/skills";

function normalizeNewlines(value) {
  return value.replaceAll("\r\n", "\n");
}

export function splitFrontmatter(raw, file) {
  const match = /^---\n([\s\S]*?)\n---\n?/.exec(normalizeNewlines(raw));
  if (!match) {
    throw new Error(`${file}: expected YAML frontmatter delimited by '---'`);
  }

  const data = new Map();
  for (const line of match[1].split("\n")) {
    const field = /^([A-Za-z][\w-]*):\s*(.*)$/.exec(line);
    if (field) {
      data.set(field[1], field[2].trim());
    }
  }

  return { data, body: normalizeNewlines(raw).slice(match[0].length).trim() };
}

// TOML basic string. JSON's escape set (\" \\ \b \t \n \f \r \uXXXX) is a subset of TOML's, so
// stringify produces a valid TOML basic string for any scalar these frontmatters can hold.
function tomlBasicString(value) {
  return JSON.stringify(value);
}

// TOML *literal* multi-line string. Literal strings perform no escape processing, which is what
// the instruction bodies need: they are full of C# raw-string literals (`"""`), shell line
// continuations (`\`), and Windows-ish paths that a basic `"""` string would mangle silently.
export function tomlLiteralMultiline(body, file, field) {
  if (body.includes("'''")) {
    throw new Error(
      `${file}: '${field}' contains ''' which cannot appear in a TOML literal string. ` +
        `Reword the source in ${agentSourceDir}/.`,
    );
  }
  if (body.endsWith("'")) {
    throw new Error(
      `${file}: '${field}' ends with a quote, which would extend the delimiter`,
    );
  }
  // A newline directly after the opening delimiter is trimmed by TOML, so the body round-trips.
  return `'''\n${body}'''`;
}

function renderCodexAgent(sourceFile, raw) {
  const { data, body } = splitFrontmatter(raw, sourceFile);

  for (const field of ["name", "description"]) {
    if (!data.get(field)) {
      throw new Error(`${sourceFile}: frontmatter is missing '${field}'`);
    }
  }
  if (!body) {
    throw new Error(`${sourceFile}: instruction body is empty`);
  }

  // `model` and `tools` are deliberately dropped: they name Claude Code's model ids and tool
  // registry, neither of which transfers to another harness.
  return [
    `# Generated from ${sourceFile} by scripts/sync-agent-mirrors.mjs — do not edit by hand.`,
    `name = ${tomlBasicString(data.get("name"))}`,
    `description = ${tomlBasicString(data.get("description"))}`,
    `developer_instructions = ${tomlLiteralMultiline(body, sourceFile, "developer_instructions")}`,
    "",
  ].join("\n");
}

const skillBannerPattern = /^# GENERATED — do not edit\.[^\n]*\n/;

// Injects a YAML-comment banner as line 2, directly after the frontmatter's opening '---', so the
// frontmatter block still parses as valid YAML/whatever the skill loader expects.
export function injectSkillBanner(sourceFile, raw) {
  const normalized = normalizeNewlines(raw);
  const match = /^---\n/.exec(normalized);
  if (!match) {
    throw new Error(`${sourceFile}: expected SKILL.md to open with '---\\n'`);
  }
  const banner = `# GENERATED — do not edit. Source: ${sourceFile} — regenerate with 'node scripts/sync-agent-mirrors.mjs'.\n`;
  return (
    normalized.slice(0, match[0].length) +
    banner +
    normalized.slice(match[0].length)
  );
}

// Strips a previously-injected banner, e.g. when adopting a generated file as the new authored
// source. No-op if the banner isn't present.
export function stripSkillBanner(raw) {
  const normalized = normalizeNewlines(raw);
  const match = /^---\n/.exec(normalized);
  if (!match) return normalized;
  const rest = normalized.slice(match[0].length);
  const bannerMatch = skillBannerPattern.exec(rest);
  return bannerMatch
    ? normalized.slice(0, match[0].length) + rest.slice(bannerMatch[0].length)
    : normalized;
}

// Recursively lists files under `dir` (relative to `root`), returning POSIX-style relative paths.
// Uses withFileTypes + isDirectory()/isFile() explicitly — a stray symlink (e.g. left over from a
// prior installer run) is neither, and is skipped rather than silently treated as either. This is
// the fix for the failure mode where a directory-walking generator filters on entry.isDirectory()
// and a symlinked skill directory reports as neither a directory nor a file, vanishing from the
// source listing entirely.
function listFilesRecursive(root, dir) {
  const absolute = path.join(root, dir);
  if (!existsSync(absolute)) return [];
  const results = [];
  for (const entry of readdirSync(absolute, { withFileTypes: true })) {
    const relPath = `${dir}/${entry.name}`;
    if (entry.isDirectory()) {
      results.push(...listFilesRecursive(root, relPath));
    } else if (entry.isFile()) {
      results.push(relPath);
    }
  }
  return results;
}

// Skills are mirrored as a whole directory tree, not a single file: references, scripts/*.sh, and
// per-harness agents/*.yaml all need to be drift-controlled, not just SKILL.md. SKILL.md gets the
// generated-banner treatment (text, LF-normalized); everything else is copied as raw bytes so a
// CRLF/encoding quirk in a non-markdown asset can never silently diverge from its source.
function buildSkillMirrors(root) {
  const mirrors = new Map();
  for (const sourceRel of listFilesRecursive(root, skillSourceDir)) {
    const targetRel = skillTargetDir + sourceRel.slice(skillSourceDir.length);
    const absolute = path.join(root, sourceRel);
    if (path.basename(sourceRel) === "SKILL.md") {
      mirrors.set(
        targetRel,
        injectSkillBanner(sourceRel, readFileSync(absolute, "utf8")),
      );
    } else {
      mirrors.set(targetRel, readFileSync(absolute));
    }
  }
  return mirrors;
}

export function buildMirrors(root = defaultRoot) {
  const mirrors = new Map();

  const sourceDir = path.join(root, agentSourceDir);
  const agentFiles = readdirSync(sourceDir)
    .filter((file) => file.endsWith(".md"))
    .sort();

  for (const file of agentFiles) {
    const sourceFile = `${agentSourceDir}/${file}`;
    const raw = readFileSync(path.join(sourceDir, file), "utf8");
    const target = `${codexAgentDir}/${file.replace(/\.md$/, ".toml")}`;
    mirrors.set(target, renderCodexAgent(sourceFile, raw));
  }

  for (const [target, content] of buildSkillMirrors(root)) {
    mirrors.set(target, content);
  }

  return mirrors;
}

// Generated files with no surviving source — an agent or skill file that was renamed or deleted
// upstream. Walked recursively so a whole removed skill directory is caught, not just top-level
// entries.
function findOrphans(root, mirrors) {
  const orphans = [];
  for (const dir of [codexAgentDir, skillTargetDir]) {
    for (const target of listFilesRecursive(root, dir)) {
      if (!mirrors.has(target)) orphans.push(target);
    }
  }
  return orphans.sort();
}

export function syncMirrors(root = defaultRoot, { check = false } = {}) {
  const mirrors = buildMirrors(root);
  const stale = [];

  for (const [target, content] of mirrors) {
    const absolute = path.join(root, target);
    const isBinary = Buffer.isBuffer(content);
    const current = existsSync(absolute)
      ? isBinary
        ? readFileSync(absolute)
        : normalizeNewlines(readFileSync(absolute, "utf8"))
      : null;

    const matches =
      current !== null &&
      (isBinary ? current.equals(content) : current === content);
    if (matches) continue;

    if (check) {
      stale.push(current === null ? `${target} (missing)` : target);
      continue;
    }

    mkdirSync(path.dirname(absolute), { recursive: true });
    writeFileSync(absolute, content);
  }

  const orphans = findOrphans(root, mirrors);
  if (check) {
    stale.push(...orphans.map((target) => `${target} (orphaned)`));
  } else {
    for (const target of orphans) {
      rmSync(path.join(root, target), { force: true });
    }
  }

  return {
    stale,
    count: mirrors.size,
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const check = process.argv.includes("--check");

  try {
    const { stale, count } = syncMirrors(defaultRoot, { check });

    if (!check) {
      console.log(
        `Synced ${count} harness mirror(s) (.codex/agents from .claude/agents, .claude/skills from .agents/skills).`,
      );
    } else if (stale.length > 0) {
      console.error("Harness mirrors are out of date:");
      for (const file of stale) console.error(`  ${file}`);
      console.error(
        "\n.codex/agents is generated from .claude/agents; .claude/skills is generated from " +
          ".agents/skills. Run 'node scripts/sync-agent-mirrors.mjs' and commit the result.",
      );
      process.exit(1);
    } else {
      console.log(
        `All ${count} harness mirror(s) match their authored sources (.claude/agents, .agents/skills).`,
      );
    }
  } catch (error) {
    console.error(`sync-agent-mirrors: ${error.message}`);
    process.exit(1);
  }
}
