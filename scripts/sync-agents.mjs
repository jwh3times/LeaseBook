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
// It also writes the shared blocks in AGENTS.md: the text between each pair of `shared:<name>`
// markers comes from the block file `open-pr` installs, so every repository says the same thing.
// Where `scripts/guard.mjs` exists, it registers it as a PreToolUse and a PostToolUse hook in
// `.claude/settings.json`, keeping every other key and hook, and checks `docs/agents/guard.md`.
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
import { fileURLToPath, pathToFileURL } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");
const regenerateCommand = "node scripts/sync-agents.mjs";

const agentSourceDir = ".claude/agents";
const agentTargetDir = ".codex/agents";
const skillSourceDir = ".agents/skills";
const skillTargetDir = ".claude/skills";
const blockSource = ".agents/skills/open-pr/settings/agents-md.md";
const blockTarget = "AGENTS.md";
const blockSetting = "docs/agents/workspace.md";
const blockSettingHeading = "## AGENTS.md blocks";
const settingsTarget = ".claude/settings.json";
const guardScript = "scripts/guard.mjs";
const guardSettings = "docs/agents/guard.md";

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

// --- AGENTS.md blocks ----------------------------------------------------------------------------

function findBlocks(text) {
  const pattern = /^<!-- shared:([a-z-]+) -->\n(?:([\s\S]*?)\n)?<!-- \/shared:\1 -->$/gm;
  return [...text.matchAll(pattern)].map(([, name, body = ""]) => ({ name, body }));
}

// The `AGENTS.md blocks` setting: every block when the heading is missing, none for `none`, or the
// backticked names written under it.
export function takenBlocks(settings, available) {
  const lines = normalizeNewlines(settings).split("\n");
  const start = lines.indexOf(blockSettingHeading);
  if (start === -1) return available;
  const rest = lines.slice(start + 1);
  const end = rest.findIndex((line) => line.startsWith("#"));
  const value = (end === -1 ? rest : rest.slice(0, end)).join("\n").trim();
  if (value === "none") return [];
  const names = [...value.matchAll(/`([a-z-]+)`/g)].map((match) => match[1]);
  const unknown = names.filter((name) => !available.includes(name));
  if (names.length === 0 || unknown.length > 0) {
    throw new Error(
      `${blockSetting}: '${blockSettingHeading.slice(3)}' must be 'none' or block names from ${blockSource}; got ${JSON.stringify(value)}`,
    );
  }
  return names;
}

// AGENTS.md with each taken block's text written between its markers. Everything outside the
// markers is the repository's own and stays as it is.
export function renderSharedBlocks(raw, source, taken) {
  let text = normalizeNewlines(raw);
  const present = findBlocks(text).map((block) => block.name);
  for (const name of present) {
    if (!taken.includes(name)) {
      throw new Error(
        `${blockTarget}: block '${name}' is not one this repository takes; remove its markers or see '${blockSettingHeading.slice(3)}' in ${blockSetting}`,
      );
    }
  }
  for (const name of taken) {
    const count = present.filter((other) => other === name).length;
    if (count !== 1) {
      throw new Error(
        `${blockTarget}: expected the markers of shared block '${name}' once, found ${count}; add <!-- shared:${name} --> and <!-- /shared:${name} --> on their own lines`,
      );
    }
    const body = source.find((block) => block.name === name).body;
    text = text.replace(
      new RegExp(`^<!-- shared:${name} -->\\n(?:[\\s\\S]*?\\n)?<!-- /shared:${name} -->$`, "m"),
      () => `<!-- shared:${name} -->\n${body}\n<!-- /shared:${name} -->`,
    );
  }
  return text;
}

function buildAgentsFile(root) {
  const sourcePath = path.join(root, blockSource);
  if (!existsSync(sourcePath)) return null;
  const source = findBlocks(normalizeNewlines(readFileSync(sourcePath, "utf8")));
  const settingsPath = path.join(root, blockSetting);
  const settings = existsSync(settingsPath) ? readFileSync(settingsPath, "utf8") : "";
  const taken = takenBlocks(settings, source.map((block) => block.name));
  if (taken.length === 0) return null;
  const targetPath = path.join(root, blockTarget);
  if (!existsSync(targetPath)) throw new Error(`${blockTarget}: not found`);
  return renderSharedBlocks(readFileSync(targetPath, "utf8"), source, taken);
}

// --- Claude Code settings ------------------------------------------------------------------------

// Exec form: no shell, so no quoting, and Claude Code substitutes the placeholder in each argument.
// `onFailure` stays unset, so a guard that cannot start blocks nothing.
function guardHook(extra = []) {
  return {
    type: "command",
    command: "node",
    args: [`\${CLAUDE_PROJECT_DIR}/${guardScript}`, ...extra],
    timeout: 10,
  };
}

export const managedHooks = {
  PreToolUse: {
    matcher: "Bash|PowerShell|Edit|Write|MultiEdit|NotebookEdit",
    hooks: [guardHook()],
  },
  PostToolUse: { matcher: "Write|Edit|MultiEdit", hooks: [guardHook(["--post"])] },
};

const isObject = (value) =>
  value !== null && typeof value === "object" && !Array.isArray(value);

// A hook is managed here when its command or arguments name the guard script.
function namesGuard(hook) {
  const parts = [hook?.command, ...(Array.isArray(hook?.args) ? hook.args : [])];
  return parts.some(
    (part) =>
      typeof part === "string" &&
      part.replaceAll("\\", "/").includes(guardScript),
  );
}

// JSON in the shape Prettier and Biome print it, so a consumer's formatter check passes on a file
// this script wrote: an object one key per line (both keep an expanded object expanded), an array
// of primitives on one line when that whole line, comma included, fits `width`, otherwise one
// element per line, an array holding an object or array expanded, `{}` and `[]` inline, a 2-space
// indent and a final newline.
export function formatJson(value, width = 80) {
  const pad = (n) => " ".repeat(n);
  const isPrimitive = (item) => item === null || typeof item !== "object";
  // `lead` is the text before the value on its line, `tail` what follows it there (a comma).
  const print = (item, indent, lead, tail) => {
    if (isPrimitive(item)) return JSON.stringify(item);
    if (Array.isArray(item)) {
      if (item.length === 0) return "[]";
      if (item.every(isPrimitive)) {
        const inline = `[${item.map((element) => JSON.stringify(element)).join(", ")}]`;
        if (lead + inline.length + tail <= width) return inline;
      }
      const lines = item.map((element, i) => {
        const last = i === item.length - 1;
        return `${pad(indent + 2)}${print(element, indent + 2, indent + 2, last ? 0 : 1)}`;
      });
      return `[\n${lines.join(",\n")}\n${pad(indent)}]`;
    }
    const entries = Object.entries(item);
    if (entries.length === 0) return "{}";
    const lines = entries.map(([key, element], i) => {
      const head = `${pad(indent + 2)}${JSON.stringify(key)}: `;
      return head + print(element, indent + 2, head.length, i === entries.length - 1 ? 0 : 1);
    });
    return `{\n${lines.join(",\n")}\n${pad(indent)}}`;
  };
  return `${print(value, 0, 0, 0)}\n`;
}

// The print width the repository's formatter uses for JSON: Prettier's `printWidth` (from
// `.prettierrc`, JSON or YAML, `.prettierrc.json` or `package.json`), else Biome's
// `json.formatter.lineWidth` or `formatter.lineWidth`, else Prettier's and Biome's default of 80.
export function formatWidth(root = defaultRoot) {
  const readText = (rel) => {
    const file = path.join(root, rel);
    return existsSync(file) ? readFileSync(file, "utf8") : null;
  };
  const parse = (text) => {
    try {
      return JSON.parse(text);
    } catch {
      return null;
    }
  };
  const positive = (value) => (Number.isInteger(value) && value > 0 ? value : null);
  for (const rel of [".prettierrc", ".prettierrc.json", ".prettierrc.yaml", ".prettierrc.yml"]) {
    const text = readText(rel);
    if (text === null) continue;
    const width = positive(parse(text)?.printWidth) ?? positive(Number(/^[ \t]*"?printWidth"?[ \t]*:[ \t]*(\d+)/m.exec(text)?.[1]));
    if (width) return width;
  }
  const packageText = readText("package.json");
  const fromPackage = packageText === null ? null : positive(parse(packageText)?.prettier?.printWidth);
  if (fromPackage) return fromPackage;
  for (const rel of ["biome.json", "biome.jsonc"]) {
    const text = readText(rel);
    if (text === null) continue;
    // A `.jsonc` may hold comments, which JSON.parse refuses; full-line comments are dropped first.
    const config = parse(text) ?? parse(text.replace(/^[ \t]*\/\/.*$/gm, ""));
    const width = positive(config?.json?.formatter?.lineWidth) ?? positive(config?.formatter?.lineWidth);
    if (width) return width;
  }
  return 80;
}

// `.claude/settings.json` with the managed entries in place: a stale one is replaced where it
// stands, duplicates collapse into it, a missing one is appended, and the file is written as the
// repository's formatter prints it (`formatJson`). When nothing changes, the text comes back as it
// was, so a file formatted its own way is never rewritten.
export function mergeClaudeSettings(raw, file = settingsTarget, { width = 80 } = {}) {
  const text = raw === null ? null : normalizeNewlines(raw);
  let settings;
  try {
    settings = text === null ? {} : JSON.parse(text);
  } catch (error) {
    throw new Error(`${file}: invalid JSON (${error.message})`, { cause: error });
  }
  if (!isObject(settings)) throw new Error(`${file}: expected a JSON object`);
  const before = JSON.stringify(settings);
  settings.hooks ??= {};
  if (!isObject(settings.hooks)) {
    throw new Error(`${file}: 'hooks' must be an object`);
  }
  for (const [event, managed] of Object.entries(managedHooks)) {
    const entries = settings.hooks[event] ?? [];
    if (!Array.isArray(entries)) {
      throw new Error(`${file}: 'hooks.${event}' must be an array`);
    }
    const merged = [];
    let placed = false;
    for (const entry of entries) {
      const hooks = Array.isArray(entry?.hooks) ? entry.hooks : [];
      if (!hooks.some(namesGuard)) {
        merged.push(entry);
        continue;
      }
      if (!placed) merged.push(managed);
      placed = true;
      const others = hooks.filter((hook) => !namesGuard(hook));
      if (others.length > 0) merged.push({ ...entry, hooks: others });
    }
    if (!placed) merged.push(managed);
    settings.hooks[event] = merged;
  }
  if (text !== null && JSON.stringify(settings) === before) return text;
  return formatJson(settings, width);
}

function buildClaudeSettings(root) {
  if (!existsSync(path.join(root, guardScript))) return null;
  const target = path.join(root, settingsTarget);
  return mergeClaudeSettings(
    existsSync(target) ? readFileSync(target, "utf8") : null,
    settingsTarget,
    { width: formatWidth(root) },
  );
}

// Problems in `docs/agents/guard.md`, read by the repository's own guard's parser.
export async function checkGuardSettings(root = defaultRoot) {
  const script = path.join(root, guardScript);
  const settings = path.join(root, guardSettings);
  if (!existsSync(script) || !existsSync(settings)) return [];
  const { parseGuardSettings } = await import(pathToFileURL(script).href);
  try {
    parseGuardSettings(readFileSync(settings, "utf8"), guardSettings);
    return [];
  } catch (error) {
    return [error.message];
  }
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

  const agentsFile = buildAgentsFile(root);
  if (agentsFile !== null) mirrors.set(blockTarget, agentsFile);

  const claudeSettings = buildClaudeSettings(root);
  if (claudeSettings !== null) mirrors.set(settingsTarget, claudeSettings);

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

  // Links go before anything is written: `npx skills update` links .claude/skills/<name> to
  // .agents/skills/<name>, and a write through that link would land in the authored source.
  if (!check) {
    for (const dir of [agentTargetDir, skillTargetDir]) {
      const strays = [];
      listFiles(root, dir, { strays });
      for (const stray of strays) {
        rmSync(path.join(root, stray), { recursive: true, force: true });
      }
    }
  }

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

async function main(argv) {
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
  const guardProblems = await checkGuardSettings(defaultRoot);
  for (const problem of guardProblems) console.error(`sync-agents: ${problem}`);
  const guardStatus = guardProblems.length === 0 ? 0 : 1;

  if (!check) {
    console.log(
      `Synced ${count} generated file(s) (.codex/agents, .claude/skills, ${blockTarget} blocks, ${settingsTarget} hooks).`,
    );
    return guardStatus;
  }
  if (stale.length === 0) {
    console.log(`All ${count} generated file(s) match their authored sources.`);
    return guardStatus;
  }

  console.error("Generated agent files are out of date:");
  for (const file of stale) console.error(`  ${file}`);
  console.error(
    `\n.codex/agents is generated from .claude/agents; .claude/skills from .agents/skills;\n` +
      `the marked blocks in ${blockTarget} from ${blockSource};\n` +
      `the guard hooks in ${settingsTarget} from ${guardScript}.\n` +
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
    process.exitCode = await main(process.argv.slice(2));
  } catch (error) {
    console.error(`sync-agents: ${error.message}`);
    process.exitCode = 1;
  }
}
