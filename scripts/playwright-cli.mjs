/**
 * Runs the installed Playwright from web/, with this Node and no shell, for the scripts that need it
 * (#664). Not `npx`: on Windows that is a `.cmd` shim only a shell can run, and a shell would parse
 * these arguments again, and they hold paths and whatever a maintainer passes through.
 */

import { spawnSync } from "node:child_process";
import path from "node:path";
import { fileURLToPath } from "node:url";

const webRoot = path.resolve(
  path.dirname(fileURLToPath(import.meta.url)),
  "..",
  "web",
);
const cli = path.join(webRoot, "node_modules", "@playwright", "test", "cli.js");

export function spawnPlaywright(args, options = {}) {
  return spawnSync(process.execPath, [cli, ...args], {
    cwd: webRoot,
    ...options,
  });
}
