// Generates the non-Claude harness copies of the agent and skill instructions.
//
// `.claude/` is the single source of truth. Codex reads TOML agent definitions from
// `.codex/agents/`, and the cross-harness `.agents/` tree carries the portable skills, but
// neither is hand-maintained: both are derived here so the instructions cannot drift apart.
// CI runs this with `--check`, so a change to `.claude/` that skips the regen fails the build.
//
//   node scripts/sync-agent-mirrors.mjs          # rewrite the mirrors
//   node scripts/sync-agent-mirrors.mjs --check  # verify only; exit 1 if stale

import {
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

const agentSourceDir = ".claude/agents";
const codexAgentDir = ".codex/agents";

// Skills are copied byte-for-byte: the target harness parses the same frontmatter + markdown
// format Claude Code does, so there is nothing to transform.
const skillMirrors = [
  [".claude/skills/ship/SKILL.md", ".agents/skills/ship/SKILL.md"],
];

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

  for (const [source, target] of skillMirrors) {
    const absolute = path.join(root, source);
    if (!existsSync(absolute)) {
      throw new Error(`${source}: skill mirror source is missing`);
    }
    mirrors.set(target, normalizeNewlines(readFileSync(absolute, "utf8")));
  }

  return mirrors;
}

// Mirror files with no surviving source — an agent that was renamed or deleted upstream.
function findOrphans(root, mirrors) {
  const orphans = [];
  for (const dir of [codexAgentDir, path.dirname(skillMirrors[0][1])]) {
    const absolute = path.join(root, dir);
    if (!existsSync(absolute)) continue;
    for (const entry of readdirSync(absolute)) {
      const target = `${dir}/${entry}`;
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
    const current = existsSync(absolute)
      ? normalizeNewlines(readFileSync(absolute, "utf8"))
      : null;

    if (current === content) continue;

    if (check) {
      stale.push(current === null ? `${target} (missing)` : target);
      continue;
    }

    mkdirSync(path.dirname(absolute), { recursive: true });
    writeFileSync(absolute, content, "utf8");
  }

  return {
    stale: [...stale, ...findOrphans(root, mirrors)],
    count: mirrors.size,
  };
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const check = process.argv.includes("--check");

  try {
    const { stale, count } = syncMirrors(defaultRoot, { check });

    if (!check) {
      console.log(`Synced ${count} harness mirror(s) from .claude/.`);
    } else if (stale.length > 0) {
      console.error("Harness mirrors are out of date:");
      for (const file of stale) console.error(`  ${file}`);
      console.error(
        "\nThese files are generated from .claude/. Run 'node scripts/sync-agent-mirrors.mjs' and commit the result.",
      );
      process.exit(1);
    } else {
      console.log(`All ${count} harness mirror(s) match .claude/.`);
    }
  } catch (error) {
    console.error(`sync-agent-mirrors: ${error.message}`);
    process.exit(1);
  }
}
