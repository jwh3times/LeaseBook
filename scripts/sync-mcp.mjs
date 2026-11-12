#!/usr/bin/env node
// Generates Codex's project MCP config from Claude Code's.
//
//   .mcp.json (authored) --> .codex/config.toml (generated, for Codex)
//
// One list of shared MCP servers, read by both harnesses. Codex loads a repository's
// `.codex/config.toml` only once the project is trusted in the user's own Codex config.
//
//   node scripts/sync-mcp.mjs           rewrite .codex/config.toml
//   node scripts/sync-mcp.mjs --check   exit 1 when the committed file is stale
//
// The source holds no secrets: an entry with anything but type, url, command and args is
// refused, so a key or token never reaches either checked-in file.

import { existsSync, mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const defaultRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
);
export const sourceFile = ".mcp.json";
export const targetFile = ".codex/config.toml";

// Codex gives a server 30 seconds to start by default; a first `npx -y` download can take longer.
const stdioStartupTimeoutSec = 60;

const header = [
  "# GENERATED from .mcp.json by scripts/sync-mcp.mjs — do not edit.",
  "# Edit .mcp.json, then run `node scripts/sync-mcp.mjs`. Codex loads this file only for a",
  '# trusted project (trust_level = "trusted" in your own ~/.codex/config.toml).',
];

const allowedKeys = {
  http: ["type", "url"],
  stdio: ["type", "command", "args"],
};

/** A TOML basic string. JSON escapes are valid TOML escapes for the characters allowed here. */
function tomlString(value) {
  return JSON.stringify(value);
}

function fail(message) {
  throw new Error(`${sourceFile}: ${message}`);
}

function checkString(name, key, value) {
  if (typeof value !== "string" || value.length === 0)
    fail(`"${name}".${key} must be a non-empty string`);
  // eslint-disable-next-line no-control-regex
  if (/[\u0000-\u001f\u007f]/.test(value))
    fail(`"${name}".${key} contains a control character`);
}

/** Validates the parsed `.mcp.json` and returns its servers in name order. */
export function readServers(raw) {
  let parsed;
  try {
    parsed = JSON.parse(raw);
  } catch (error) {
    fail(`not valid JSON (${error.message})`);
  }
  const servers = parsed?.mcpServers;
  if (
    servers === null ||
    typeof servers !== "object" ||
    Array.isArray(servers)
  ) {
    fail('needs an "mcpServers" object');
  }
  return Object.keys(servers)
    .sort()
    .map((name) => {
      if (!/^[A-Za-z0-9_-]+$/.test(name))
        fail(`server name "${name}" may use only letters, digits, - and _`);
      const entry = servers[name];
      const type = entry?.type;
      if (!(type in allowedKeys))
        fail(`"${name}".type must be "http" or "stdio"`);
      const extra = Object.keys(entry).filter(
        (key) => !allowedKeys[type].includes(key),
      );
      if (extra.length > 0) {
        fail(
          `"${name}" has ${extra.join(", ")}; shared servers carry no env, headers or other settings`,
        );
      }
      if (type === "http") {
        checkString(name, "url", entry.url);
        if (!entry.url.startsWith("https://"))
          fail(`"${name}".url must be https`);
        return { name, type, url: entry.url };
      }
      checkString(name, "command", entry.command);
      const args = entry.args ?? [];
      if (!Array.isArray(args)) fail(`"${name}".args must be an array`);
      args.forEach((arg, index) => checkString(name, `args[${index}]`, arg));
      return { name, type, command: entry.command, args };
    });
}

/** Renders `.codex/config.toml` from the text of `.mcp.json`. */
export function renderCodexConfig(raw) {
  const lines = [...header];
  for (const server of readServers(raw)) {
    lines.push("", `[mcp_servers.${server.name}]`);
    if (server.type === "http") {
      lines.push(`url = ${tomlString(server.url)}`);
    } else {
      lines.push(`command = ${tomlString(server.command)}`);
      lines.push(`args = [${server.args.map(tomlString).join(", ")}]`);
      lines.push(`startup_timeout_sec = ${stdioStartupTimeoutSec}`);
    }
  }
  return `${lines.join("\n")}\n`;
}

/** Writes or checks the generated file. Returns true when it is (or now is) up to date. */
export function syncMcp(root = defaultRoot, { check = false } = {}) {
  const expected = renderCodexConfig(
    readFileSync(path.join(root, sourceFile), "utf8"),
  );
  const target = path.join(root, targetFile);
  const current = existsSync(target)
    ? readFileSync(target, "utf8").replace(/\r\n/g, "\n")
    : null;
  if (current === expected) return true;
  if (check) return false;
  mkdirSync(path.dirname(target), { recursive: true });
  writeFileSync(target, expected);
  return true;
}

function main(argv) {
  const check = argv.includes("--check");
  try {
    if (syncMcp(defaultRoot, { check })) {
      console.log(
        check
          ? `${targetFile} matches ${sourceFile}.`
          : `Wrote ${targetFile} from ${sourceFile}.`,
      );
      return 0;
    }
    console.error(
      `${targetFile} is stale: edit ${sourceFile}, then run \`node scripts/sync-mcp.mjs\` and commit both.`,
    );
    return 1;
  } catch (error) {
    console.error(error.message);
    return 1;
  }
}

if (
  process.argv[1] &&
  path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)
) {
  process.exitCode = main(process.argv.slice(2));
}
