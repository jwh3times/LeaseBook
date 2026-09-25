#!/usr/bin/env node
// Keeps the Claude Code and Codex copies of the agent instructions from drifting. Two independent
// directions, because the two trees have different authored sources:
//
//   .claude/agents/*.md  (authored) --> .codex/agents/*.toml  (generated, for Codex)
//   .agents/skills/**    (authored) --> .claude/skills/**     (generated, for Claude Code)
//
// `.agents/skills/` is authored because Codex reads it directly and the skills installer writes
// there, so installing or updating a skill stays a one-way operation. Generated files are committed
// so a fresh clone works in both harnesses; CI runs this with `--check` and fails on drift.
//
//   node scripts/sync-agents.mjs          # rewrite the generated trees
//   node scripts/sync-agents.mjs --check  # verify only; exit 1 if anything is stale
//   node scripts/sync-agents.mjs --hook   # Claude Code PostToolUse hook: sync only when an
//                                         # authored file was just edited (payload on stdin)
//
// This file is shared verbatim across repositories. Change it in one, copy it to all.

import {
  existsSync,
  lstatSync,
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
const regenerateCommand = "node scripts/sync-agents.mjs";

const agentSourceDir = ".claude/agents";
const agentTargetDir = ".codex/agents";
const skillSourceDir = ".agents/skills";
const skillTargetDir = ".claude/skills";

// Claude Code tools that can change files. An agent whose `tools:` list names none of them is
// read-only, and Codex is told so; an agent with no `tools:` line inherits every tool.
const writingTools = new Set(["Write", "Edit", "MultiEdit", "NotebookEdit"]);

function normalizeNewlines(value) {
  return value.replaceAll("\r\n", "\n");
}

// Code-point order, independent of locale, so every machine lists files identically.
function byCodePoint(a, b) {
  if (a < b) return -1;
  return a > b ? 1 : 0;
}

// C0 controls and DEL, minus the characters a caller allows. Checked by code point rather than
// with a regex, so linters' no-control-regex rule has nothing to object to.
function isControl(code, allowed = "") {
  return (
    (code < 0x20 || code === 0x7f) &&
    !allowed.includes(String.fromCharCode(code))
  );
}

function hasControl(text, allowed = "") {
  for (let i = 0; i < text.length; i += 1) {
    if (isControl(text.charCodeAt(i), allowed)) return true;
  }
  return false;
}

// --- Frontmatter ---------------------------------------------------------------------------------

function unquote(raw, file, key) {
  const value = raw.trim();
  if (value.startsWith('"')) {
    try {
      return JSON.parse(value);
    } catch {
      throw new Error(
        `${file}: '${key}' has an unterminated or invalid double-quoted value`,
      );
    }
  }
  if (value.startsWith("'")) {
    if (!value.endsWith("'") || value.length < 2) {
      throw new Error(
        `${file}: '${key}' has an unterminated single-quoted value`,
      );
    }
    return value.slice(1, -1).replaceAll("''", "'");
  }
  return value;
}

// A deliberately small YAML subset: `key: value` scalars (plain, single- or double-quoted), plain
// scalars continued on indented lines, and `- item` lists. Anything else is an error rather than a
// silent misread, because a misread description or tool list ships straight to another harness.
export function splitFrontmatter(raw, file) {
  const normalized = normalizeNewlines(raw);
  const match = /^---\n([\s\S]*?)\n---(?:\n|$)/.exec(normalized);
  if (!match) {
    throw new Error(`${file}: expected YAML frontmatter delimited by '---'`);
  }

  const data = new Map();
  let lastKey = null;
  for (const line of match[1].split("\n")) {
    if (!line.trim() || line.trimStart().startsWith("#")) continue;

    const field = /^([A-Za-z][\w-]*):(?:\s+(.*))?$/.exec(line);
    if (field) {
      const [, key, rest = ""] = field;
      if (/^[|>][+-]?\d*$/.test(rest.trim())) {
        throw new Error(
          `${file}: '${key}' uses a block scalar; write it on one line`,
        );
      }
      data.set(key, rest.trim() === "" ? [] : unquote(rest, file, key));
      lastKey = key;
      continue;
    }

    const item = /^\s+-\s+(.*)$/.exec(line);
    if (item && lastKey && Array.isArray(data.get(lastKey))) {
      data.get(lastKey).push(unquote(item[1], file, lastKey));
      continue;
    }

    const continuation = /^\s+(\S.*)$/.exec(line);
    if (continuation && lastKey && typeof data.get(lastKey) === "string") {
      data.set(lastKey, `${data.get(lastKey)} ${continuation[1].trim()}`);
      continue;
    }

    throw new Error(
      `${file}: cannot parse frontmatter line: ${JSON.stringify(line)}`,
    );
  }

  for (const [key, value] of data) {
    const text = Array.isArray(value) ? value.join(",") : value;
    // Tab is fine; every other control character would corrupt a TOML string or a banner.
    if (hasControl(text, "\t")) {
      throw new Error(`${file}: '${key}' contains a control character`);
    }
  }

  return { data, body: normalized.slice(match[0].length).trim() };
}

function toolList(value) {
  if (value === undefined) return null;
  const items = Array.isArray(value)
    ? value
    : value.replace(/^\[|\]$/g, "").split(",");
  return items.map((tool) => tool.trim()).filter(Boolean);
}

// --- Codex agents --------------------------------------------------------------------------------

// TOML basic string. JSON's escape set is a subset of TOML's, so stringify is valid TOML.
function tomlBasicString(value) {
  return JSON.stringify(value);
}

// Instruction bodies are full of backslashes, quotes, and code fences, so they go in a TOML
// *literal* multi-line string, which performs no escape processing. The rare body a literal string
// cannot hold (one containing ''' or ending in a quote) falls back to an escaped basic string.
export function tomlMultiline(body) {
  if (!body.includes("'''") && !body.endsWith("'")) {
    return `'''\n${body}'''`;
  }
  let escaped = "";
  for (const char of body) {
    const code = char.charCodeAt(0);
    if (char === "\\" || char === '"') {
      escaped += `\\${char}`;
    } else if (isControl(code, "\t\n")) {
      escaped += `\\u${code.toString(16).padStart(4, "0")}`;
    } else {
      escaped += char;
    }
  }
  return `"""\n${escaped}"""`;
}

export function renderCodexAgent(sourceFile, raw) {
  const { data, body } = splitFrontmatter(raw, sourceFile);

  for (const field of ["name", "description"]) {
    if (typeof data.get(field) !== "string" || !data.get(field)) {
      throw new Error(`${sourceFile}: frontmatter is missing '${field}'`);
    }
  }
  const expected = path.basename(sourceFile, ".md");
  if (data.get("name") !== expected) {
    throw new Error(
      `${sourceFile}: frontmatter name '${data.get("name")}' must match the filename '${expected}'`,
    );
  }
  if (!body) {
    throw new Error(`${sourceFile}: instruction body is empty`);
  }

  // `model`, `color`, and the tool list itself are dropped: they name Claude Code's model ids and
  // tool registry, neither of which transfers. Only the read-only fact survives, as a sandbox.
  const tools = toolList(data.get("tools"));
  const readOnly =
    tools !== null && !tools.some((tool) => writingTools.has(tool));

  return [
    `# GENERATED — do not edit. Source: ${sourceFile} — regenerate with '${regenerateCommand}'.`,
    `name = ${tomlBasicString(data.get("name"))}`,
    `description = ${tomlBasicString(data.get("description"))}`,
    ...(readOnly ? ['sandbox_mode = "read-only"'] : []),
    `developer_instructions = ${tomlMultiline(body)}`,
    "",
  ].join("\n");
}

// --- Claude skills -------------------------------------------------------------------------------

const skillBannerPattern = /^# GENERATED — do not edit\.[^\n]*\n/;

// The banner is a YAML comment on line 2, directly after the opening '---', so the frontmatter
// still parses for the skill loader.
export function injectSkillBanner(sourceFile, raw) {
  const normalized = normalizeNewlines(raw);
  if (!normalized.startsWith("---\n")) {
    throw new Error(`${sourceFile}: expected SKILL.md to open with '---'`);
  }
  const banner = `# GENERATED — do not edit. Source: ${sourceFile} — regenerate with '${regenerateCommand}'.\n`;
  return `---\n${banner}${normalized.slice(4)}`;
}

// Removes an injected banner, e.g. when adopting a generated file as a new authored source.
export function stripSkillBanner(raw) {
  const normalized = normalizeNewlines(raw);
  if (!normalized.startsWith("---\n")) return normalized;
  return `---\n${normalized.slice(4).replace(skillBannerPattern, "")}`;
}

// --- Tree walking --------------------------------------------------------------------------------

// Lists regular files under `dir` as POSIX paths relative to `root`. Symlinks are never followed:
// `withFileTypes` reports a link (or Windows junction) as neither file nor directory. In an authored
// tree a link is an error — git stores it as a copy where `core.symlinks` is false. In a generated
// tree it is collected into `strays` so it can be reported and removed like any other orphan.
function listFiles(root, dir, { strays, authored = false } = {}) {
  const absolute = path.join(root, dir);
  if (!existsSync(absolute)) return [];
  if (lstatSync(absolute).isSymbolicLink()) {
    if (authored)
      throw new Error(`${dir}: authored tree must not be a symlink`);
    strays?.push(dir);
    return [];
  }

  const results = [];
  for (const entry of readdirSync(absolute, { withFileTypes: true })) {
    const relPath = `${dir}/${entry.name}`;
    if (entry.name === ".DS_Store") continue;
    if (entry.isDirectory()) {
      results.push(...listFiles(root, relPath, { strays, authored }));
    } else if (entry.isFile()) {
      results.push(relPath);
    } else if (authored) {
      throw new Error(
        `${relPath}: symlinks are not allowed in an authored tree; copy the files`,
      );
    } else {
      strays?.push(relPath);
    }
  }
  return results.toSorted(byCodePoint);
}

function pruneEmptyDirs(root, dir) {
  const absolute = path.join(root, dir);
  if (!existsSync(absolute) || !lstatSync(absolute).isDirectory()) return;
  for (const entry of readdirSync(absolute, { withFileTypes: true })) {
    if (!entry.isDirectory()) continue;
    const child = `${dir}/${entry.name}`;
    pruneEmptyDirs(root, child);
    if (readdirSync(path.join(root, child)).length === 0) {
      rmSync(path.join(root, child), { recursive: true });
    }
  }
}

// --- Sync ----------------------------------------------------------------------------------------

export function buildMirrors(root = defaultRoot) {
  const mirrors = new Map();

  for (const sourceRel of listFiles(root, agentSourceDir, { authored: true })) {
    if (!sourceRel.endsWith(".md")) continue;
    const targetRel =
      agentTargetDir +
      sourceRel.slice(agentSourceDir.length).replace(/\.md$/, ".toml");
    mirrors.set(
      targetRel,
      renderCodexAgent(
        sourceRel,
        readFileSync(path.join(root, sourceRel), "utf8"),
      ),
    );
  }

  // Whole skill directories are mirrored — references, scripts, and agents/*.yaml drift too.
  // SKILL.md gets the banner; every other file is copied as raw bytes so a binary asset or an
  // encoding quirk can never be rewritten.
  for (const sourceRel of listFiles(root, skillSourceDir, { authored: true })) {
    const targetRel = skillTargetDir + sourceRel.slice(skillSourceDir.length);
    const absolute = path.join(root, sourceRel);
    mirrors.set(
      targetRel,
      path.basename(sourceRel) === "SKILL.md"
        ? injectSkillBanner(sourceRel, readFileSync(absolute, "utf8"))
        : readFileSync(absolute),
    );
  }

  return mirrors;
}

// Text is compared with CRLF folded to LF, so a Windows checkout with core.autocrlf is not "stale".
// A buffer containing a NUL byte is treated as binary and compared exactly.
function sameContent(current, content) {
  if (typeof content === "string") {
    return normalizeNewlines(current.toString("utf8")) === content;
  }
  if (current.equals(content)) return true;
  if (content.includes(0) || current.includes(0)) return false;
  return (
    normalizeNewlines(current.toString("utf8")) ===
    normalizeNewlines(content.toString("utf8"))
  );
}

function findOrphans(root, mirrors) {
  const orphans = [];
  for (const dir of [agentTargetDir, skillTargetDir]) {
    const strays = [];
    for (const target of listFiles(root, dir, { strays })) {
      if (!mirrors.has(target)) orphans.push(target);
    }
    orphans.push(...strays);
  }
  return orphans.toSorted(byCodePoint);
}

export function syncMirrors(root = defaultRoot, { check = false } = {}) {
  const mirrors = buildMirrors(root);
  const stale = [];

  for (const [target, content] of mirrors) {
    const absolute = path.join(root, target);
    const exists = existsSync(absolute) && lstatSync(absolute).isFile();
    if (exists && sameContent(readFileSync(absolute), content)) continue;

    if (check) {
      stale.push(exists ? target : `${target} (missing)`);
      continue;
    }
    if (existsSync(absolute) && !exists)
      rmSync(absolute, { recursive: true, force: true });
    mkdirSync(path.dirname(absolute), { recursive: true });
    writeFileSync(absolute, content);
  }

  const orphans = findOrphans(root, mirrors);
  if (check) {
    stale.push(...orphans.map((target) => `${target} (orphaned)`));
  } else {
    // rmSync on a link removes the link itself, never the tree it points into.
    for (const target of orphans) {
      rmSync(path.join(root, target), { recursive: true, force: true });
    }
    for (const dir of [agentTargetDir, skillTargetDir])
      pruneEmptyDirs(root, dir);
  }

  return { stale, count: mirrors.size };
}

// True when a Claude Code PostToolUse payload names a file under an authored tree.
export function hookTouchesAuthoredTree(payload, root = defaultRoot) {
  const input = payload?.tool_input ?? {};
  const files = [
    input.file_path,
    input.notebook_path,
    ...(input.file_paths ?? []),
  ].filter((file) => typeof file === "string");
  return files.some((file) => {
    const rel = path
      .relative(root, path.resolve(root, file))
      .split(path.sep)
      .join("/");
    return [agentSourceDir, skillSourceDir].some((dir) =>
      rel.startsWith(`${dir}/`),
    );
  });
}

function readStdin() {
  try {
    return readFileSync(0, "utf8");
  } catch {
    return "";
  }
}

function main(argv) {
  const check = argv.includes("--check");

  if (argv.includes("--hook")) {
    let payload;
    try {
      payload = JSON.parse(readStdin() || "null");
    } catch {
      return 0;
    }
    if (!hookTouchesAuthoredTree(payload)) return 0;
    syncMirrors(defaultRoot);
    return 0;
  }

  const { stale, count } = syncMirrors(defaultRoot, { check });

  if (!check) {
    console.log(
      `Synced ${count} generated file(s) (.codex/agents, .claude/skills).`,
    );
    return 0;
  }
  if (stale.length === 0) {
    console.log(`All ${count} generated file(s) match their authored sources.`);
    return 0;
  }

  console.error("Generated agent files are out of date:");
  for (const file of stale) console.error(`  ${file}`);
  console.error(
    `\n.codex/agents is generated from .claude/agents; .claude/skills from .agents/skills.\n` +
      `Run '${regenerateCommand}' and commit the result.`,
  );
  if (process.env.GITHUB_ACTIONS === "true") {
    for (const file of stale) {
      const target = file.replace(/ \((missing|orphaned)\)$/, "");
      console.log(
        `::error file=${target}::Stale generated file. Run '${regenerateCommand}'.`,
      );
    }
  }
  return 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`sync-agents: ${error.message}`);
    process.exitCode = 1;
  }
}
