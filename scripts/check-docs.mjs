#!/usr/bin/env node
// Checks the repository's Markdown against its files and its Doc map:
//
//  1. Every relative link and image in a document resolves to a tracked file or folder, in the file's
//     exact case, inside the repository, and every fragment into a Markdown file names a heading or
//     an explicit anchor. A target that is ignored (the companion, build output) is reported: it
//     resolves on one machine and breaks on a clean clone.
//  2. Every document is listed in the `Doc map` of `docs/agents/docs.md`, so docs-updater knows it
//     exists, and every path the map lists still exists. An item covers a document by exact path, by
//     folder, or by glob. Companion paths and URLs are not this repository's to check.
//
// Installed skills and their mirrors are out of scope: a reinstall overwrites them, and
// `scripts/lint-agent-neutrality.mjs` checks their links.
//
//   node scripts/check-docs.mjs          # exit 1 and list every problem found
//   node scripts/check-docs.mjs <root>   # check another checkout instead
//
// This repository holds the canonical copy (jwh3times/skills); every consumer carries it unchanged.

import { execFileSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const DOC_MAP_FILE = "docs/agents/docs.md";
const OUT_OF_SCOPE = [".agents/skills/", ".claude/skills/", ".codex/"];

const FENCE = /^ {0,3}(`{3,}|~{3,})(.*)$/;
const ATX_HEADING = /^ {0,3}#{1,6}(?:[ \t]+(.*?))?(?:[ \t]+#+)?[ \t]*$/;
const SETEXT_UNDERLINE = /^ {0,3}(=+|-+)[ \t]*$/;
const NOT_A_SETEXT_TITLE = /^ {0,3}(?:[-*+>|]|\d+[.)])/;
const HTML_ANCHOR = /<[a-zA-Z][^>]*\s(?:id|name)=["']([^"']+)["']/g;
const LINE_FRAGMENT = /^L\d+(?:-L?\d+)?$/;

// The file's lines with front matter and fenced code blanked, so positions stay true. A fence closes
// only on the same character, at least as long, with nothing after it.
export function visibleLines(markdown) {
  const lines = markdown.split(/\r?\n/);
  const visible = [...lines];
  if (lines[0] === "---") {
    const end = lines.indexOf("---", 1);
    if (end > 0) visible.fill("", 0, end + 1);
  }
  let fence = null;
  for (let index = 0; index < lines.length; index += 1) {
    if (visible[index] === "" && lines[index] !== "") continue;
    const match = FENCE.exec(lines[index]);
    if (fence === null) {
      if (match) {
        fence = match[1];
        visible[index] = "";
      }
      continue;
    }
    visible[index] = "";
    if (
      match &&
      match[1][0] === fence[0] &&
      match[1].length >= fence.length &&
      match[2].trim() === ""
    ) {
      fence = null;
    }
  }
  return visible;
}

function withoutInlineCode(line) {
  return line.replace(/(`+)(?:(?!\1).)*?\1/g, (span) => " ".repeat(span.length));
}

// Every link and image target on a visible line, inline or a reference definition.
export function lineTargets(line) {
  const text = withoutInlineCode(line);
  const targets = [];
  for (const match of text.matchAll(/\]\(\s*(<[^>]+>|[^\s)]+)(?:\s+["'(][^)]*)?\)/g)) {
    targets.push(match[1]);
  }
  const reference = /^ {0,3}\[[^\]]+\]:\s*(<[^>]+>|\S+)/.exec(text);
  if (reference) targets.push(reference[1]);
  return targets.map((target) => target.replace(/^<|>$/g, ""));
}

// The slug GitHub gives a heading: inline Markdown rendered away, lowercased, everything but letters,
// digits, marks, spaces, hyphens and underscores removed, spaces to hyphens.
export function headingSlug(heading) {
  return heading
    .replace(/!\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[([^\]]*)\]\([^)]*\)/g, "$1")
    .replace(/\[([^\]]*)\]\[[^\]]*\]/g, "$1")
    .replace(/`([^`]*)`/g, "$1")
    .replace(/<[^>]+>/g, "")
    .replace(/(^|[^\p{L}\p{N}_])_([^_]+)_(?=[^\p{L}\p{N}_]|$)/gu, "$1$2")
    .trim()
    .toLowerCase()
    .replace(/[^\p{L}\p{N}\p{M} _-]/gu, "")
    .replace(/ /g, "-");
}

// Every fragment a Markdown file answers to. A repeated slug is numbered -1, -2, ...
export function anchorsOf(markdown) {
  const anchors = new Set();
  const seen = new Map();
  const lines = visibleLines(markdown);
  for (const [index, line] of lines.entries()) {
    for (const match of line.matchAll(HTML_ANCHOR)) anchors.add(match[1]);

    let heading = ATX_HEADING.exec(line)?.[1];
    if (ATX_HEADING.test(line)) heading ??= "";
    else if (
      line.trim() &&
      SETEXT_UNDERLINE.test(lines[index + 1] ?? "") &&
      !SETEXT_UNDERLINE.test(line) &&
      !NOT_A_SETEXT_TITLE.test(line)
    ) {
      heading = line.trim();
    }
    if (heading === undefined) continue;

    const slug = headingSlug(heading);
    const count = seen.get(slug) ?? 0;
    seen.set(slug, count + 1);
    anchors.add(count === 0 ? slug : `${slug}-${count}`);
  }
  return anchors;
}

function decode(value) {
  try {
    return decodeURIComponent(value);
  } catch {
    return null;
  }
}

// Problems with one target in `file`, given the repository's files and folders (POSIX, exact case).
function checkTarget(target, file, repository) {
  if (/^[A-Za-z]:[\\/]/.test(target)) return `a local absolute path: ${target}`;
  if (/^(?:[a-z][a-z0-9+.-]*:|\/\/)/i.test(target)) return null;

  const hashAt = target.indexOf("#");
  const rawPath = (hashAt === -1 ? target : target.slice(0, hashAt)).split("?", 1)[0];
  const decodedPath = decode(rawPath);
  const fragment = decode(hashAt === -1 ? "" : target.slice(hashAt + 1));
  if (decodedPath === null || fragment === null) return `invalid URL encoding: ${target}`;

  const resolved = decodedPath
    ? decodedPath.startsWith("/")
      ? path.posix.normalize(decodedPath.slice(1))
      : path.posix.normalize(path.posix.join(path.posix.dirname(file), decodedPath))
    : file;
  const clean = resolved.replace(/\/$/, "");

  if (clean === ".." || clean.startsWith("../")) return `escapes the repository: ${target}`;
  if (clean === "." || clean === "") return null;
  if (!repository.files.has(clean) && !repository.folders.has(clean)) {
    return `no tracked file or folder: ${target}`;
  }

  if (!fragment || LINE_FRAGMENT.test(fragment)) return null;
  if (!clean.toLowerCase().endsWith(".md") || !repository.files.has(clean)) return null;
  const anchors = repository.anchors(clean);
  if (anchors.has(fragment) || anchors.has(fragment.toLowerCase())) return null;
  return `no heading or anchor '#${fragment}' in ${clean}: ${target}`;
}

// The text under `## Doc map`, or null when it is absent.
export function docMapBody(settings) {
  const lines = settings.split(/\r?\n/);
  const start = lines.findIndex((line) => line.trim() === "## Doc map");
  if (start === -1) return null;
  const end = lines.findIndex((line, index) => index > start && line.startsWith("## "));
  return lines.slice(start + 1, end === -1 ? undefined : end).join("\n");
}

// The paths and globs each top-level item of the Doc map opens with: the run of backticked names at
// its start. A nested bullet or a prose mention lists nothing, so it cannot satisfy coverage.
export function docMapPaths(body) {
  const items = [];
  for (const line of body.split("\n")) {
    if (/^[-*] /.test(line)) items.push(line.slice(2));
    else if (/^\s*[-*] /.test(line)) items.push(null);
    else if (line.trim() && items.length > 0 && items.at(-1) !== null) {
      items[items.length - 1] += ` ${line.trim()}`;
    }
  }
  return items
    .filter((item) => item !== null)
    .flatMap((item) => {
      const opening = /^(?:(?:,\s*)?(?:and\s+)?`[^`\s]+`\s*)+/.exec(item)?.[0] ?? "";
      return [...opening.matchAll(/`([^`]+)`/g)].map((match) => match[1]);
    });
}

function isCheckablePath(entry) {
  return (
    !/^[a-z][a-z0-9+.-]*:/i.test(entry) &&
    entry !== "private" &&
    !entry.startsWith("private/") &&
    !entry.startsWith("../") &&
    !entry.startsWith("~")
  );
}

function globPattern(entry) {
  const body = entry
    .replace(/[.+^$()|\\]/g, "\\$&")
    .replace(/\{([^}]*)\}/g, (_, options) => `(?:${options.split(",").join("|")})`)
    .replace(/\*\*\/?|\*|\?/g, (token) =>
      token === "?" ? "[^/]" : token === "*" ? "[^/]*" : token === "**" ? ".*" : "(?:.*/)?",
    );
  return new RegExp(`^${body}(?:/.*)?$`);
}

export function entryMatcher(rawEntry) {
  const entry = rawEntry.replace(/\/$/, "");
  if (/[*?{]/.test(entry)) {
    const pattern = globPattern(entry);
    return (file) => pattern.test(file);
  }
  return (file) => file === entry || file.startsWith(`${entry}/`);
}

export function validateRepository(root) {
  const output = execFileSync(
    "git",
    ["-C", root, "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
    { encoding: "utf8", maxBuffer: 1 << 28 },
  );
  const files = new Set(
    output.split("\0").filter((file) => file && existsSync(path.join(root, file))),
  );
  const folders = new Set();
  for (const file of files) {
    for (let parent = path.posix.dirname(file); parent !== "."; parent = path.posix.dirname(parent)) {
      folders.add(parent);
    }
  }

  const read = (file) => readFileSync(path.join(root, file), "utf8");
  const anchorCache = new Map();
  const repository = {
    files,
    folders,
    anchors(file) {
      if (!anchorCache.has(file)) anchorCache.set(file, anchorsOf(read(file)));
      return anchorCache.get(file);
    },
  };

  const documents = [...files]
    .filter((file) => file.toLowerCase().endsWith(".md"))
    .filter((file) => !OUT_OF_SCOPE.some((prefix) => file.startsWith(prefix)))
    .sort();
  const problems = [];
  let links = 0;

  for (const file of documents) {
    for (const [index, line] of visibleLines(read(file)).entries()) {
      for (const target of lineTargets(line)) {
        links += 1;
        const problem = checkTarget(target, file, repository);
        if (problem) problems.push(`${file}:${index + 1}: link target has ${problem}`);
      }
    }
  }

  const body = files.has(DOC_MAP_FILE) ? docMapBody(read(DOC_MAP_FILE)) : null;
  if (body === null) {
    problems.push(`${DOC_MAP_FILE}:1: the \`## Doc map\` setting is missing`);
  } else {
    const entries = docMapPaths(body).filter(isCheckablePath);
    const matchers = entries.map((entry) => ({ entry, matches: entryMatcher(entry) }));
    for (const file of documents) {
      if (!matchers.some(({ matches }) => matches(file))) {
        problems.push(`${DOC_MAP_FILE}:1: the Doc map does not list ${file}`);
      }
    }
    for (const { entry, matches } of matchers) {
      if (![...files].some(matches)) {
        problems.push(`${DOC_MAP_FILE}:1: the Doc map lists ${entry}, which matches no file`);
      }
    }
  }

  return { problems, documents: documents.length, links };
}

export function main(argv) {
  const root = argv[0] ? path.resolve(argv[0]) : repositoryRoot;
  const { problems, documents, links } = validateRepository(root);
  for (const problem of problems) console.error(problem);
  if (problems.length > 0) {
    console.error(`${problems.length} docs problem(s) in ${root}.`);
    return 1;
  }
  console.log(`Checked ${documents} documents and ${links} links in ${root}.`);
  return 0;
}

if (process.argv[1] && import.meta.url === pathToFileURL(path.resolve(process.argv[1])).href) {
  process.exitCode = main(process.argv.slice(2));
}
