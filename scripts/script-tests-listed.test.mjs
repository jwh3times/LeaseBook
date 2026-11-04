import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";

/**
 * Continuous integration names each script test file in a workflow step, with no glob, so a new
 * `scripts/*.test.mjs` that no step names never runs there, and a change that breaks it merges with
 * every check green (#692). This guard fails, naming the file, when a test file is named in no
 * workflow and in no `web/package.json` script.
 *
 * It reads names only. It cannot prove that the step or the package script naming a file actually
 * runs in a required job, nor that `node --test` really reaches it.
 */

/** Every `scripts/<name>.test.mjs` a text names, by its file name. */
function namedTests(text) {
  return [...text.matchAll(/scripts\/([\w.-]+\.test\.mjs)/g)].map(
    (match) => match[1],
  );
}

/**
 * A workflow's text without its comments, so that a file only mentioned in one does not count as
 * run. A `#` at the start of a line or after a space opens a comment in YAML and in a `run:` shell
 * script alike. One inside a quoted string is taken for a comment too, which can only make the
 * guard report a file that does run, never pass one that does not.
 */
function withoutComments(workflow) {
  return workflow.replace(/(^|\s)#.*$/gm, "$1");
}

/**
 * The test files that no workflow and no package script names.
 *
 * @param {string[]} testFiles file names under `scripts/`, such as `sync-agents.test.mjs`
 * @param {string[]} workflows the text of each workflow file
 * @param {string[]} packageScripts the command of each `web/package.json` script
 */
function unlistedTests(testFiles, workflows, packageScripts) {
  const listed = new Set(
    [...workflows.map(withoutComments), ...packageScripts].flatMap(namedTests),
  );
  return testFiles.filter((file) => !listed.has(file));
}

describe("which script tests are listed", () => {
  it("does not count a name that appears only in a workflow comment", () => {
    const workflow = [
      "      # scripts/commented.test.mjs is mentioned here and in no step",
      "      - run: node --test scripts/listed.test.mjs # not scripts/trailing.test.mjs",
    ].join("\n");

    assert.deepEqual(
      unlistedTests(
        ["commented.test.mjs", "listed.test.mjs", "trailing.test.mjs"],
        [workflow],
        [],
      ),
      ["commented.test.mjs", "trailing.test.mjs"],
    );
  });

  it("counts a file a web/package.json script names from web/, as docs:policy:test does", () => {
    assert.deepEqual(
      unlistedTests(
        ["check-docs.test.mjs", "unnamed.test.mjs"],
        [],
        ["node --test ../scripts/check-docs.test.mjs"],
      ),
      ["unnamed.test.mjs"],
    );
  });
});

describe("every script test runs somewhere", () => {
  it("names every scripts/*.test.mjs in a workflow or a web/package.json script", () => {
    const repoRoot = path.resolve(import.meta.dirname, "..");
    const testFiles = readdirSync(import.meta.dirname).filter((name) =>
      name.endsWith(".test.mjs"),
    );
    const workflowsDir = path.join(repoRoot, ".github", "workflows");
    const workflows = readdirSync(workflowsDir)
      .filter((name) => /\.ya?ml$/.test(name))
      .map((name) => readFileSync(path.join(workflowsDir, name), "utf8"));
    const { scripts } = JSON.parse(
      readFileSync(path.join(repoRoot, "web", "package.json"), "utf8"),
    );

    assert.deepEqual(
      unlistedTests(testFiles, workflows, Object.values(scripts)),
      [],
      "each of these script test files runs in no CI step: name it in a workflow step",
    );
  });
});
