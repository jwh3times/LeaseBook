<!-- Generated from settings/dependabot.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: dependabot

`docs/agents/dependabot.md` — what reviewing a Dependabot pull request takes in this repository:
which bumps need a review though they are minor, how to reproduce CI's install, where the
dependencies are used, the facts about this stack a review must not get wrong, and the known fix
for each kind of bump that has broken before. `dependabot` reads it on every run, in a session and
in the scheduled review, so the file is the one place this repository's Dependabot knowledge lives.
Each setting below is a `##` heading in that file; prose around them is read too.

A fact in this file applies to this repository only. A review never carries one repository's facts
into another's.

## Label

- **Read by:** dependabot
- **Value:** the name in the closing line of every review comment,
  `🤖 Dependabot PR review by the <Label> routine`, which is how a later run finds an earlier
  verdict. Changing it orphans the verdicts already posted, so keep the one in use.
- **Default:** the repository's name.
- **Discover:** an existing review comment on a Dependabot pull request
  (`gh api "repos/<owner>/<repo>/issues/<n>/comments" --jq '.[].body'`, the text after "by the"
  in its last line). Write the heading only when it differs from the repository's name.

## Review triggers

- **Read by:** dependabot
- **Value:** the bumps that are reviewed in full even when no version crosses a major: packages
  and images by name or pattern, with the kind of bump that triggers it (any bump, a digest, a
  `major.minor` change), and why each is risky here.
- **Default:** none: only a major bump (the leading version segment increases, for actions and
  image tags too) is reviewed in full.
- **Discover:** the packages the `Playbook` and the repository's merging guide name as having
  broken on a minor or a digest bump (framework packages pinned with an SDK, a database image, a
  runtime image).

## Resolution

- **Read by:** dependabot
- **Value:** the commands that reproduce CI's dependency resolution for each manifest Dependabot
  updates, run from the directory named with each: the toolchain versions first (Node, the package
  manager's own version, Python, the .NET SDK, Go), then the strict, lockfile-faithful install CI
  runs (`npm ci`, `pnpm install --frozen-lockfile`, `uv sync --frozen`, `dotnet restore`,
  `go build ./...`).
- **Default:** for each lockfile, its ecosystem's strict install in that lockfile's directory, under
  the toolchain `.nvmrc`, `.python-version`, `global.json` or `go.mod` names.
- **Discover:** the install steps and `setup-*` versions in `.github/workflows/`, and the
  directories in `.github/dependabot.yml`. Write the heading when CI pins a toolchain no file
  names, installs in several directories, or uses a package manager whose version matters (pnpm).

## Where to look

- **Read by:** dependabot
- **Value:** where this repository's use of a dependency shows: the workflow files whose `with:`
  inputs an action bump can break, the config files a framework or tool major can break, the
  Dockerfiles and compose files that pin images, and the source directories that import libraries.
- **Default:** `.github/workflows/`, the manifests Dependabot changed, their tool configs beside
  them, and every Dockerfile and compose file.
- **Discover:** the config files at the root and in each manifest's directory, and the
  Dockerfiles. Write the heading when a use lives somewhere else (a second pin of an image, a
  generated file checked against a tool's version).

## Grounding facts

- **Read by:** dependabot
- **Value:** the facts about this repository's stack that decide whether an upstream breaking
  change reaches it, each one checkable in a named file: what it is and how it deploys, what it
  does not have (a server runtime, a database image, Docker), the runtimes CI uses, and the cases
  that are always a break here (a database major, a framework major without its SDK).
- **Default:** none: the review reads the repository for each fact it needs, and states none it
  has not read.
- **Discover:** `AGENTS.md`, the architecture document, the build config, the workflows. Propose
  the facts a review would otherwise get wrong, each with the file that shows it.

## Playbook

- **Read by:** dependabot
- **Value:** for each kind of bump that has needed more than a green CI: the gates to run for it
  (commands, with what each proves), and its known fix when it goes red for a reason that is not
  the code (a pin to move with it, a generated file to regenerate, a word list to extend), as the
  command that fixes it and the check it turns green.
- **Default:** none: the gates are the `Resolution` install, then the build and the tests CI runs.
- **Discover:** the repository's merging guide's section on dependency pull requests, earlier
  review comments that named a fix, and the pin-sync scripts in `package.json`. Record a fix only
  once it has worked.
