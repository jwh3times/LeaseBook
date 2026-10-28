#!/usr/bin/env node
// Reads and updates handoff_map.json, the map in the Handoffs folder that names each project's
// active handoff document. The handoff and lets-go skills change the map only through this script:
// it is shared by every repository on every machine, so a hand-edited slip breaks all of them.
//
//   node .agents/skills/handoff/handoff-map.mjs config [--write] [--transport T] [--dir D] [--remote R]
//   node .agents/skills/handoff/handoff-map.mjs get --key <key>
//   node .agents/skills/handoff/handoff-map.mjs name --key <key> [--topic <slug>]
//   node .agents/skills/handoff/handoff-map.mjs set <file> --key <key>
//   node .agents/skills/handoff/handoff-map.mjs clear --key <key> --expect <file>
//   node .agents/skills/handoff/handoff-map.mjs push <file>
//
// Every command prints one JSON object. The key comes from the repository's
// docs/agents/handoffs.md; this script reads no repository settings.
//
// Machine config: ~/.config/handoffs/config (or $XDG_CONFIG_HOME/handoffs/config, or
// $HANDOFFS_CONFIG), shell-style KEY=value lines; an environment variable of the same name wins.
//
//   HANDOFFS_TRANSPORT  folder      the folder is everything: a sync client (or nothing) moves it
//                       proton-cli  a local mirror that this script pulls and pushes through the
//                                   proton-drive CLI
//   HANDOFFS_DIR        the local Handoffs folder (the synced folder, or the mirror)
//   HANDOFFS_REMOTE     proton-cli only: the cloud folder (default /my-files/Documents/Handoffs)

import { spawnSync } from "node:child_process";
import {
  existsSync,
  mkdirSync,
  mkdtempSync,
  readdirSync,
  readFileSync,
  renameSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import { homedir, tmpdir } from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

export const MAP_FILE = "handoff_map.json";
export const TRANSPORTS = ["folder", "proton-cli"];
export const DEFAULT_REMOTE = "/my-files/Documents/Handoffs";
const CONFIG_KEYS = ["HANDOFFS_TRANSPORT", "HANDOFFS_DIR", "HANDOFFS_REMOTE"];
const MAX_ATTEMPTS = 3;

export class HandoffError extends Error {}

// ---------------------------------------------------------------------------------------------
// Arguments

export function parseArgs(argv) {
  const [command, ...rest] = argv;
  const commands = ["config", "get", "name", "set", "clear", "push"];
  if (!commands.includes(command)) {
    throw new HandoffError(`expected one of ${commands.join(", ")}; got ${command ?? "(none)"}`);
  }
  const flags = {
    key: "--key",
    topic: "--topic",
    expect: "--expect",
    transport: "--transport",
    dir: "--dir",
    remote: "--remote",
  };
  const options = { command, write: false, file: undefined };
  for (let index = 0; index < rest.length; index += 1) {
    const argument = rest[index];
    const name = Object.keys(flags).find((flag) => flags[flag] === argument);
    if (name) {
      const value = rest[index + 1];
      if (value === undefined || value.startsWith("--"))
        throw new HandoffError(`${argument} needs a value`);
      options[name] = value;
      index += 1;
    } else if (argument === "--write" && command === "config") {
      options.write = true;
    } else if (
      (command === "set" || command === "push") &&
      options.file === undefined &&
      !argument.startsWith("--")
    ) {
      options.file = argument;
    } else {
      throw new HandoffError(`unknown argument: ${argument}`);
    }
  }
  if (["get", "name", "set", "clear"].includes(command) && !options.key) {
    throw new HandoffError(
      `${command} needs --key <key> (the Map key setting in docs/agents/handoffs.md)`,
    );
  }
  if ((command === "set" || command === "push") && !options.file) {
    throw new HandoffError(`${command} needs the handoff file name`);
  }
  if (command === "clear" && !options.expect) {
    throw new HandoffError("clear needs --expect <file> naming the handoff that was read");
  }
  for (const file of [options.file, options.expect]) {
    if (file && path.basename(file) !== file) {
      throw new HandoffError(
        `expected a file name inside the Handoffs folder, not a path: ${file}`,
      );
    }
  }
  if (options.topic && !/^[a-z0-9]+(?:-[a-z0-9]+)*$/u.test(options.topic)) {
    throw new HandoffError(
      `--topic takes a lowercase slug such as ship-port; got ${options.topic}`,
    );
  }
  return options;
}

// ---------------------------------------------------------------------------------------------
// Machine config

export function configPath(env = process.env, home = homedir()) {
  if (env.HANDOFFS_CONFIG) return env.HANDOFFS_CONFIG;
  return path.join(env.XDG_CONFIG_HOME || path.join(home, ".config"), "handoffs", "config");
}

export function parseConfig(text) {
  const values = {};
  for (const line of text.replace(/^\uFEFF/u, "").split(/\r?\n/u)) {
    const match = line.match(/^\s*(?:export\s+)?([A-Z_]+)\s*=\s*(.*?)\s*$/u);
    if (!match || line.trim().startsWith("#")) continue;
    values[match[1]] = match[2].replace(/^(["'])(.*)\1$/u, "$2");
  }
  return values;
}

export function expandHome(value, home = homedir()) {
  return value === "~" || /^~[/\\]/u.test(value) ? path.join(home, value.slice(1)) : value;
}

// The machine config, from the config file with environment variables layered over it.
export function readConfig({ env = process.env, home = homedir(), read = readIfExists } = {}) {
  const file = configPath(env, home);
  const text = read(file);
  const fromFile = text === null ? {} : parseConfig(text);
  const merged = {};
  for (const key of CONFIG_KEYS) {
    const value = env[key]?.trim() || fromFile[key];
    if (value) merged[key] = value;
  }
  const transport = merged.HANDOFFS_TRANSPORT ?? null;
  const dir = merged.HANDOFFS_DIR ? path.resolve(expandHome(merged.HANDOFFS_DIR, home)) : null;
  const remote = transport === "proton-cli" ? (merged.HANDOFFS_REMOTE ?? DEFAULT_REMOTE) : null;
  const problems = [];
  if (!transport) problems.push("HANDOFFS_TRANSPORT is not set");
  else if (!TRANSPORTS.includes(transport))
    problems.push(`HANDOFFS_TRANSPORT must be one of ${TRANSPORTS.join(", ")}; got ${transport}`);
  if (!dir) problems.push("HANDOFFS_DIR is not set");
  return {
    file,
    fileExists: text !== null,
    transport,
    dir,
    remote,
    configured: problems.length === 0,
    problems,
  };
}

function readIfExists(file) {
  try {
    return readFileSync(file, "utf8");
  } catch {
    return null;
  }
}

// Proton Drive's Windows folders are cloud-filter reparse points, which a Dirent does not report as
// directories, so anything not plainly a file is stat'ed through.
function childDirs(dir) {
  let entries;
  try {
    entries = readdirSync(dir, { withFileTypes: true });
  } catch {
    return [];
  }
  return entries
    .filter((entry) => {
      if (entry.isDirectory()) return true;
      if (entry.isFile()) return false;
      try {
        return statSync(path.join(dir, entry.name)).isDirectory();
      } catch {
        return false;
      }
    })
    .map((entry) => path.join(dir, entry.name));
}

// Case-insensitive, because Proton clients disagree on "My files" and "My Files".
function childNamed(dir, name) {
  if (!dir) return undefined;
  return childDirs(dir).find((child) => path.basename(child).toLowerCase() === name.toLowerCase());
}

// Every <home>/<*proton*>/[<account>/]My files/Documents/Handoffs holding a map.
export function findSyncedFolders(home = homedir()) {
  const found = new Set();
  for (const root of childDirs(home).filter((dir) => /proton/iu.test(path.basename(dir)))) {
    for (const base of [root, ...childDirs(root)]) {
      const handoffs = childNamed(
        childNamed(childNamed(base, "My files"), "Documents"),
        "Handoffs",
      );
      if (handoffs && existsSync(path.join(handoffs, MAP_FILE))) found.add(handoffs);
    }
  }
  return [...found];
}

// What step 0 proposes for this machine, with the evidence behind it.
export function proposeConfig({
  env = process.env,
  home = homedir(),
  synced = findSyncedFolders(home),
  hasCli = commandExists("proton-drive"),
} = {}) {
  const evidence = [];
  if (synced.length === 1) {
    evidence.push(`a synced Proton Drive Handoffs folder holding ${MAP_FILE}: ${synced[0]}`);
    return { transport: "folder", dir: synced[0], remote: null, evidence };
  }
  if (synced.length > 1)
    evidence.push(`several synced Handoffs folders; pick one: ${synced.join(", ")}`);
  if (hasCli) {
    const dir = env.HANDOFFS_DIR?.trim()
      ? path.resolve(expandHome(env.HANDOFFS_DIR.trim(), home))
      : path.join(home, "Handoffs");
    evidence.push("the proton-drive CLI is on PATH and no synced folder was found");
    if (env.HANDOFFS_DIR?.trim())
      evidence.push(`HANDOFFS_DIR is already exported as ${env.HANDOFFS_DIR.trim()}`);
    return { transport: "proton-cli", dir, remote: DEFAULT_REMOTE, evidence };
  }
  evidence.push(
    "no synced Handoffs folder and no proton-drive CLI: ask where the Handoffs folder is",
  );
  return { transport: "folder", dir: synced[0] ?? null, remote: null, evidence };
}

export function formatConfig({ transport, dir, remote }) {
  const lines = [
    "# Handoffs machine config, read by the handoff and lets-go skills (jwh3times/skills).",
    `HANDOFFS_TRANSPORT=${transport}`,
    `HANDOFFS_DIR=${dir}`,
  ];
  if (transport === "proton-cli") lines.push(`HANDOFFS_REMOTE=${remote ?? DEFAULT_REMOTE}`);
  return `${lines.join("\n")}\n`;
}

function commandExists(name) {
  const probe =
    process.platform === "win32"
      ? spawnSync("where", [name], { stdio: "ignore", windowsHide: true })
      : spawnSync("sh", ["-c", `command -v ${name}`], { stdio: "ignore" });
  return probe.status === 0;
}

// ---------------------------------------------------------------------------------------------
// The map

export function normalizeKey(name) {
  return name.toLowerCase().replace(/[^a-z0-9]/gu, "");
}

export function findKey(active, key) {
  const wanted = normalizeKey(key);
  return Object.keys(active).find((existing) => normalizeKey(existing) === wanted) ?? null;
}

/** `MM-dd-yyyy HH:mm:ss` in local time, the format the map already uses. */
export function formatTimestamp(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return (
    `${pad(date.getMonth() + 1)}-${pad(date.getDate())}-${date.getFullYear()} ` +
    `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
  );
}

export function readMap(dir) {
  const file = path.join(dir, MAP_FILE);
  if (!existsSync(file)) {
    throw new HandoffError(
      `no ${MAP_FILE} in ${dir}; the map is shared by every repository, so this script never creates one`,
    );
  }
  const raw = readFileSync(file, "utf8").replace(/^\uFEFF/u, "");
  let map;
  try {
    map = JSON.parse(raw);
  } catch (error) {
    throw new HandoffError(`${file} is not valid JSON: ${error.message}`);
  }
  if (
    !map?.Active_Handoffs ||
    typeof map.Active_Handoffs !== "object" ||
    Array.isArray(map.Active_Handoffs)
  ) {
    throw new HandoffError(`${file} has no Active_Handoffs object`);
  }
  return { file, map, eol: raw.includes("\r\n") ? "\r\n" : "\n" };
}

/** A new map with `key`'s entry set to `file`, keeping the map's own spelling of the key. */
export function updateMap(map, key, file, now) {
  const active = map.Active_Handoffs;
  const existing = findKey(active, key);
  return {
    key: existing ?? key,
    previous: existing === null ? null : (active[existing] ?? null),
    map: {
      ...map,
      Last_Updated: formatTimestamp(now),
      Active_Handoffs: { ...active, [existing ?? key]: file },
    },
  };
}

// Written to a temporary file and renamed over the map, so a reader never sees half a map.
function writeMap(file, map, eol) {
  const temporary = `${file}.${process.pid}.tmp`;
  writeFileSync(temporary, JSON.stringify(map, null, 2).replace(/\n/gu, eol) + eol, "utf8");
  renameSync(temporary, file);
}

// Proton Drive names sync-conflict copies after the original file.
export function conflictCopies(dir) {
  return readdirSync(dir).filter(
    (name) => name !== MAP_FILE && /^handoff_map\b.*\.json$/iu.test(name),
  );
}

export function keySlug(key) {
  return key
    .toLowerCase()
    .replace(/[^a-z0-9]+/gu, "-")
    .replace(/^-|-$/gu, "");
}

export function localDate(date) {
  const pad = (value) => String(value).padStart(2, "0");
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}

/** `<key>-handoff-<date>[-<topic>].md`, numbered from -2 when the name is taken. */
export function pickName(key, taken, now, topic) {
  const base = `${keySlug(key)}-handoff-${localDate(now)}${topic ? `-${topic}` : ""}`;
  const used = new Set([...taken].map((name) => name.toLowerCase()));
  let candidate = `${base}.md`;
  for (let n = 2; used.has(candidate.toLowerCase()); n += 1) candidate = `${base}-${n}.md`;
  return candidate;
}

// ---------------------------------------------------------------------------------------------
// The proton-drive CLI

const LOGIN_HINT =
  "the proton-drive CLI needs a login: the user runs `proton-drive auth login` in a real terminal, then the command is retried";

function runCli(args) {
  const result = spawnSync("proton-drive", args, { encoding: "utf8", windowsHide: true });
  if (result.error) throw new HandoffError(`could not run proton-drive: ${result.error.message}`);
  return { status: result.status, stdout: result.stdout ?? "", stderr: result.stderr ?? "" };
}

function parseSummary(output) {
  const line = output.split(/\r?\n/u).find((text) => text.trim().startsWith("{"));
  if (!line) return null;
  try {
    return JSON.parse(line);
  } catch {
    return null;
  }
}

export function makeCloud(config, cli = runCli) {
  const call = (args) => {
    const result = cli(args);
    const output = `${result.stdout}\n${result.stderr}`;
    if (/need to log ?in/iu.test(output)) throw new HandoffError(LOGIN_HINT);
    return { ...result, output, summary: parseSummary(result.stdout) };
  };
  const remotePath = (name) => `${config.remote.replace(/\/+$/u, "")}/${name}`;

  return {
    // Downloads one file into `into`, replacing a local copy. False when the cloud lacks it.
    download(name, into = config.dir) {
      mkdirSync(into, { recursive: true });
      const result = call(["filesystem", "download", "-j", "-f", "remove", remotePath(name), into]);
      if (/node not found/iu.test(result.output)) return false;
      if (
        result.status !== 0 ||
        !result.summary ||
        result.summary.failedItems > 0 ||
        result.summary.transferredItems < 1
      ) {
        throw new HandoffError(`downloading ${remotePath(name)} failed: ${result.output.trim()}`);
      }
      return true;
    },
    // Uploads files from the local folder as new revisions. A file whose content the cloud already
    // has reports as skipped, which counts as uploaded.
    upload(names) {
      const result = call([
        "filesystem",
        "upload",
        "-j",
        "-f",
        "create-new-revision",
        "-t",
        ...names.map((name) => path.join(config.dir, name)),
        config.remote,
      ]);
      const { summary } = result;
      if (
        result.status !== 0 ||
        !summary ||
        summary.failedItems > 0 ||
        summary.transferredItems + summary.skippedItems < names.length
      ) {
        throw new HandoffError(
          `uploading ${names.join(", ")} to ${config.remote} failed: ${result.output.trim()}`,
        );
      }
      return { uploaded: summary.transferredItems, skipped: summary.skippedItems };
    },
    list() {
      const result = call(["filesystem", "list", "-j", config.remote]);
      let nodes;
      try {
        nodes = JSON.parse(result.stdout);
      } catch {
        throw new HandoffError(`listing ${config.remote} failed: ${result.output.trim()}`);
      }
      return nodes.map((node) => node?.name?.value).filter(Boolean);
    },
  };
}

// ---------------------------------------------------------------------------------------------
// Commands

function requireConfig(config) {
  if (!config.configured) {
    throw new HandoffError(
      `no machine config (${config.problems.join("; ")}): run \`node .agents/skills/handoff/handoff-map.mjs config\` and set it up with the user`,
    );
  }
  if (config.transport === "folder" && !existsSync(config.dir)) {
    throw new HandoffError(`HANDOFFS_DIR does not exist: ${config.dir}`);
  }
}

// Points `key` at `next` (a file name, or null to clear). On the CLI mirror the map is pulled
// right before the change, pushed right after, and read back from the cloud; a mismatch means
// another session wrote in between, so the whole round repeats.
function writeEntry(context, key, next, { expect } = {}) {
  const { config, cloud, now } = context;
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt += 1) {
    if (cloud && !cloud.download(MAP_FILE))
      throw new HandoffError(`the cloud folder ${config.remote} has no ${MAP_FILE}`);
    const { file, map, eol } = readMap(config.dir);
    const current = findKey(map.Active_Handoffs, key);
    if (expect !== undefined) {
      if (current === null) throw new HandoffError(`the map has no entry for ${key}`);
      const actual = map.Active_Handoffs[current] ?? null;
      if (actual !== expect) {
        throw new HandoffError(
          `refusing to clear ${current}: the map names ${JSON.stringify(actual)}, not ${JSON.stringify(expect)}`,
        );
      }
    }
    const updated = updateMap(map, key, next, now());
    writeMap(file, updated.map, eol);
    if (!cloud) {
      return {
        key: updated.key,
        previous: updated.previous,
        current: next,
        lastUpdated: updated.map.Last_Updated,
        conflicts: conflictCopies(config.dir),
      };
    }
    cloud.upload([MAP_FILE]);
    const check = mkdtempSync(path.join(tmpdir(), "handoff-map-"));
    try {
      cloud.download(MAP_FILE, check);
      const { map: remote } = readMap(check);
      const remoteKey = findKey(remote.Active_Handoffs, key);
      if (remoteKey !== null && (remote.Active_Handoffs[remoteKey] ?? null) === next) {
        return {
          key: updated.key,
          previous: updated.previous,
          current: next,
          lastUpdated: updated.map.Last_Updated,
          verified: "cloud",
          attempts: attempt,
        };
      }
    } finally {
      rmSync(check, { recursive: true, force: true });
    }
  }
  throw new HandoffError(
    `the cloud map kept changing underneath ${key}; ${MAX_ATTEMPTS} attempts failed`,
  );
}

export function run(
  options,
  {
    env = process.env,
    home = homedir(),
    cli = runCli,
    now = () => new Date(),
    read = readIfExists,
  } = {},
) {
  const config = readConfig({ env, home, read });

  if (options.command === "config") {
    const chosen = {
      transport: options.transport,
      dir: options.dir && path.resolve(expandHome(options.dir, home)),
      remote: options.remote,
    };
    if (!options.write) {
      const proposal = config.configured ? null : proposeConfig({ env, home });
      return { ...config, proposal };
    }
    const base = config.configured ? config : proposeConfig({ env, home });
    const values = {
      transport: chosen.transport ?? base.transport,
      dir: chosen.dir ?? base.dir,
      remote: chosen.remote ?? base.remote,
    };
    if (!TRANSPORTS.includes(values.transport))
      throw new HandoffError(`--transport must be one of ${TRANSPORTS.join(", ")}`);
    if (!values.dir) throw new HandoffError("--dir is needed: no Handoffs folder was found");
    mkdirSync(path.dirname(config.file), { recursive: true });
    writeFileSync(config.file, formatConfig(values), "utf8");
    return readConfig({ env, home, read });
  }

  requireConfig(config);
  const cloud = config.transport === "proton-cli" ? makeCloud(config, cli) : null;
  const context = { config, cloud, now };
  const base = { transport: config.transport, dir: config.dir };

  if (options.command === "get") {
    if (cloud && !cloud.download(MAP_FILE))
      throw new HandoffError(`the cloud folder ${config.remote} has no ${MAP_FILE}`);
    const { map } = readMap(config.dir);
    const key = findKey(map.Active_Handoffs, options.key);
    const file = key === null ? null : (map.Active_Handoffs[key] ?? null);
    const docPath = file === null ? null : path.join(config.dir, file);
    let exists = docPath === null ? null : existsSync(docPath);
    let note;
    if (file !== null && !exists && cloud) {
      exists = cloud.download(file);
      if (!exists)
        note =
          "the cloud folder does not have this document yet: the other machine has not pushed it";
    }
    return {
      ...base,
      key: key ?? options.key,
      inMap: key !== null,
      file,
      path: docPath,
      exists,
      conflicts: conflictCopies(config.dir),
      ...(note ? { note } : {}),
    };
  }

  if (options.command === "name") {
    if (cloud) mkdirSync(config.dir, { recursive: true });
    const taken = new Set(existsSync(config.dir) ? readdirSync(config.dir) : []);
    if (cloud) for (const name of cloud.list()) taken.add(name);
    const file = pickName(options.key, taken, now(), options.topic);
    return { ...base, file, path: path.join(config.dir, file) };
  }

  if (options.command === "set") {
    if (!existsSync(path.join(config.dir, options.file))) {
      throw new HandoffError(
        `refusing to point the map at a file that is not in ${config.dir}: ${options.file}`,
      );
    }
    // The document reaches the cloud before the map names it.
    const pushed = cloud ? cloud.upload([options.file]) : null;
    return {
      ...base,
      ...writeEntry(context, options.key, options.file),
      ...(pushed ? { document: pushed } : {}),
    };
  }

  if (options.command === "clear") {
    return { ...base, ...writeEntry(context, options.key, null, { expect: options.expect }) };
  }

  // push: an edited document goes to the cloud again, without the map.
  if (!existsSync(path.join(config.dir, options.file))) {
    throw new HandoffError(`no ${options.file} in ${config.dir}`);
  }
  if (!cloud)
    return {
      ...base,
      file: options.file,
      pushed: false,
      reason: "folder transport: the folder syncs itself",
    };
  return { ...base, file: options.file, pushed: true, ...cloud.upload([options.file]) };
}

const scriptPath = fileURLToPath(import.meta.url);
if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  try {
    console.log(JSON.stringify(run(parseArgs(process.argv.slice(2))), null, 2));
  } catch (error) {
    console.error(`handoff-map: ${error.message}`);
    process.exitCode = 1;
  }
}
