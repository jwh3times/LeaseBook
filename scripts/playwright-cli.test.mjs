import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";
import { spawnPlaywright } from "./playwright-cli.mjs";

const playwrightPackage = path.join(
  import.meta.dirname,
  "..",
  "web",
  "node_modules",
  "@playwright",
  "test",
  "package.json",
);

describe("spawnPlaywright", () => {
  it(
    "runs the installed Playwright",
    {
      skip:
        !existsSync(playwrightPackage) && "web dependencies are not installed",
    },
    () => {
      const result = spawnPlaywright(["--version"], { encoding: "utf8" });
      const { version } = JSON.parse(readFileSync(playwrightPackage, "utf8"));
      assert.equal(result.status, 0, result.stderr);
      assert.equal(result.stdout.trim(), `Version ${version}`);
    },
  );
});
