#!/usr/bin/env node

/**
 * Renders a coverage summary as one markdown table, a row per module (ADR-055,
 * docs/testing/coverage.md). It is what each coverage step appends to the job summary.
 *
 * Two inputs, one table shape:
 *
 *   dotnet  ReportGenerator's `JsonSummary` file (`Summary.json`). A row per product assembly.
 *   web     An Istanbul `json-summary` file (`coverage-summary.json`), which both web collections
 *           write: Vitest for the unit suite, monocart-coverage-reports for the Playwright run.
 *           A row per area, an area being the first directory under `web/src`.
 *
 * Usage:
 *
 *   node scripts/coverage-summary.mjs dotnet --summary <Summary.json> --title <text> [--out-md <file>] [--out-json <file>]
 *   node scripts/coverage-summary.mjs web --summary <coverage-summary.json> --title <text> [--out-md <file>] [--out-json <file>]
 *
 * The markdown goes to standard output unless `--out-md` names a file.
 *
 * Measurement only. The exit code is non-zero when a summary is missing, empty, or lacks a product
 * assembly, because each of those is a collection that did not happen. It is never non-zero because
 * of a figure.
 */

import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

/**
 * The product assemblies, in the order the table shows them. A report without one of them is a
 * broken collection (a filter that stopped matching, an assembly that was never loaded), and it
 * fails. An assembly that is not listed here still gets a row.
 */
export const PRODUCT_ASSEMBLIES = [
  ["LeaseBook.Modules.Accounting", "Accounting"],
  ["LeaseBook.Modules.Directory", "Directory"],
  ["LeaseBook.Modules.Banking", "Banking"],
  ["LeaseBook.Modules.Reporting", "Reporting"],
  ["LeaseBook.Modules.Operations", "Operations"],
  ["LeaseBook.Modules.Capabilities", "Capabilities"],
  ["LeaseBook.Modules.Payments", "Payments"],
  ["LeaseBook.SharedKernel", "SharedKernel"],
  ["LeaseBook.Migrator", "Migrator"],
  ["LeaseBook.Web", "Web host"],
];

const pair = (covered, total) => ({ covered, total });

/** Rows from ReportGenerator's `Summary.json`: one per assembly, product assemblies first. */
export function summariseDotnet(document) {
  const assemblies = document?.coverage?.assemblies ?? [];
  const byName = new Map(assemblies.map((entry) => [entry.name, entry]));
  const missing = PRODUCT_ASSEMBLIES.filter(([name]) => !byName.has(name)).map(
    ([name]) => name,
  );
  if (missing.length > 0) {
    throw new Error(
      `The coverage report has no entry for: ${missing.join(", ")}.`,
    );
  }
  const known = new Set(PRODUCT_ASSEMBLIES.map(([name]) => name));
  const ordered = [
    ...PRODUCT_ASSEMBLIES,
    ...assemblies
      .filter((entry) => !known.has(entry.name))
      .map((entry) => [entry.name, entry.name]),
  ];
  const rows = ordered.map(([name, label]) => {
    const entry = byName.get(name);
    return {
      name: label,
      count: entry.classes,
      lines: pair(entry.coveredlines, entry.coverablelines),
      branches: pair(entry.coveredbranches ?? 0, entry.totalbranches ?? 0),
    };
  });
  return { unit: "Module", countLabel: "Classes", rows, total: totalOf(rows) };
}

/** `src/features/x/Y.tsx` → `features`; a file directly under `src` → `(src root)`. */
export function areaOf(filePath) {
  const normalised = filePath.replaceAll("\\", "/");
  const index = normalised.lastIndexOf("/src/");
  const relative =
    index >= 0
      ? normalised.slice(index + "/src/".length)
      : normalised.replace(/^src\//, "");
  const parts = relative.split("/");
  return parts.length > 1 ? parts[0] : "(src root)";
}

/** Rows from an Istanbul `json-summary` document: its per-file entries summed into areas. */
export function summariseWeb(document) {
  const areas = new Map();
  for (const [file, entry] of Object.entries(document)) {
    if (file === "total") continue;
    const name = areaOf(file);
    const area = areas.get(name) ?? {
      name,
      count: 0,
      lines: pair(0, 0),
      branches: pair(0, 0),
    };
    area.count += 1;
    for (const metric of ["lines", "branches"]) {
      area[metric].covered += entry[metric]?.covered ?? 0;
      area[metric].total += entry[metric]?.total ?? 0;
    }
    areas.set(name, area);
  }
  const rows = [...areas.values()].sort((a, b) => a.name.localeCompare(b.name));
  if (rows.length === 0) {
    throw new Error("The coverage summary holds no source files.");
  }
  return { unit: "Area", countLabel: "Files", rows, total: totalOf(rows) };
}

// Summed from the rows, so the total is the total of what the table shows.
function totalOf(rows) {
  const total = {
    name: "Total",
    count: 0,
    lines: pair(0, 0),
    branches: pair(0, 0),
  };
  for (const row of rows) {
    total.count += row.count;
    for (const metric of ["lines", "branches"]) {
      total[metric].covered += row[metric].covered;
      total[metric].total += row[metric].total;
    }
  }
  return total;
}

/** A percentage to one decimal place, or `n/a` when there is nothing to cover. */
export function percent({ covered, total }) {
  return total === 0 ? "n/a" : `${((covered / total) * 100).toFixed(1)}%`;
}

export function renderMarkdown({ unit, countLabel, rows, total }, title) {
  const line = (row, bold = false) => {
    const cell = (text) => (bold ? `**${text}**` : `${text}`);
    return (
      `| ${cell(row.name)} | ${cell(row.count)} ` +
      `| ${cell(`${row.lines.covered} / ${row.lines.total}`)} | ${cell(percent(row.lines))} ` +
      `| ${cell(`${row.branches.covered} / ${row.branches.total}`)} | ${cell(percent(row.branches))} |`
    );
  };
  return [
    `## ${title}`,
    "",
    `| ${unit} | ${countLabel} | Lines covered | Line | Branches covered | Branch |`,
    "| :-- | --: | --: | --: | --: | --: |",
    ...rows.map((row) => line(row)),
    line(total, true),
    "",
  ].join("\n");
}

export function parseArgs(argv) {
  const usage =
    "Usage: coverage-summary.mjs <dotnet|web> --summary <file> --title <text> [--out-md <file>] [--out-json <file>]";
  const [kind, ...rest] = argv;
  if (kind !== "dotnet" && kind !== "web") throw new Error(usage);
  const names = {
    "--summary": "summary",
    "--title": "title",
    "--out-md": "outMd",
    "--out-json": "outJson",
  };
  const options = { kind };
  for (let index = 0; index < rest.length; index += 2) {
    const name = names[rest[index]];
    const value = rest[index + 1];
    if (!name || value === undefined) {
      throw new Error(`Unknown or incomplete argument: ${rest[index]}`);
    }
    options[name] = value;
  }
  if (!options.summary || !options.title) throw new Error(usage);
  return options;
}

export function run(argv) {
  const options = parseArgs(argv);
  const document = JSON.parse(readFileSync(options.summary, "utf8"));
  const result =
    options.kind === "dotnet"
      ? summariseDotnet(document)
      : summariseWeb(document);
  const markdown = renderMarkdown(result, options.title);
  for (const [target, content] of [
    [options.outMd, markdown],
    [options.outJson, `${JSON.stringify(result, null, 2)}\n`],
  ]) {
    if (!target) continue;
    mkdirSync(path.dirname(path.resolve(target)), { recursive: true });
    writeFileSync(target, content);
  }
  return { markdown, outMd: options.outMd };
}

if (
  process.argv[1] &&
  import.meta.url === pathToFileURL(process.argv[1]).href
) {
  try {
    const { markdown, outMd } = run(process.argv.slice(2));
    if (!outMd) process.stdout.write(markdown);
  } catch (error) {
    console.error(error instanceof Error ? error.message : error);
    process.exit(1);
  }
}
