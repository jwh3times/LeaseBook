import assert from "node:assert/strict";
import { existsSync, readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { spawnPlaywright } from "./playwright-cli.mjs";

const scriptsRoot = import.meta.dirname;
const playwrightPackage = path.join(
  scriptsRoot,
  "..",
  "web",
  "node_modules",
  "@playwright",
  "test",
  "package.json",
);

describe("scripts stay runnable on a Windows checkout", () => {
  // Continuous integration runs on Linux, where both of these work, so only the source can show them.
  const sources = readdirSync(scriptsRoot)
    .filter(
      (name) =>
        name.endsWith(".mjs") && name !== path.basename(import.meta.filename),
    )
    .map((name) => ({
      name,
      text: readFileSync(path.join(scriptsRoot, name), "utf8"),
    }));

  const offenders = (pattern) =>
    sources.filter(({ text }) => pattern.test(text)).map(({ name }) => name);

  it("spawns no npm-installed command by name: on Windows each is a .cmd shim that only a shell can run", () => {
    assert.deepEqual(
      offenders(/\b(?:spawn|spawnSync|execFile|execFileSync)\(\s*"np[mx]"/),
      [],
    );
  });

  it("links no directory with a symlink, which Windows refuses without developer mode; a junction needs no privilege", () => {
    assert.deepEqual(offenders(/\bsymlinkSync\([^;]*?"dir"\s*,?\s*\)/), []);
  });
});

describe("spawnPlaywright", () => {
  const skip = existsSync(playwrightPackage)
    ? false
    : "web/node_modules is not installed";

  it("runs the installed Playwright", { skip }, () => {
    const result = spawnPlaywright(["--version"], { encoding: "utf8" });
    const { version } = JSON.parse(readFileSync(playwrightPackage, "utf8"));
    assert.equal(result.status, 0, result.stderr);
    assert.equal(result.stdout.trim(), `Version ${version}`);
  });
});
