# Reading a dependency resolution

The `dependabot` skill's step 4 reproduces CI's install for a pull request's head and reads the
result here. Source-level reasoning cannot see a resolution failure: it happens before anything
compiles, and it is the failure that once produced "safe to merge" verdicts on PRs that could not
install. Read the signal; never infer it.

Run the strict install CI runs, never a looser one: `npm install <pkg>@<version>` overrides a
conflicting peer with a warning and hands back a false green.

## Toolchain first

Use the toolchain the `Resolution` setting names, or the one the repository pins (`.nvmrc`,
`.python-version`, `global.json`, `go.mod`), never the machine's default. With nvm:
`nvm install "$version" && nvm use "$version"`. Install pnpm at the version named, under that Node
(`npm install -g "pnpm@$pnpm_version"`; current Node majors do not bundle corepack), and confirm it
with `pnpm --version`. Report the versions actually run. A toolchain that cannot be provided is
said so, and CI's result is the evidence instead.

## npm

The failure is `ERESOLVE`, and it turns on peer dependencies. Read the declared ranges out of the
lockfile, from the directory holding that `package-lock.json`:

```bash
node -e "const l=require('./package-lock.json'); for (const k of Object.keys(l.packages)) { const e=l.packages[k]; if (e.peerDependencies && e.peerDependencies[process.argv[1]]) console.log(k, e.version, JSON.stringify(e.peerDependencies[process.argv[1]]), JSON.stringify(e.peerDependenciesMeta?.[process.argv[1]] ?? 'HARD')); }" "$package"
```

- A peer marked `"optional": true` in `peerDependenciesMeta` is not enforced by npm. It can neither
  cause nor cure an install failure, and is never evidence that a bump is expected, blessed or safe.
- Any other peer is hard: `npm ci` refuses a version outside its range. One hard peer anywhere in
  the tree blocks the bump, however green the type-check. Before calling a major safe, list every
  package that hard-peers on it and confirm the new version satisfies each. When one still
  hard-peers on the old major and has no wider release, the bump is not takeable yet: name the
  blocker and its range.
- "The types still compile" answers compatibility, not resolution. Only `npm ci` answers whether
  the graph resolves.

## pnpm

- `pnpm install --frozen-lockfile` failing with `ERR_PNPM_OUTDATED_LOCKFILE` means the lockfile
  does not match the manifests. That is dispositive.
- An unmet peer does not fail the install. With pnpm 10's defaults a hard peer out of range prints
  `✕ unmet peer <pkg>@<range>: found <version>` and exits 0, so a green install, and a green CI,
  say nothing about peers. Read the install output for those lines.
- Before citing one, run the same install on the default branch (`git checkout --detach
  origin/main`, install, then back to the PR head). Only a line new on the PR head is evidence
  about this PR.
- Confirm the peer is not optional in the declaring package's own `package.json`, under
  `node_modules/.pnpm/<name>@<version>/node_modules/<name>/`.
- A new unmet hard peer naming the bumped package is a declared incompatibility: NEEDS EFFORT,
  quoting the line and the declaring package's range, saying that pnpm installs it with a warning
  rather than refusing it. Never VERIFIED LOW-RISK, however green the build.

## uv (Python)

`uv lock --check` fails when `uv.lock` does not match `pyproject.toml`; `uv sync --frozen`, which CI
runs, fails when the locked set cannot install on the pinned Python (no wheel for that version).
Both are dispositive. uv runs the Python `.python-version` pins, never the system one. Read the
constraints out of `pyproject.toml` and `uv.lock`.

## NuGet (.NET)

The blocking restore errors are `NU1605` (a package downgrade), `NU1107` (a version conflict between
transitive constraints) and `NU1608` (a resolved version outside a dependency's constraint). A
framework or runtime major (EF Core, ASP.NET Core, Npgsql, `Microsoft.*`) moves together with the
SDK in `global.json` and the `mcr.microsoft.com/dotnet` image tags: a lone bump that leaves those on
the old major fails restore or build. Read the constraint out of the `.csproj` or
`Directory.Packages.props`. With `TreatWarningsAsErrors`, an obsoleted API is a build error.

## Go modules

Go uses minimal version selection, so a `go.mod` or `go.sum` the bump leaves inconsistent fails
`go build ./...` or `go mod verify` outright: dispositive. A module major changes the import path to
a new `/vN` suffix, so the `require` and every import move together; a lone `go.mod` bump does not
build. `go mod tidy` must leave both files clean; a diff there is a signal.

## Actions and images

A GitHub Actions bump or a container image bump changes no lockfile, so nothing resolves locally.
CI green is the build gate, with the release-note evidence of step 5; the verdict says CI was the
gate. Every job here runs on GitHub-hosted `ubuntu-latest` unless the `Grounding facts` say
otherwise, so a minimum-runner-version floor that comes with an action's Node runtime bump is
always met, and is not by itself a break.
