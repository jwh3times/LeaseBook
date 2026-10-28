#!/usr/bin/env node
// Tool-neutrality lint for the authored agent and skill sources.
//
// `scripts/sync-agents.mjs` copies prose verbatim into the other tool's tree
// (.claude/agents/*.md -> .codex/agents/*.toml, .agents/skills/** -> .claude/skills/**), so the
// sources must read correctly from both locations. Three checks catch the ways that has gone wrong:
//
//  1. Blind-substitution artifacts — a global `claude`->`Codex` replace (how the original Codex
//     files were made) produces wrong-case paths such as `.Codex/` and dead `code.Codex.com` links.
//  2. Depth-fragile relative links — a Markdown link that resolves to a real file from the source
//     directory but to nothing from the mirrored directory (e.g. `../skills/...` from
//     `.claude/agents/` points into `.codex/skills/`, which does not exist). Links that land in the
//     same place from both locations pass, so a working link never false-positives.
//  3. Bare positional parameters in a skill body — Claude Code replaces `$0`-`$9` in a loaded
//     SKILL.md with the invocation's argument words, so a shell snippet's `"$3"` silently becomes
//     an argument (apexracers#451). Braced `${1:-x}` survives, as do files a skill only references.
//
// The same checks cover `skills/`, where jwh3times/skills publishes skills that install as
// .agents/skills/<name>/ elsewhere. A relative link there must stay inside its own skill folder.
//
//   node scripts/lint-agent-neutrality.mjs          # exit 1 and list every problem found
//   node scripts/lint-agent-neutrality.mjs <root>   # lint another checkout instead
//
// Without <root> it lints the repository this copy of the script lives in, not the working
// directory, and names the root it linted either way.

import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const scriptPath = fileURLToPath(import.meta.url);
const defaultRoot = path.resolve(path.dirname(scriptPath), "..");

// [authored dir, mirrored dir, whether the mirror carries the same files]
const trees = [
  [".claude/agents", ".codex/agents", false],
  [".agents/skills", ".claude/skills", true],
];
const textExtensions = new Set([".md", ".yaml", ".yml", ".sh", ".txt", ".json", ".toml"]);
const substitutionArtifacts = [/\.Codex\//, /code\.Codex\.com/];
const bareParameter = /\$\d/g;
const publishedDir = "skills";

function listFiles(root, dir) {
  const absolute = path.join(root, dir);
  if (!existsSync(absolute)) return [];
  const results = [];
  for (const entry of readdirSync(absolute, { withFileTypes: true })) {
    const rel = `${dir}/${entry.name}`;
    if (entry.isDirectory()) results.push(...listFiles(root, rel));
    else if (entry.isFile()) results.push(rel);
  }
  // An explicit code-unit comparator: the default order, which type-aware linters require spelled out.
  return results.sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
}

// True when `rel` (POSIX, repo-relative) will exist once the generator has run. A path inside a
// wholly mirrored tree exists exactly when its authored counterpart does.
function existsAfterSync(root, rel) {
  for (const [source, target, wholeTree] of trees) {
    if (wholeTree && rel.startsWith(`${target}/`)) {
      return existsSync(path.join(root, source + rel.slice(target.length)));
    }
  }
  return existsSync(path.join(root, rel));
}

function lintContent(file, content, isSkillTree, problems) {
  for (const artifact of substitutionArtifacts) {
    const match = content.match(artifact);
    if (match) {
      problems.push(
        `${file}: contains "${match[0]}" — a blind claude->Codex substitution artifact; keep the source tool-neutral`,
      );
    }
  }

  if (isSkillTree && path.posix.basename(file) === "SKILL.md") {
    content.split("\n").forEach((line, index) => {
      for (const [parameter] of line.matchAll(bareParameter)) {
        problems.push(
          `${file}:${index + 1}: bare \`${parameter}\` is replaced by the skill's invocation arguments when loaded — use a named variable`,
        );
      }
    });
  }
}

export function lintNeutrality(root = defaultRoot) {
  const problems = [];
  for (const [source, target, wholeTree] of trees) {
    for (const file of listFiles(root, source)) {
      const ext = path.extname(file).toLowerCase();
      if (wholeTree ? !textExtensions.has(ext) : ext !== ".md") continue;
      const content = readFileSync(path.join(root, file), "utf8");
      lintContent(file, content, wholeTree, problems);

      const sourceDir = path.posix.dirname(file);
      const mirrorDir = target + sourceDir.slice(source.length);
      for (const [, link] of content.matchAll(/\]\((\.\.?\/[^)\s]+)\)/g)) {
        const bare = link.replace(/#.*$/, "");
        const fromSource = path.posix.join(sourceDir, bare);
        const fromMirror = path.posix.join(mirrorDir, bare);
        if (existsSync(path.join(root, fromSource)) && !existsAfterSync(root, fromMirror)) {
          problems.push(
            `${file}: relative link \`${link}\` resolves here but breaks in the mirrored copy under ${target} — write it as a plain repo-root-relative path`,
          );
        }
      }
    }
  }

  for (const file of listFiles(root, publishedDir)) {
    if (!textExtensions.has(path.extname(file).toLowerCase())) continue;
    const content = readFileSync(path.join(root, file), "utf8");
    lintContent(file, content, true, problems);

    const skillDir = file.split("/").slice(0, 2).join("/");
    for (const [, link] of content.matchAll(/\]\((\.\.?\/[^)\s]+)\)/g)) {
      const resolved = path.posix.join(path.posix.dirname(file), link.replace(/#.*$/, ""));
      if (!resolved.startsWith(`${skillDir}/`)) {
        problems.push(
          `${file}: relative link \`${link}\` leaves ${skillDir}/, which installs elsewhere — write it as a plain repo-root-relative path`,
        );
      }
    }
  }
  return problems;
}

if (process.argv[1] && path.resolve(process.argv[1]) === scriptPath) {
  const root = process.argv[2] ? path.resolve(process.argv[2]) : defaultRoot;
  const problems = lintNeutrality(root);
  if (problems.length) {
    console.error(`Agent tool-neutrality lint failed in ${root}:`);
    for (const problem of problems) console.error(`  ${problem}`);
    process.exitCode = 1;
  } else {
    console.log(`Agent and skill sources in ${root} are tool-neutral.`);
  }
}
