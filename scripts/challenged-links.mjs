#!/usr/bin/env node

/**
 * Links the link check is refused by a site's bot challenge (#665). `.github/workflows/docs-links.yml`
 * excludes them on every event, and this script is the signal that ends the exclude: the weekly sweep
 * asks lychee about them and `check` fails once lychee stops being refused. The workflow takes both
 * the exclude and the links it asks about from the list below, so the two cannot disagree.
 *
 * Usage (from the repository root):
 *
 *   node scripts/challenged-links.mjs pattern
 *   node scripts/challenged-links.mjs urls
 *   node scripts/challenged-links.mjs check <report.json> [url...]
 *
 * `pattern` prints the regular expression lychee excludes. `urls` prints the links, one per line, for
 * lychee to read from a file: given a URL as an argument, lychee fetches it as a page to find links
 * in instead of checking it. `check` reads the report of `lychee --format json --verbose`. Only
 * lychee's own answer counts, because the challenge treats clients differently: on 2026-10-31 the
 * same laptop got 200 through lychee and 403 through Node's fetch. Each link's answer means:
 *
 *   - 403: still refused, which passes.
 *   - 2xx, after redirects: the challenge has lifted. Fails: remove the exclude and re-read the page.
 *   - 404 or 410: the page has gone. Fails: the citation must be updated.
 *   - Any other status, a timeout or a network failure: says nothing about the page. Logged, and
 *     passes. A challenge can itself answer 503, and one bad night must not read as a broken citation.
 *   - No answer in the report: lychee did not ask. Fails, since nothing was checked.
 *
 * The exit code is 0 when every link passes, 1 when one fails, and 2 on a usage error or a report
 * that cannot be read. Passing URLs replaces the list, which is how the tests and a by-hand run try
 * the logic on another address.
 */

import { readFileSync } from "node:fs";
import { pathToFileURL } from "node:url";

// None at present. IDAutomation's two pages, cited by docs/research/micr-e13b-refund-checks.md,
// answered 403 to lychee on 2026-10-31 and 200 on 2026-11-01 (#665).
const LINKS = [];

const USAGE =
  "Usage: node scripts/challenged-links.mjs pattern | urls | check <report.json> [url...]";

/**
 * An empty pattern would exclude every link and pass, so an empty list gives the never-matching
 * pattern the workflow's other excludes use.
 */
export function excludePattern(links) {
  if (links.length === 0) return "^$";
  return links
    .map((url) => `^${url.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}$`)
    .join("|");
}

/** Lychee files each link under the input it came from, in a map by what happened to it. */
function answersIn(report) {
  const answers = new Map();
  for (const map of [
    report.success_map,
    report.error_map,
    report.timeout_map,
  ]) {
    for (const entries of Object.values(map ?? {})) {
      for (const { url, status } of entries) answers.set(url, status);
    }
  }
  return answers;
}

function passes(url, status) {
  if (status === undefined) {
    console.log(
      `::error::${url}: the report has no answer for it, so nothing was checked.`,
    );
    return false;
  }

  const { code, text } = status;
  if (code === 403) {
    console.log(`${url}: 403, lychee is still refused.`);
    return true;
  }
  if (code >= 200 && code < 300) {
    console.log(
      `::error::${url}: answered ${code}, so lychee is no longer refused. Remove it from scripts/challenged-links.mjs and re-read the page against what the citing document quotes.`,
    );
    return false;
  }
  if (code === 404 || code === 410) {
    console.log(
      `::error::${url}: answered ${code}, so the citation has gone. Update the citing document and scripts/challenged-links.mjs.`,
    );
    return false;
  }
  console.log(`::warning::${url}: ${text}. Nothing to conclude.`);
  return true;
}

function check([reportPath, ...urls]) {
  if (!reportPath || [reportPath, ...urls].some((a) => a.startsWith("-"))) {
    console.error(USAGE);
    return 2;
  }

  let answers;
  try {
    answers = answersIn(JSON.parse(readFileSync(reportPath, "utf8")));
  } catch (error) {
    console.error(
      `::error::Cannot read lychee's report ${reportPath}: ${error.message}`,
    );
    return 2;
  }

  const results = (urls.length > 0 ? urls : LINKS).map((url) =>
    passes(url, answers.get(url)),
  );
  return results.every(Boolean) ? 0 : 1;
}

function main([command, ...args]) {
  if (command === "pattern" && args.length === 0) {
    console.log(excludePattern(LINKS));
    return 0;
  }
  if (command === "urls" && args.length === 0) {
    console.log(LINKS.join("\n"));
    return 0;
  }
  if (command === "check") {
    return check(args);
  }
  console.error(USAGE);
  return 2;
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  process.exitCode = main(process.argv.slice(2));
}
