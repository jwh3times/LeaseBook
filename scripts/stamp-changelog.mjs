#!/usr/bin/env node
// Stamps CHANGELOG.md for `Scheme: stamp-on-tag`. The tag workflow runs it on every push to main,
// in a full clone (fetch-depth: 0), with a git identity configured.
//
//   node scripts/stamp-changelog.mjs [--dry-run] [--notes-dir <dir>]
//
// From the tip it walks main's first-parent history back to the newest commit a three-part tag
// points at, skipping earlier stamp commits, and takes each untagged merge oldest first:
//
// - its section holds the `changelog.d/` fragments that merge added, merged by category, or,
//   when it added none (a bot's PR, a docs-only change), one `### Changed` bullet from its PR
//   title;
// - its version is next-version.mjs's computation over the version file at that merge and the tags
//   so far, and its date is the merge's.
//
// It writes the new sections below `## [Unreleased]`, newest first, keeping a link footer current,
// deletes the fragments it used, and makes one commit, `chore(release): v<x.y.z>[, ...] [skip ci]`
// with a `Stamped-by: stamp-changelog` trailer. It tags each merge commit, not the stamp commit, so
// a tag is exactly the code that merge deployed. With --notes-dir it writes each section's body to
// `<dir>/v<x.y.z>.md` for the release notes. It prints one `v<x.y.z> <merge sha>` line per tag.
// --dry-run prints the sections and changes nothing.
//
// It pushes nothing. The workflow pushes the stamp commit and the tags together:
//
//   git push --atomic origin HEAD:main <each tag>
//
// with a token whose app may bypass the main ruleset; `GITHUB_TOKEN` cannot, and `[skip ci]` keeps
// the app's push from starting workflows. Give the job a `concurrency` group that never cancels, so
// runs take turns. A push rejected as non-fast-forward means another merge landed meanwhile; its
// own run starts from the new tip and stamps both, so the workflow stops without failing. Any
// other rejection (a tag that already exists, a ruleset refusal) fails the run.
//
// Every first-parent commit is one release, so the repository merges with merge commits or squash
// merges, never rebase merging, and nobody pushes several commits straight to main. A malformed
// fragment (one merged past the changelog check) never blocks a run: its lines are released under
// `### Changed` with a warning. On moving to this scheme, the newest three-part tag must be on the
// last merge before the move, or every merge since that tag is released on the first run.
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { spawnSync } from "node:child_process";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

import {
  CATEGORY_ORDER,
  documentedVersions,
  isFragmentPath,
  parseFragment,
} from "./check-changelog.mjs";
import {
  computeNextVersion,
  parseDeclaredVersion,
  refreshTags,
  repositoryRoot,
  versionFilePath,
} from "./next-version.mjs";

const CHANGELOG = "CHANGELOG.md";
export const TRAILER = "Stamped-by: stamp-changelog";

export function parseArgs(argv) {
  const options = { dryRun: false, notesDir: null };
  for (let index = 0; index < argv.length; index += 1) {
    const argument = argv[index];
    if (argument === "--dry-run") {
      options.dryRun = true;
    } else if (argument === "--notes-dir") {
      const value = argv[index + 1];
      if (!value) throw new Error("--notes-dir requires a value.");
      options.notesDir = value;
      index += 1;
    } else {
      throw new Error(`Unknown argument: ${argument}`);
    }
  }
  return options;
}

const STAMP_SUBJECT = /^chore\(release\): v\d+\.\d+\.\d+(?:, v\d+\.\d+\.\d+)* \[skip ci\]$/;

// A stamp commit has this script's subject and ends in its trailer. A squash commit that merely
// quotes a stamp commit's message in its body is neither.
export function isStampCommit(message) {
  const lines = message.replace(/\r\n?/g, "\n").trim().split("\n");
  return STAMP_SUBJECT.test(lines[0].trim()) && lines.at(-1).trim() === TRAILER;
}

// A fragment that fails to parse (merged past the changelog check) still releases: its entry lines
// go under `### Changed`, so one bad file never blocks every later merge. A later PR can tidy the
// stamped section, whose body the check lets a branch edit.
export function salvageFragment(text) {
  const lines = text
    .replace(/^\uFEFF/, "")
    .replace(/\r\n?/g, "\n")
    .split("\n")
    .filter((line) => line.trim() !== "" && !/^#{1,6}\s/.test(line));
  return lines.length > 0 ? new Map([["Changed", lines]]) : null;
}

// The entry for a merge that added no fragment, from its PR title: GitHub's merge commit names the
// PR in its subject and carries the title as its body; a squash merge's subject is the title
// followed by `(#N)`.
export function prTitleEntry(message) {
  const [subject = "", ...rest] = message.replace(/\r\n?/g, "\n").trim().split("\n");
  const merge = /^Merge pull request #(\d+) from \S+/.exec(subject);
  if (merge) {
    const title = rest.find((line) => line.trim() !== "")?.trim();
    return `- ${title ?? subject} (#${merge[1]})`;
  }
  return `- ${subject.trim()}`;
}

// Merges parsed fragments' categories into one, Keep a Changelog's order first.
export function mergeCategories(parsed) {
  const merged = new Map();
  for (const categories of parsed) {
    for (const [name, lines] of categories) {
      if (!merged.has(name)) merged.set(name, []);
      merged.get(name).push(...lines);
    }
  }
  const rank = (name) => {
    const at = CATEGORY_ORDER.indexOf(name);
    return at === -1 ? CATEGORY_ORDER.length : at;
  };
  return new Map([...merged].sort(([a], [b]) => rank(a) - rank(b)));
}

export function renderBody(categories) {
  return [...categories]
    .map(([name, lines]) => `### ${name}\n\n${lines.join("\n")}`)
    .join("\n\n");
}

export function renderSection({ version, date, categories }) {
  return `## [${version}] - ${date}\n\n${renderBody(categories)}\n`;
}

// Inserts sections (newest first, each ending in a newline) after the `## [Unreleased]` block, or
// before the first version section when there is no Unreleased heading.
export function insertSections(changelog, sections) {
  const eol = changelog.includes("\r\n") ? "\r\n" : "\n";
  const lines = changelog.replace(/\r\n?/g, "\n").split("\n");
  const unreleased = lines.findIndex((line) => /^## \[Unreleased\]/.test(line));
  const from = unreleased === -1 ? 0 : unreleased + 1;
  let at = lines.findIndex((line, index) => index >= from && /^## /.test(line));
  if (at === -1) {
    at = lines.findIndex((line, index) => index >= from && /^\[[^\]]+\]: \S/.test(line));
  }
  if (at === -1) at = lines.length;

  const before = lines.slice(0, at);
  while (before.length > 0 && before.at(-1).trim() === "") before.pop();
  const after = lines.slice(at);
  const block = sections.map((section) => section.trimEnd()).join("\n\n");
  const text = [before.join("\n"), block, after.join("\n")]
    .filter((part) => part !== "")
    .join("\n\n");
  const ending = changelog.endsWith("\n") || changelog === "" ? "\n" : "";
  return `${text.trimEnd()}${ending}`.replace(/\n/g, eol);
}

// Moves a link footer's `[Unreleased]` comparison to the newest version and adds a comparison link
// for each new one. A file with no `[Unreleased]` link has no footer to keep.
export function updateFooter(changelog, versionsOldestFirst) {
  const eol = changelog.includes("\r\n") ? "\r\n" : "\n";
  const text = changelog.replace(/\r\n?/g, "\n");
  const link = /^\[Unreleased\]: (\S*)\/compare\/v(\d+\.\d+\.\d+)\.\.\.HEAD[ \t]*$/m.exec(text);
  if (!link || versionsOldestFirst.length === 0) return changelog;
  const [, url, previous] = link;
  const added = [];
  let from = previous;
  for (const version of versionsOldestFirst) {
    added.unshift(`[${version}]: ${url}/compare/v${from}...v${version}`);
    from = version;
  }
  const replaced = text.replace(
    link[0],
    [`[Unreleased]: ${url}/compare/v${from}...HEAD`, ...added].join("\n"),
  );
  return replaced.replace(/\n/g, eol);
}

// Fragment names are passed back to git, so pathspecs are literal (a `[ab]` or `*` in a name
// matches only itself), and paths print unquoted.
function git(root, args, options = {}) {
  const flags = ["--literal-pathspecs", "-c", "core.quotePath=false", "-C", root];
  const result = spawnSync("git", [...flags, ...args], {
    encoding: "utf8",
    windowsHide: true,
    maxBuffer: 64 * 1024 * 1024,
    ...options,
  });
  if (result.status !== 0) {
    const reason = (result.stderr || result.error || "").toString().trim();
    throw new Error(`git ${args.join(" ")} failed: ${reason}`);
  }
  return result.stdout ?? "";
}

// Commits that three-part tags point at, peeled, and every tag name.
function readTags(root) {
  const tagged = new Set();
  const names = [];
  const refs = git(root, [
    "for-each-ref",
    "refs/tags",
    "--format=%(refname:short)%09%(objectname)%09%(*objectname)",
  ]);
  for (const line of refs.split(/\r?\n/).filter(Boolean)) {
    const [name, object, peeled] = line.split("\t");
    names.push(name);
    if (/^v\d+\.\d+\.\d+$/.test(name)) tagged.add(peeled || object);
  }
  return { tagged, names };
}

// The untagged first-parent commits since the newest tagged one, oldest first, without stamp
// commits.
export function untaggedMerges(root, tagged) {
  const pending = [];
  const history = git(root, ["rev-list", "--first-parent", "HEAD"]).split(/\r?\n/).filter(Boolean);
  for (const sha of history) {
    if (tagged.has(sha)) return pending.reverse();
    const message = git(root, ["log", "-1", "--format=%B", sha]);
    if (!isStampCommit(message)) pending.push({ sha, message });
  }
  throw new Error(
    "No three-part tag on main's first-parent history. Tag the last release before switching to " +
      "stamp-on-tag.",
  );
}

function fragmentsAdded(root, sha) {
  const parents = git(root, ["log", "-1", "--format=%P", sha]).trim().split(/\s+/).filter(Boolean);
  if (parents.length === 0) return [];
  const args = ["diff", "--name-only", "-z", "--no-renames", "--diff-filter=A", parents[0], sha];
  return git(root, [...args, "--", "changelog.d"])
    .split("\0")
    .filter(isFragmentPath)
    .sort();
}

// Works out each untagged merge's version, date and section without changing anything.
export function plan(root) {
  const { tagged, names } = readTags(root);
  const versionFile = versionFilePath(root);
  const tags = [...names];
  const releases = [];
  for (const { sha, message } of untaggedMerges(root, tagged)) {
    const files = fragmentsAdded(root, sha);
    const parsed = files.flatMap((file) => {
      const text = git(root, ["show", `${sha}:${file}`]);
      const { categories, errors } = parseFragment(text);
      if (errors.length === 0) return [categories];
      console.error(
        `::warning file=${file}::${file} at ${sha.slice(0, 12)} is malformed ` +
          `(${errors.join(" ")}); its lines are released under ### Changed.`,
      );
      return [salvageFragment(text)].filter(Boolean);
    });
    const categories =
      parsed.length > 0 ? mergeCategories(parsed) : new Map([["Changed", [prTitleEntry(message)]]]);
    const atMerge = git(root, ["show", `${sha}:${versionFile}`]);
    const declared = parseDeclaredVersion(versionFile, atMerge);
    const version = computeNextVersion(declared, tags);
    tags.push(`v${version}`);
    const date = git(root, ["log", "-1", "--format=%cs", sha]).trim();
    releases.push({ sha, version, date, categories, files });
  }
  return releases;
}

export function main(argv, root = repositoryRoot) {
  const options = parseArgs(argv);
  refreshTags(root);
  const releases = plan(root);
  if (releases.length === 0) {
    console.error("stamp-changelog: every merge on main is tagged; nothing to stamp.");
    return 0;
  }

  const file = path.join(root, CHANGELOG);
  const changelog = readFileSync(file, "utf8");
  const sections = releases.map(renderSection).reverse();
  const known = new Set(documentedVersions(changelog));
  const clash = releases.find(({ version }) => known.has(version));
  if (clash) throw new Error(`CHANGELOG.md already has a [${clash.version}] section.`);

  if (options.dryRun) {
    for (const { sha, version } of releases) console.log(`v${version} ${sha}`);
    console.log(`\n${sections.join("\n")}`);
    return 0;
  }

  const inserted = insertSections(changelog, sections);
  const stamped = updateFooter(
    inserted,
    releases.map(({ version }) => version),
  );
  if (stamped === inserted && /^\[Unreleased\]:/m.test(changelog)) {
    console.error(
      "::warning file=CHANGELOG.md::The [Unreleased] link does not compare v<x.y.z>...HEAD, so " +
        "the link footer was left as it was.",
    );
  }
  writeFileSync(file, stamped);

  // With no paths, `git ls-files --` lists every file: only ask about fragments actually used.
  const used = [...new Set(releases.flatMap(({ files }) => files))];
  const present = used.length > 0 ? git(root, ["ls-files", "-z", "--", ...used]).split("\0") : [];
  const stale = present.filter((file) => used.includes(file));
  if (stale.length > 0) git(root, ["rm", "-q", "--", ...stale]);
  git(root, ["add", "--", CHANGELOG]);

  const versions = releases.map(({ version }) => `v${version}`).join(", ");
  git(root, ["commit", "-q", "-m", `chore(release): ${versions} [skip ci]`, "-m", TRAILER]);

  for (const release of releases) {
    const tag = `v${release.version}`;
    git(root, ["tag", "-a", tag, release.sha, "-m", `Release ${tag}`]);
    if (options.notesDir) {
      mkdirSync(options.notesDir, { recursive: true });
      const notes = `${renderBody(release.categories)}\n`;
      writeFileSync(path.join(options.notesDir, `${tag}.md`), notes);
    }
    console.log(`${tag} ${release.sha}`);
  }
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  try {
    process.exitCode = main(process.argv.slice(2));
  } catch (error) {
    console.error(`stamp-changelog: ${error instanceof Error ? error.message : error}`);
    process.exitCode = 1;
  }
}
