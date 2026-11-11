#!/usr/bin/env node
// Verifies the installed skills against skills-lock.json and the shared install commands. An edit to
// an installed skill is undone by the next install, and a formatter's edit looks like nothing at all,
// so CI fails on both instead of letting a repository drift from the others.
//
//   node scripts/check-skills.mjs          # exit 1 and list every problem found
//   node scripts/check-skills.mjs <root>   # check another checkout instead
//
// The expected set comes from the `npx skills add` commands in the `shared-skills` block, which `ship`
// installs. A source those commands name holds exactly the skills they list, at the ref they pin, so
// moving every repository to a new snapshot is one change there. A skill from any other source only
// has to match its lock entry, and a folder with no lock entry is the repository's own.
//
// A lock's `computedHash` is computed one of two ways, by the lock's `version`:
//
// - Version 2 (CLI 1.8.0 on) follows the CLI's published rule ("Lock File Content Hash" in its
//   README): CRLF becomes LF in a text file, paths sort by their UTF-8 bytes, and each file feeds
//   path, NUL, length, NUL, content. The same skill hashes the same on every machine.
// - Version 1 (CLI 1.7.x) covers each file's bytes as the CLI read them, and `git` gives a Windows
//   clone CRLF, so a hash is accepted when the folder matches it as it stands, with LF, or with CRLF.
//
// The CLI upgrades a version 1 lock on its next write, and gives an empty hash to each entry whose
// folder no longer matched its old one; that entry fails here until the install is re-run.
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { createHash } from "node:crypto";
import {
  existsSync,
  readdirSync,
  readFileSync,
  realpathSync,
  statSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

const LOCK = "skills-lock.json";
const SKILLS = ".agents/skills";
const BLOCKS = ".agents/skills/ship/settings/agents-md.md";

// Every `npx skills add` command in the block, as { source, ref, names }. A trailing backslash
// continues a command on the next line.
export function parseInstallCommands(text) {
  const block =
    /<!-- shared:shared-skills -->\n([\s\S]*?)\n<!-- \/shared:shared-skills -->/.exec(
      text.replaceAll("\r\n", "\n"),
    );
  if (!block) throw new Error(`${BLOCKS}: no shared-skills block`);
  const commands = block[1]
    .replaceAll("\\\n", " ")
    .split("\n")
    .filter((line) => line.startsWith("npx skills add "))
    .map((line) => {
      const [spec] = line.slice("npx skills add ".length).trim().split(/\s+/);
      const [source, ref = null] = spec.split("#");
      const names = [...line.matchAll(/(?:^|\s)-s\s+(\S+)/g)].map(
        (match) => match[1],
      );
      return { source, ref, names };
    });
  if (commands.length === 0)
    throw new Error(`${BLOCKS}: no install command in the shared-skills block`);
  return commands;
}

// Version 1: every file but those under .git and node_modules, symlinks skipped.
function collectFiles(baseDir, currentDir, results) {
  for (const entry of readdirSync(currentDir, { withFileTypes: true })) {
    const fullPath = path.join(currentDir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name !== ".git" && entry.name !== "node_modules") {
        collectFiles(baseDir, fullPath, results);
      }
    } else if (entry.isFile()) {
      results.push({
        relativePath: path
          .relative(baseDir, fullPath)
          .split(path.sep)
          .join("/"),
        content: readFileSync(fullPath),
      });
    }
  }
  return results;
}

// Version 1: the CLI's computeSkillFolderHash, with each file's bytes passed through `transform`.
// Its order is `localeCompare`, so names are compared the same way, in a fixed locale.
function folderHash(files, transform) {
  const hash = createHash("sha256");
  for (const file of files.toSorted((a, b) =>
    a.relativePath.localeCompare(b.relativePath, "en"),
  )) {
    hash.update(file.relativePath);
    hash.update(transform(file.content));
  }
  return hash.digest("hex");
}

const asIs = (content) => content;
const withLf = (content) =>
  Buffer.from(content.toString("latin1").replaceAll("\r\n", "\n"), "latin1");
const withCrlf = (content) =>
  Buffer.from(
    withLf(content).toString("latin1").replaceAll("\n", "\r\n"),
    "latin1",
  );

// Version 2: every file but `metadata.json` and those under .git, __pycache__ and __pypackages__.
// A symlink counts as what it points to, unless that is broken, outside the folder, or a loop.
const EXCLUDED_FILES = new Set(["metadata.json"]);
const EXCLUDED_DIRS = new Set([".git", "__pycache__", "__pypackages__"]);

function collectFilesV2(root, dir, prefix, ancestors, results) {
  const realDir = realpathSync(dir);
  if (ancestors.has(realDir)) return results;
  const branch = new Set(ancestors).add(realDir);
  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    if (EXCLUDED_FILES.has(entry.name)) continue;
    const fullPath = path.join(dir, entry.name);
    let isDirectory = entry.isDirectory();
    if (entry.isSymbolicLink()) {
      let target;
      try {
        target = realpathSync(fullPath);
      } catch {
        continue;
      }
      if (target !== root && !target.startsWith(root + path.sep)) continue;
      isDirectory = statSync(target).isDirectory();
    } else if (!isDirectory && !entry.isFile()) continue;
    if (isDirectory) {
      if (!EXCLUDED_DIRS.has(entry.name))
        collectFilesV2(
          root,
          fullPath,
          `${prefix}${entry.name}/`,
          branch,
          results,
        );
    } else {
      results.push({
        path: Buffer.from(prefix + entry.name, "utf8"),
        content: readFileSync(fullPath),
      });
    }
  }
  return results;
}

// The CLI's version 2 hashSkillFiles. A file with a NUL byte is binary and keeps its CRLF.
export function folderHashV2(folder) {
  const root = realpathSync(folder);
  const files = collectFilesV2(root, folder, "", new Set(), []).map(
    ({ path: name, content }) => ({
      path: name,
      content: content.includes(0) ? content : withLf(content),
    }),
  );
  files.sort((a, b) => Buffer.compare(a.path, b.path));
  const hash = createHash("sha256");
  for (const file of files) {
    hash.update(file.path);
    hash.update("\0");
    hash.update(String(file.content.length));
    hash.update("\0");
    hash.update(file.content);
  }
  return hash.digest("hex");
}

export function matchesLock(folder, computedHash, version = 1) {
  if (version === 2) return folderHashV2(folder) === computedHash;
  const files = collectFiles(folder, folder, []);
  return [asIs, withLf, withCrlf].some(
    (transform) => folderHash(files, transform) === computedHash,
  );
}

export function checkSkills(root = defaultRoot) {
  const problems = [];
  const lockPath = path.join(root, LOCK);
  const blocksPath = path.join(root, BLOCKS);
  if (!existsSync(lockPath)) return [`${LOCK}: not found`];
  if (!existsSync(blocksPath))
    return [`${BLOCKS}: not found; install the ship skill`];

  const lock = JSON.parse(readFileSync(lockPath, "utf8"));
  const version = lock.version ?? 1;
  if (version !== 1 && version !== 2)
    return [
      `${LOCK}: version ${version}, which this check-skills.mjs cannot read; take the latest from jwh3times/skills`,
    ];
  const locked = lock.skills ?? {};
  const commands = parseInstallCommands(readFileSync(blocksPath, "utf8"));

  for (const { source, ref, names } of commands) {
    for (const name of names) {
      const entry = locked[name];
      if (!entry) {
        problems.push(
          `${name}: not in ${LOCK}, but the shared install commands name it`,
        );
      } else if (entry.source !== source) {
        problems.push(
          `${name}: locked from ${entry.source}, expected ${source}`,
        );
      } else if ((entry.ref ?? null) !== ref) {
        problems.push(
          `${name}: locked at ${entry.ref ?? "no ref"}, expected ${ref ?? "no ref"}`,
        );
      }
    }
    for (const [name, entry] of Object.entries(locked)) {
      if (entry.source === source && !names.includes(name)) {
        problems.push(
          `${name}: locked from ${source}, but the shared install commands do not name it`,
        );
      }
    }
  }

  for (const [name, entry] of Object.entries(locked)) {
    const folder = path.join(root, SKILLS, name);
    if (!existsSync(folder)) {
      problems.push(`${name}: in ${LOCK}, but ${SKILLS}/${name} is missing`);
    } else if (!entry.computedHash) {
      problems.push(
        `${name}: no hash in ${LOCK}; the CLI could not carry it over from a version 1 lock`,
      );
    } else if (!matchesLock(folder, entry.computedHash, version)) {
      problems.push(`${name}: ${SKILLS}/${name} differs from ${LOCK}`);
    }
  }
  return problems;
}

function main(argv) {
  const root = argv[0] ? path.resolve(argv[0]) : defaultRoot;
  const problems = checkSkills(root);
  if (problems.length === 0) {
    console.log(
      `Installed skills in ${root} match ${LOCK} and the shared install commands.`,
    );
    return 0;
  }
  console.error("Installed skills do not match:");
  for (const problem of problems) console.error(`  ${problem}`);
  console.error(
    `\nAn installed skill is never edited: what it needs to know about this repository goes in\n` +
      `AGENTS.md or docs/agents/. Re-run the install commands in the shared-skills block of AGENTS.md,\n` +
      `then 'node scripts/sync-agents.mjs', and keep .agents/skills/ out of every formatter.`,
  );
  return 1;
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`check-skills: ${error.message}`);
    process.exitCode = 1;
  }
}
