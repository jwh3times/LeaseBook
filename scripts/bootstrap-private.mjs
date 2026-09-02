// Restores the optional confidential `private/` companion checkout for authorized maintainers.
//
// The private repository's locator deliberately never appears in public source. It is read at run
// time from the 1Password `LeaseBook Workspace` item (secret reference below), so a fresh clone
// of the public repository can be completed with one command:
//
//   npm run bootstrap:private
//
// The locator may be `owner/name` (cloned with `gh repo clone`, reusing the GitHub CLI login) or a
// credential-free GitHub HTTPS/SSH URL (cloned with `git clone`). Nothing here prints the locator,
// a token, or any resolved secret; the public checkout keeps ignoring `/private/` either way.
//
// Options:
//   --url <locator>                          skip 1Password and use this locator
//   --op-reference <op://...>                read the locator from a different secret reference
//   --service-account-reference <op://...>   if the current identity cannot read the locator, read a
//                                            service-account token from this reference and retry
//                                            with it in a process-scoped OP_SERVICE_ACCOUNT_TOKEN
//                                            (also LEASEBOOK_OP_SERVICE_ACCOUNT_REFERENCE)
import { existsSync, readdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const privateRoot = join(repositoryRoot, "private");
const defaultReference =
  "op://Leasebook/LeaseBook Workspace/private_repository";

let explicitLocator = null;
let reference = defaultReference;
let serviceAccountReference =
  process.env.LEASEBOOK_OP_SERVICE_ACCOUNT_REFERENCE ?? null;
for (let index = 2; index < process.argv.length; index += 1) {
  const argument = process.argv[index];
  if (
    !["--url", "--op-reference", "--service-account-reference"].includes(
      argument,
    )
  ) {
    throw new Error(`Unknown argument: ${argument}`);
  }
  const value = process.argv[index + 1];
  if (!value) throw new Error(`${argument} requires a value.`);
  if (argument === "--url") explicitLocator = value;
  else if (argument === "--op-reference") reference = value;
  else serviceAccountReference = value;
  index += 1;
}

if (existsSync(join(privateRoot, ".git"))) {
  console.log(
    "The private companion checkout is already installed at private/.",
  );
  process.exit(0);
}
if (existsSync(privateRoot) && readdirSync(privateRoot).length > 0) {
  throw new Error(
    "Refusing to overwrite the non-empty private/ directory because it is not a Git checkout.",
  );
}

function opRead(secretReference, env = process.env) {
  const result = spawnSync("op", ["read", secretReference], {
    encoding: "utf8",
    env,
    windowsHide: true,
  });
  return result.status === 0 ? result.stdout.trim() : "";
}

let locator = explicitLocator;
if (!locator) {
  locator = opRead(reference);
  if (!locator && serviceAccountReference) {
    let serviceToken = opRead(serviceAccountReference);
    if (serviceToken) {
      const scopedEnvironment = {
        ...process.env,
        OP_SERVICE_ACCOUNT_TOKEN: serviceToken,
      };
      locator = opRead(reference, scopedEnvironment);
      scopedEnvironment.OP_SERVICE_ACCOUNT_TOKEN = "";
      serviceToken = "";
    }
  }
  if (!locator) {
    throw new Error(
      "Could not read the private repository locator with the current 1Password identity" +
        " or the optional service-account reference. Sign in to 1Password (`op vault list`) and retry.",
    );
  }
}
if (/\r|\n/u.test(locator))
  throw new Error("The private repository locator must be a single line.");

const ownerName = /^[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+$/u;
const sshUrl = /^git@github\.com:[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+(?:\.git)?$/u;
let cloneCommand;
if (ownerName.test(locator)) {
  cloneCommand = ["gh", ["repo", "clone", locator, privateRoot]];
} else if (/^https?:\/\//iu.test(locator)) {
  const parsed = new URL(locator);
  if (
    parsed.hostname.toLowerCase() !== "github.com" ||
    parsed.username ||
    parsed.password
  ) {
    throw new Error(
      "An HTTPS locator must target github.com and contain no embedded credential.",
    );
  }
  cloneCommand = ["git", ["clone", locator, privateRoot]];
} else if (sshUrl.test(locator)) {
  cloneCommand = ["git", ["clone", locator, privateRoot]];
} else {
  throw new Error(
    "The private repository locator must be `owner/name` or a credential-free GitHub HTTPS/SSH URL.",
  );
}

// stdio is inherited so clone progress is visible; git/gh print the locator themselves only in
// their normal "Cloning into" line, which names the destination path, not the remote.
const clone = spawnSync(cloneCommand[0], cloneCommand[1], {
  stdio: "inherit",
  windowsHide: true,
});
if (clone.status !== 0 || !existsSync(join(privateRoot, ".git"))) {
  throw new Error("The private companion clone did not complete successfully.");
}
console.log("Private companion checkout installed at private/.");
