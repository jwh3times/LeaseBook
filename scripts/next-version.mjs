#!/usr/bin/env node
// Prints the version the next merge to main will mint, as a bare SemVer ("1.2.11", no leading "v").
// The tag workflow, the changelog guard and the `ship` skill all call this one computation.
//
//   node scripts/next-version.mjs
//
// The declared version comes from the file `docs/agents/releases.md` names under `Version file`
// (`package.json` when the heading is absent). Its major and minor pick the release line, and its
// build is a floor: the next build is the highest three-part tag on that line plus one, never below
// the floor, and a line with no tag yet releases the declared build as written, so x.y.0 stays
// x.y.0. Four-part tags (v1.1.1.3) predate the scheme and never count.
//
// Tags are refreshed from origin first, on a best-effort basis: offline, or with no credentials, it
// predicts from the local tags. A merge landing elsewhere can still take the number printed here,
// which is what the changelog check catches.
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

export const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

const DEFAULT_VERSION_FILE = "package.json";
const FETCH_TIMEOUT_MS = 30_000;

// The text under a `## <name>` heading of `docs/agents/<topic>.md`, or null when the file or the
// heading is absent.
export function readSetting(root, topic, name) {
  const file = path.join(root, "docs", "agents", `${topic}.md`);
  if (!existsSync(file)) return null;
  const lines = readFileSync(file, "utf8").split(/\r?\n/);
  const start = lines.findIndex((line) => line.trim() === `## ${name}`);
  if (start === -1) return null;
  const end = lines.findIndex((line, index) => index > start && line.startsWith("## "));
  return lines
    .slice(start + 1, end === -1 ? undefined : end)
    .join("\n")
    .trim();
}

// The repo-relative version file. A value is a backticked path, optionally followed by prose.
export function versionFilePath(root) {
  const value = readSetting(root, "releases", "Version file");
  if (value === null) return DEFAULT_VERSION_FILE;
  const file = /`([^`]+)`/.exec(value)?.[1] ?? value.split(/\s+/)[0];
  if (file === "none") throw new Error("docs/agents/releases.md says `Version file` is none.");
  return file;
}

export function parseDeclaredVersion(file, content) {
  return path.extname(file) === ".json" ? JSON.parse(content).version : content.trim();
}

export function declaredVersion(root) {
  const file = versionFilePath(root);
  return parseDeclaredVersion(file, readFileSync(path.join(root, file), "utf8"));
}

export function computeNextVersion(version, tags) {
  const declared = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(String(version).trim());
  if (!declared) throw new Error(`Version "${version}" is not a plain major.minor.build SemVer.`);
  const [, major, minor, build] = declared;
  const floor = Number(build);

  const onLine = new RegExp(`^v${major}\\.${minor}\\.(\\d+)$`);
  let highest = -1;
  for (const tag of tags) {
    const match = onLine.exec(tag.trim());
    if (match) highest = Math.max(highest, Number(match[1]));
  }

  return `${major}.${minor}.${highest === -1 ? floor : Math.max(floor, highest + 1)}`;
}

export function gitTags(root = repositoryRoot) {
  const result = spawnSync("git", ["-C", root, "tag", "--list"], {
    encoding: "utf8",
    windowsHide: true,
  });
  if (result.status !== 0) throw new Error(`git tag failed: ${result.stderr || result.error}`);
  return result.stdout.split(/\r?\n/).filter(Boolean);
}

export function refreshTags(root = repositoryRoot) {
  spawnSync("git", ["-C", root, "fetch", "--tags", "--quiet", "origin"], {
    env: { ...process.env, GIT_TERMINAL_PROMPT: "0" },
    stdio: "ignore",
    timeout: FETCH_TIMEOUT_MS,
    windowsHide: true,
  });
}

export function main(root = repositoryRoot) {
  refreshTags(root);
  console.log(computeNextVersion(declaredVersion(root), gitTags(root)));
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    main();
  } catch (error) {
    console.error(`next-version: ${error instanceof Error ? error.message : error}`);
    process.exitCode = 1;
  }
}
