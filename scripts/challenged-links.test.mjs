import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { after, test } from "node:test";
import { fileURLToPath } from "node:url";

import { excludePattern } from "./challenged-links.mjs";

const SCRIPT = join(
  dirname(fileURLToPath(import.meta.url)),
  "challenged-links.mjs",
);
const URL_A = "https://example.test/a";
const URL_B = "https://example.test/b";

const dir = mkdtempSync(join(tmpdir(), "challenged-links-"));
after(() => rmSync(dir, { recursive: true, force: true }));

/** Runs the script as the workflow does, so the exit code and the log are what is under test. */
function run(...args) {
  const { status, stdout, stderr } = spawnSync(
    process.execPath,
    [SCRIPT, ...args],
    { encoding: "utf8" },
  );
  return { code: status, stdout, stderr };
}

let reports = 0;

/** Checks `URL_A` against a report holding `maps`, the shape lychee 0.24.2 wrote. */
function checkReport(maps, ...urls) {
  reports += 1;
  const path = join(dir, `report-${reports}.json`);
  writeFileSync(path, JSON.stringify(maps));
  return run("check", path, ...(urls.length > 0 ? urls : [URL_A]));
}

const rejected = (code, reason, url = URL_A) => ({
  url,
  status: { text: `Rejected status code: ${code} ${reason}`, code },
});
const errors = (...entries) => ({ error_map: { "links.txt": entries } });

/** Whether a line of the log is about `url`: it opens the line, after any annotation prefix. */
const logsLink = (stdout, url) =>
  stdout
    .split("\n")
    .some((line) => line.replace(/^::\w+::/, "").startsWith(`${url}: `));

test("the exclude pattern for the real list names exactly the two links, anchored at both ends", () => {
  const { code, stdout } = run("pattern");

  assert.equal(code, 0);
  assert.equal(
    stdout.trim(),
    String.raw`^https://www\.idautomation\.com/licensing/software-license/$|^https://www\.idautomation\.com/micr-fonts/e13b$`,
  );
});

test("the links lychee is asked about are the links the exclude names", () => {
  const { code, stdout } = run("urls");

  assert.equal(code, 0);
  assert.equal(
    excludePattern(stdout.trim().split("\n")),
    run("pattern").stdout.trim(),
  );
});

test("the exclude pattern matches neither a sibling page nor a longer path", () => {
  const pattern = new RegExp(excludePattern(["https://x.example/a/b"]));

  assert.match("https://x.example/a/b", pattern);
  assert.doesNotMatch("https://x.example/a/", pattern);
  assert.doesNotMatch("https://x.example/a/b/c", pattern);
  assert.doesNotMatch("https://xxexample/a/b", pattern);
});

test("an empty list excludes nothing, because an empty pattern would exclude every link", () => {
  const pattern = new RegExp(excludePattern([]));

  assert.equal(excludePattern([]), "^$");
  assert.doesNotMatch("https://x.example/", pattern);
});

test("a link lychee is still refused by passes", () => {
  const { code, stdout } = checkReport(errors(rejected(403, "Forbidden")));

  assert.equal(code, 0);
  assert.match(stdout, /403/);
  assert.doesNotMatch(stdout, /::error::/);
});

for (const [status, text] of [
  [200, "200 OK"],
  [204, "204 No Content"],
]) {
  test(`a link answering ${status} fails, saying to remove the exclude and re-read the page`, () => {
    const { code, stdout } = checkReport({
      success_map: {
        "links.txt": [{ url: URL_A, status: { text, code: status } }],
      },
    });

    assert.equal(code, 1);
    assert.match(stdout, /::error::/);
    assert.match(stdout, /remove it from scripts\/challenged-links\.mjs/i);
    assert.match(stdout, /re-read/i);
    assert.ok(logsLink(stdout, URL_A));
  });
}

for (const [status, reason] of [
  [404, "Not Found"],
  [410, "Gone"],
]) {
  test(`a link answering ${status} fails, saying the citation has gone`, () => {
    const { code, stdout } = checkReport(errors(rejected(status, reason)));

    assert.equal(code, 1);
    assert.match(stdout, /::error::/);
    assert.match(stdout, /citation has gone/i);
    assert.match(stdout, /update/i);
    assert.doesNotMatch(stdout, /remove it/i);
  });
}

for (const [status, reason] of [
  [500, "Internal Server Error"],
  [503, "Service Unavailable"],
  [429, "Too Many Requests"],
  [401, "Unauthorized"],
]) {
  test(`a link answering ${status} does not fail, and is logged`, () => {
    const { code, stdout } = checkReport(errors(rejected(status, reason)));

    assert.equal(code, 0);
    assert.match(stdout, /::warning::/);
    assert.ok(logsLink(stdout, URL_A));
    assert.ok(stdout.includes(String(status)));
  });
}

test("a link that timed out does not fail, and is logged", () => {
  const { code, stdout } = checkReport({
    timeout_map: {
      "links.txt": [
        {
          url: URL_A,
          status: { text: "Timeout", details: "Request timed out" },
        },
      ],
    },
  });

  assert.equal(code, 0);
  assert.match(stdout, /::warning::/);
  assert.match(stdout, /Timeout/);
  assert.ok(logsLink(stdout, URL_A));
});

test("a link that could not be reached does not fail, and is logged", () => {
  const { code, stdout } = checkReport(
    errors({
      url: URL_A,
      status: {
        text: "Network error: Connection refused",
        details: "Connection refused",
      },
    }),
  );

  assert.equal(code, 0);
  assert.match(stdout, /::warning::/);
  assert.match(stdout, /Network error/);
});

test("a link the report has no answer for fails, since nothing was checked", () => {
  const { code, stdout } = checkReport({
    excluded_map: {
      "links.txt": [{ url: URL_A, status: { text: "Excluded" } }],
    },
  });

  assert.equal(code, 1);
  assert.match(stdout, /::error::/);
  assert.match(stdout, /no answer/i);
});

test("one recovered link fails the run, and every link is still reported", () => {
  const { code, stdout } = checkReport(
    {
      success_map: {
        "links.txt": [{ url: URL_A, status: { text: "200 OK", code: 200 } }],
      },
      error_map: { "links.txt": [rejected(403, "Forbidden", URL_B)] },
    },
    URL_A,
    URL_B,
  );

  assert.equal(code, 1);
  assert.ok(logsLink(stdout, URL_A));
  assert.ok(logsLink(stdout, URL_B));
});

test("a recovered link listed first does not hide a refused one listed second, or the reverse", () => {
  const maps = {
    success_map: {
      "links.txt": [{ url: URL_A, status: { text: "200 OK", code: 200 } }],
    },
    error_map: { "links.txt": [rejected(403, "Forbidden", URL_B)] },
  };

  assert.equal(checkReport(maps, URL_A, URL_B).code, 1);
  assert.equal(checkReport(maps, URL_B, URL_A).code, 1);
});

test("a report that is missing fails rather than passing the sweep", () => {
  const { code, stderr } = run("check", join(dir, "absent.json"));

  assert.equal(code, 2);
  assert.match(stderr, /Cannot read lychee's report/);
});

test("a report that is not JSON fails rather than passing the sweep", () => {
  const path = join(dir, "broken.json");
  writeFileSync(path, "No output. Check pipeline run");
  const { code, stderr } = run("check", path);

  assert.equal(code, 2);
  assert.match(stderr, /Cannot read lychee's report/);
});

for (const args of [
  ["chekc"],
  ["check"],
  ["check", "--bogus"],
  ["pattern", "x"],
  ["urls", "x"],
]) {
  test(`\`${args.join(" ")}\` is refused rather than passing the sweep`, () => {
    const { code, stderr } = run(...args);

    assert.equal(code, 2);
    assert.match(stderr, /Usage/);
  });
}
