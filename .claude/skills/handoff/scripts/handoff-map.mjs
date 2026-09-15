// Reads and updates handoff_map.json in the Proton Drive Handoffs folder, which the handoff and
// lets-go skills share to pass a session between machines. Deterministic on purpose: the map is
// shared by every repo on both machines, so a hand-edited JSON slip breaks all of them.
//
//   node handoff-map.mjs get                        # this repo's active handoff (JSON)
//   node handoff-map.mjs publish <local-doc.md>     # copy doc into Handoffs, set it active
//   node handoff-map.mjs clear --expect <file.md>   # mark consumed (null) if still <file.md>
//
// The folder resolves from HANDOFFS_DIR, else "<Proton Drive root>/[<account>/]My files/Documents/
// Handoffs" under the home directory. The map key is the origin remote's repository name.

import { execFileSync } from "node:child_process";
import {
  copyFileSync,
  existsSync,
  readdirSync,
  readFileSync,
  statSync,
  writeFileSync,
} from "node:fs";
import os from "node:os";
import path from "node:path";

const mapFileName = "handoff_map.json";

function fail(message) {
  console.error(`handoff-map: ${message}`);
  process.exit(1);
}

function isDirectory(candidate) {
  return existsSync(candidate) && statSync(candidate).isDirectory();
}

// Proton Drive clients disagree on "My files" vs "My Files", so segments match case-insensitively.
// The Windows client exposes "My files" as a reparse point that Dirent reports as a symlink, so
// directory-ness is checked with stat (which follows it), never Dirent.isDirectory().
function childDirectory(parent, name) {
  if (!isDirectory(parent)) return null;
  const match = readdirSync(parent).find(
    (entry) =>
      entry.toLowerCase() === name.toLowerCase() &&
      isDirectory(path.join(parent, entry)),
  );
  return match ? path.join(parent, match) : null;
}

function handoffsUnder(root) {
  let current = root;
  for (const segment of ["My files", "Documents", "Handoffs"]) {
    current = childDirectory(current, segment);
    if (!current) return null;
  }
  return current;
}

function resolveHandoffsDir(env = process.env, home = os.homedir()) {
  if (env.HANDOFFS_DIR) {
    if (!isDirectory(env.HANDOFFS_DIR)) {
      fail(`HANDOFFS_DIR does not exist: ${env.HANDOFFS_DIR}`);
    }
    return env.HANDOFFS_DIR;
  }

  const roots = ["Proton Drive", "ProtonDrive", "proton-drive"]
    .map((name) => path.join(home, name))
    .filter(isDirectory);
  for (const root of roots) {
    const direct = handoffsUnder(root);
    if (direct) return direct;
    // The Windows client nests everything under an account-named folder.
    for (const entry of readdirSync(root)) {
      const nested = handoffsUnder(path.join(root, entry));
      if (nested) return nested;
    }
  }

  fail(
    `no Handoffs folder found under ${home} (tried Proton Drive, ProtonDrive, proton-drive). ` +
      "Set HANDOFFS_DIR to the folder holding handoff_map.json.",
  );
}

function repoKey() {
  const git = (...args) =>
    execFileSync("git", args, { encoding: "utf8" }).trim();
  try {
    const url = git("remote", "get-url", "origin");
    const name = url
      .replace(/\.git$/, "")
      .split(/[/:]/)
      .pop();
    if (name) return name;
  } catch {
    // No origin remote: fall back to the checkout folder name.
  }
  try {
    return path.basename(git("rev-parse", "--show-toplevel"));
  } catch {
    fail("not inside a git repository");
  }
}

function timestamp(date = new Date()) {
  const pad = (value) => String(value).padStart(2, "0");
  return (
    `${pad(date.getMonth() + 1)}-${pad(date.getDate())}-${date.getFullYear()} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

function readMap(dir) {
  const mapPath = path.join(dir, mapFileName);
  if (!existsSync(mapPath)) fail(`${mapPath} does not exist`);
  let map;
  try {
    map = JSON.parse(readFileSync(mapPath, "utf8").replace(/^\uFEFF/, ""));
  } catch (error) {
    fail(`${mapPath} is not valid JSON: ${error.message}`);
  }
  if (
    typeof map?.Active_Handoffs !== "object" ||
    map.Active_Handoffs === null
  ) {
    fail(`${mapPath} has no Active_Handoffs object`);
  }
  return { map, mapPath };
}

function writeMap(mapPath, map) {
  map.Last_Updated = timestamp();
  writeFileSync(mapPath, `${JSON.stringify(map, null, 2)}\n`);
}

// Keeps the map's existing casing for a key (e.g. "LeaseBook") when the remote spells it differently.
function mapKeyFor(map, key) {
  return (
    Object.keys(map.Active_Handoffs).find(
      (existing) => existing.toLowerCase() === key.toLowerCase(),
    ) ?? key
  );
}

function uniqueFileName(dir, fileName) {
  const { name, ext } = path.parse(fileName);
  let candidate = fileName;
  for (let n = 2; existsSync(path.join(dir, candidate)); n++) {
    candidate = `${name}-${n}${ext}`;
  }
  return candidate;
}

function main([command, ...args]) {
  const dir = resolveHandoffsDir();
  const { map, mapPath } = readMap(dir);
  const key = mapKeyFor(map, repoKey());
  const active = map.Active_Handoffs[key] ?? null;

  if (command === "get") {
    const docPath = active ? path.join(dir, active) : null;
    return {
      dir,
      key,
      file: active,
      path: docPath,
      exists: docPath ? existsSync(docPath) : false,
      lastUpdated: map.Last_Updated ?? null,
    };
  }

  if (command === "publish") {
    const source = args[0];
    if (!source || !existsSync(source))
      fail(`publish needs an existing doc path`);
    const file = uniqueFileName(dir, path.basename(source));
    copyFileSync(source, path.join(dir, file));
    map.Active_Handoffs[key] = file;
    writeMap(mapPath, map);
    return { dir, key, file, path: path.join(dir, file), previous: active };
  }

  if (command === "clear") {
    const expectIndex = args.indexOf("--expect");
    const expected = expectIndex >= 0 ? args[expectIndex + 1] : undefined;
    if (!expected)
      fail("clear needs --expect <file> naming the handoff you resumed");
    if (active !== expected) {
      fail(
        `refusing to clear ${key}: map names ${JSON.stringify(active)}, not ${JSON.stringify(expected)}`,
      );
    }
    map.Active_Handoffs[key] = null;
    writeMap(mapPath, map);
    return { dir, key, file: null, cleared: expected };
  }

  fail(
    "usage: handoff-map.mjs get | publish <doc.md> | clear --expect <file.md>",
  );
}

console.log(JSON.stringify(main(process.argv.slice(2)), null, 2));
