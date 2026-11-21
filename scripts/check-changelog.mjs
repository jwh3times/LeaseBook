#!/usr/bin/env node
// Verifies CHANGELOG.md for the merge this branch will make. The required changelog job runs it, and
// `ship` runs it after writing the changelog. Which rule applies follows `Scheme` in
// `docs/agents/releases.md`.
//
//   node scripts/check-changelog.mjs                                # per-merge
//   node scripts/check-changelog.mjs --source src --source web/src  # cut-on-line, stamp-on-tag
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
// stamp-on-tag: a branch's entries live in its own `changelog.d/<branch-slug>.md` fragment, and
// the tag workflow (`scripts/stamp-changelog.mjs`) writes the dated sections. So the branch may not
// add, remove or renumber a version section or add entries to `## [Unreleased]` (it may empty it,
// and prose elsewhere, such as the policy at the top, may change); the one section it may add is a
// backfill for a version already tagged with none, which only a merge from before the move to
// this scheme leaves, since the workflow stamps every later one; it may not edit or delete a
// fragment another merge added; every fragment it adds must parse; and, with `--source` paths, a
// change under one needs a fragment. Without `--source`, a branch with no fragment is fine: the
// tag workflow writes its entry from the PR title.
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

export const FRAGMENT_DIR = "changelog.d";

// Keep a Changelog's categories, in the order a section lists them; others follow in the order
// they first appear.
export const CATEGORY_ORDER = ["Added", "Changed", "Deprecated", "Removed", "Fixed", "Security"];

// A fragment is a direct child `changelog.d/<name>.md`; a README there explains the folder.
export function isFragmentPath(file) {
  const match = new RegExp(`^${FRAGMENT_DIR.replaceAll(".", "\\.")}/([^/]+)\\.md$`).exec(file);
  return match !== null && match[1].toLowerCase() !== "readme";
}

// A fragment holds `### <Category>` headings, each followed by its entries; the version heading is
// the tag workflow's to write. Returns the entry lines by category, in file order, and any errors.
export function parseFragment(text) {
  const categories = new Map();
  const errors = [];
  let current = null;
  const normalized = text.replace(/^\uFEFF/, "").replace(/\r\n?/g, "\n");
  for (const [index, line] of normalized.split("\n").entries()) {
    const heading = /^### (\S.*?)\s*$/.exec(line);
    if (heading) {
      current = heading[1];
      if (!categories.has(current)) categories.set(current, []);
    } else if (/^#{1,6}\s/.test(line)) {
      errors.push(`line ${index + 1}: only ### category headings belong in a fragment.`);
    } else if (line.trim() === "") {
      if (current) categories.get(current).push("");
    } else if (current === null) {
      errors.push(`line ${index + 1}: an entry before the first ### heading.`);
    } else {
      categories.get(current).push(line);
    }
  }
  for (const [name, lines] of categories) {
    const first = lines.findIndex((line) => line.trim() !== "");
    if (first === -1) {
      errors.push(`### ${name} has no entries.`);
      categories.delete(name);
      continue;
    }
    const last = lines.findLastIndex((line) => line.trim() !== "");
    categories.set(name, lines.slice(first, last + 1));
  }
  if (categories.size === 0 && errors.length === 0) errors.push("it holds no entries.");
  return { categories, errors };
}

// The versions a branch's changelog adds to its base's, or null when it removes, reorders or
// repeats one of them.
export function addedVersions(versions, baseVersions) {
  if (new Set(versions).size !== versions.length) return null;
  const added = [];
  let next = 0;
  for (const version of versions) {
    if (version === baseVersions[next]) next += 1;
    else if (baseVersions.includes(version)) return null;
    else added.push(version);
  }
  return next === baseVersions.length ? added : null;
}

// `pending` lists fragments that already exist on the base and that the branch edits or deletes:
// they belong to merges not yet stamped, and the stamp reads each from its own merge, so an edit
// would be lost. Emptying `## [Unreleased]` is allowed, so the move to this scheme can carry its
// accumulated entries into a fragment. `tagged` lists the released versions: a section added for
// one of them backfills a tag minted before the move, which the workflow will never stamp.
export function checkStampOnTag({
  changelog,
  baseChangelog,
  changedFiles,
  fragments,
  pending = [],
  sources,
  tagged = [],
}) {
  const errors = [];
  if (baseChangelog !== null) {
    const added = addedVersions(documentedVersions(changelog), documentedVersions(baseChangelog));
    const untagged = added?.filter((version) => !tagged.includes(version)) ?? [];
    if (added === null || untagged.length > 0) {
      errors.push(
        `This branch adds, removes or renumbers a version section; the tag workflow writes them. ` +
          `Put its entries in ${FRAGMENT_DIR}/<branch-slug>.md. Only a version already tagged ` +
          `with no section may be added, as a backfill.`,
      );
    }
    const unreleased = unreleasedSection(changelog);
    if (unreleased !== unreleasedSection(baseChangelog) && /^\s*[-*+] /m.test(unreleased ?? "")) {
      errors.push(
        `This branch edits [Unreleased]; put its entries in ${FRAGMENT_DIR}/<branch-slug>.md.`,
      );
    }
  }
  for (const file of pending) {
    errors.push(
      `${file}: this branch edits or deletes a fragment another merge added, and that merge's ` +
        `copy is what gets released. Put this branch's entries in its own fragment.`,
    );
  }
  for (const { file, text } of fragments) {
    for (const error of parseFragment(text).errors) errors.push(`${file}: ${error}`);
  }
  if (sources.length > 0 && fragments.length === 0 && touchesSource(changedFiles, sources)) {
    errors.push(
      `This branch changes ${sources.join(", ")} with no fragment in ${FRAGMENT_DIR}/. ` +
        `Run the ship skill to write it.`,
    );
  }
  return errors;
}

export function touchesSource(files, sources) {
  return files.some((file) =>
    sources.some((source) => file === source || file.startsWith(`${source}/`)),
  );
}

function git(root, args) {
  const result = spawnSync("git", ["-c", "core.quotePath=false", "-C", root, ...args], {
    encoding: "utf8",
    windowsHide: true,
  });
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

// Each path changed since `base`, tracked or new, with its status letter (A, M, D, ...).
function statusSince(root, base) {
  const diff = git(root, ["diff", "--name-status", "-z", "--no-renames", base]);
  const fields = diff.stdout.split("\0");
  const entries = [];
  for (let index = 0; index + 1 < fields.length; index += 2) {
    entries.push({ status: fields[index][0], file: fields[index + 1] });
  }
  const untracked = git(root, ["ls-files", "-z", "--others", "--exclude-standard"]).stdout;
  for (const file of untracked.split("\0").filter(Boolean)) entries.push({ status: "A", file });
  return entries;
}

function checkStampOnTagRepository(root, changelog, options) {
  const base = options.base ?? mergeBase(root);
  const baseFile = git(root, ["show", `${base}:${CHANGELOG}`]);
  const changed = statusSince(root, base);
  const fragments = changed
    .filter(({ status, file }) => status === "A" && isFragmentPath(file))
    .map(({ file }) => ({ file, text: readFileSync(path.join(root, file), "utf8") }));
  const pending = changed
    .filter(({ status, file }) => status !== "A" && isFragmentPath(file))
    .map(({ file }) => file);
  refreshTags(root);
  const tagged = releasedVersions(gitTags(root));
  return checkStampOnTag({
    changelog,
    baseChangelog: baseFile.ok ? baseFile.stdout : null,
    changedFiles: changed.map(({ file }) => file),
    fragments,
    pending,
    sources: options.sources,
    tagged,
  });
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
  } else if (scheme.startsWith("stamp-on-tag")) {
    errors = checkStampOnTagRepository(root, changelog, options);
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
