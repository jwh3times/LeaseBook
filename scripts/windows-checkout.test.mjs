import assert from "node:assert/strict";
import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { describe, it } from "node:test";

/**
 * Two things that work on Linux, where continuous integration runs, and fail on a Windows checkout
 * (#664). Only the source can show them there, so these read it, and they cannot see a command name
 * held in a variable.
 */
describe("scripts stay runnable on a Windows checkout", () => {
  const scriptsRoot = import.meta.dirname;
  const sources = readdirSync(scriptsRoot)
    .filter(
      (name) =>
        name.endsWith(".mjs") && name !== path.basename(import.meta.filename),
    )
    .map((name) => ({
      name,
      text: readFileSync(path.join(scriptsRoot, name), "utf8"),
    }));

  it("runs no npm-installed command by name: on Windows each is a .cmd shim that only a shell can run", () => {
    const offenders = sources
      .filter(({ text }) => /\b\w+\(\s*"np[mx]"/.test(text))
      .map(({ name }) => name);
    assert.deepEqual(offenders, []);
  });

  it("links a directory only with a junction: Windows refuses a symlink without developer mode, and an untyped link is detected as one", () => {
    const offenders = sources
      .filter(({ text }) =>
        (text.match(/\bsymlinkSync\([^;]*;/g) ?? []).some(
          (call) => !call.includes('"junction"'),
        ),
      )
      .map(({ name }) => name);
    assert.deepEqual(offenders, []);
  });
});
