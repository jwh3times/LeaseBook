#!/usr/bin/env node
// Verifies CHANGELOG.md for the merge this branch will make. The required changelog job runs it, and
// `ship` runs it after writing the changelog. Which rule applies follows `Scheme` in
// `docs/agents/releases.md`.
//
//   node scripts/check-changelog.mjs                                # per-merge
//   node scripts/check-changelog.mjs --source src --source web/src  # cut-on-line
//
// per-merge: the newest `## [x.y.z]` section names the version this merge will mint. With
// `Skipped tags: backfill` (the default), every released three-part tag also has a section, except
// those matching its **Ignore** patterns. A bot cannot write an entry, so a workflow exempts
// dependency-bot PRs before running this; the second rule then fails the next human PR until those
// builds are backfilled, instead of letting the gaps pile up.
//
// cut-on-line: when the declared version changed from the merge base, it has a dated section (and,
// in a file with a link footer, links for it); otherwise a change under a `--source` path needs
// `## [Unreleased]` to change. `--base <ref>` compares against a ref instead of the merge base with
// origin/main.
//
// none: there is no changelog, so there is nothing to check.
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import {
  computeNextVersion,
  declaredVersion,
  gitTags,
  parseDeclaredVersion,
  readSetting,
  refreshTags,
  repositoryRoot,
  versionFilePath,
} from "./next-version.mjs";

const CHANGELOG = "CHANGELOG.md";

export function parseArgs(argv) {
  const options = { sources: [], base: null };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument !== "--source" && argument !== "--base") {
      throw new Error(`Unknown argument: ${argument}`);
    }
    const value = argv[index + 1];
    if (!value) throw new Error(`${argument} requires a value.`);
    if (argument === "--source") options.sources.push(value.replace(/\/+$/, ""));
    else options.base = value;
    index += 1;
  }
  return options;
}

export function releasedVersions(tags) {
  return tags.flatMap((tag) => /^v(\d+\.\d+\.\d+)$/.exec(tag.trim())?.[1] ?? []);
}

export function documentedVersions(changelog) {
  return [...changelog.matchAll(/^## \[(\d+\.\d+\.\d+)\]/gm)].map((match) => match[1]);
}

function compareVersions(a, b) {
  const [left, right] = [a, b].map((version) => version.split(".").map(Number));
  return left[0] - right[0] || left[1] - right[1] || left[2] - right[2];
}

// The backticked tag patterns of a `Skipped tags` value's **Ignore** list, as regular expressions
// over tag names.
export function ignorePatterns(skippedTags) {
  const at = skippedTags?.indexOf("**Ignore") ?? -1;
  if (at === -1) return [];
  return [...skippedTags.slice(at).matchAll(/`(v[0-9*][0-9.*]*)`/g)].map(
    (match) => new RegExp(`^${match[1].replaceAll(".", "\\.").replaceAll("*", ".*")}$`),
  );
}

export function checkPerMerge({ changelog, tags, next, skippedTags }) {
  const errors = [];
  const documented = documentedVersions(changelog);

  if (documented[0] !== next) {
    errors.push(
      `This merge will mint v${next}, but the newest section is ` +
        `${documented[0] ? `[${documented[0]}]` : "missing"}. Run the ship skill to write or ` +
        `renumber it. If another branch merged first, it took the number.`,
    );
  }

  if ((skippedTags ?? "backfill").startsWith("backfill")) {
    const known = new Set(documented);
    const ignored = ignorePatterns(skippedTags);
    const missing = releasedVersions(tags)
      .filter((version) => !known.has(version))
      .filter((version) => !ignored.some((pattern) => pattern.test(`v${version}`)))
      .sort(compareVersions);
    if (missing.length > 0) {
      errors.push(
        `Released with no section: ${missing.join(", ")}. Run the ship skill to backfill them, ` +
          `or list predating tags under the Skipped tags Ignore patterns.`,
      );
    }
  }

  return errors;
}

// The body of `## [Unreleased]`, normalized for comparison, or null when the heading is absent.
export function unreleasedSection(changelog) {
  const lines = changelog.split(/\r?\n/);
  const start = lines.findIndex((line) => /^## \[Unreleased\]/.test(line));
  if (start === -1) return null;
  const end = lines.findIndex((line, index) => index > start && line.startsWith("## ["));
  return lines
    .slice(start + 1, end === -1 ? undefined : end)
    .join("\n")
    .trim();
}

export function checkCut({ changelog, version }) {
  const errors = [];
  const escaped = version.replaceAll(".", "\\.");
  if (!new RegExp(`^## \\[${escaped}\\] - \\d{4}-\\d{2}-\\d{2}\\s*$`, "m").test(changelog)) {
    errors.push(`The version is now ${version}, with no '## [${version}] - <date>' section.`);
  }
  if (/^\[Unreleased\]:/m.test(changelog)) {
    if (!new RegExp(`^\\[Unreleased\\]: \\S*/compare/v${escaped}\\.\\.\\.HEAD\\s*$`, "m").test(changelog)) {
      errors.push(`The [Unreleased] link must compare from v${version} to HEAD.`);
    }
    if (!new RegExp(`^\\[${escaped}\\]: \\S*v${escaped}\\s*$`, "m").test(changelog)) {
      errors.push(`The link footer has no [${version}] link ending in v${version}.`);
    }
  }
  return errors;
}

export function touchesSource(files, sources) {
  return files.some((file) =>
    sources.some((source) => file === source || file.startsWith(`${source}/`)),
  );
}

function git(root, args) {
  const result = spawnSync("git", ["-C", root, ...args], { encoding: "utf8", windowsHide: true });
  return { ok: result.status === 0, stdout: result.stdout ?? "" };
}

function mergeBase(root) {
  for (const ref of ["origin/main", "main"]) {
    const result = git(root, ["merge-base", ref, "HEAD"]);
    if (result.ok) return result.stdout.trim();
  }
  throw new Error("Cannot find a merge base with main. Fetch it; in CI use fetch-depth: 0.");
}

function checkCutOnLine(root, changelog, options) {
  if (options.sources.length === 0) {
    throw new Error("`Scheme: cut-on-line` needs at least one --source path.");
  }
  const base = options.base ?? mergeBase(root);
  const file = versionFilePath(root);
  const version = String(declaredVersion(root)).trim();
  const baseFile = git(root, ["show", `${base}:${file}`]);
  const baseVersion = baseFile.ok ? parseDeclaredVersion(file, baseFile.stdout) : null;
  if (baseVersion !== version) return checkCut({ changelog, version });

  const changed = [
    ...git(root, ["diff", "--name-only", base]).stdout.split(/\r?\n/),
    ...git(root, ["ls-files", "--others", "--exclude-standard"]).stdout.split(/\r?\n/),
  ].filter(Boolean);
  if (!touchesSource(changed, options.sources)) return [];

  const baseChangelog = git(root, ["show", `${base}:${CHANGELOG}`]).stdout;
  return unreleasedSection(changelog) === unreleasedSection(baseChangelog)
    ? [`This branch changes ${options.sources.join(", ")} with no change to [Unreleased].`]
    : [];
}

export function main(argv, root = repositoryRoot) {
  const options = parseArgs(argv);
  const scheme = readSetting(root, "releases", "Scheme");
  if (scheme === null) throw new Error("docs/agents/releases.md has no `Scheme` setting.");
  if (scheme.startsWith("none")) {
    console.log("Scheme is none: there is no changelog to check.");
    return 0;
  }

  const changelog = readFileSync(path.join(root, CHANGELOG), "utf8");
  let errors;
  if (scheme.startsWith("per-merge")) {
    refreshTags(root);
    const tags = gitTags(root);
    errors = checkPerMerge({
      changelog,
      tags,
      next: computeNextVersion(declaredVersion(root), tags),
      skippedTags: readSetting(root, "releases", "Skipped tags"),
    });
  } else if (scheme.startsWith("cut-on-line")) {
    errors = checkCutOnLine(root, changelog, options);
  } else {
    throw new Error(`Unknown Scheme: ${scheme.split(/\s/)[0]}`);
  }

  for (const error of errors) console.log(`::error file=${CHANGELOG}::${error}`);
  if (errors.length === 0) console.log(`${CHANGELOG} is correct for this merge.`);
  return errors.length === 0 ? 0 : 1;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`check-changelog: ${error instanceof Error ? error.message : error}`);
    process.exitCode = 1;
  }
}
