<!-- Generated from settings/docs.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: docs

`docs/agents/docs.md` — the repository's documents, what each one owns, when it changes, and how
they are checked against the code. Each setting below is a `##` heading in that file.

## Doc map

- **Read by:** docs-updater, end-session
- **Value:** bulleted list, one document (public or in the companion) or document group (a glob
  such as `docs/adr/*.md`) per item:
  - what it owns — the facts it is the one home for, and its audience;
  - **when** to update it — the kinds of change, with the source paths that signal them where that
    helps (e.g. "a new controller under `src/Api/Controllers/`: the controllers table");
  - optionally one marker:
    - `frozen` — read it, never update it (an archive, design history, an accepted ADR);
    - `append-only` — add entries, never change or delete one (captured API responses);
    - `amend-only` — dated records corrected by an amendment note, never rewritten;
    - `owner-only` — the user edits it; report drift instead (`LICENSE`, open product questions);
    - `ship-owned` — written by `ship` (`CHANGELOG.md` sections, the version file); outside `ship`,
      add only `Unreleased` entries;
    - `pinned: <the pin>` — another check fingerprints it (a hash in a test fixture), so an edit must
      update that pin in the same change.

  May add a bulleted **Never in public docs** list of the kinds of content that stay in the companion
  (live resource names, prices, exploit analysis).
- **Discover:** `.claude/agents/docs-updater.md` (its documents table and its "what triggers what
  update" matrix, turned around so each document carries its triggers), a `docs-updater` skill,
  the docs index (`docs/README.md`, `private/README.md`), the docs map in `AGENTS.md`, and existing
  `end-session` routing tables. A script that parses the docs-updater file for this list (a docs
  check) must be pointed at this file instead: name it when proposing. Search scripts and test
  fixtures for document paths beside a hash (`sha256`, `Sha256`) to find `pinned` documents.
  Propose each skill `skills-lock.json` lists as `frozen`, since a reinstall overwrites it, unless
  the repository forks and edits it in place (an ADR or `AGENTS.md` says so).

## Drift probes

- **Read by:** docs-updater
- **Value:** `none`, or bulleted list of facts the docs state and where the truth lives: a glob, a
  search pattern in a file, a config file, or a command (e.g. "controllers: `src/Api/Controllers/*.cs`";
  "required checks: `gh api repos/<owner>/<name>/rules/branches/main`").
- **Discover:** the "how to detect drift" section of `.claude/agents/docs-updater.md`.

## Docs validation

- **Read by:** docs-updater, end-session
- **Value:** `none`, or commands that check the docs (a docs test, a formatter check, a companion
  docs gate), each with the folder it runs from.
- **Default:** `node scripts/check-docs.mjs`, the canonical script from `jwh3times/skills`: local
  links and anchors resolve, and the `Doc map` lists every document and no missing path.
- **Discover:** `test:docs`- and `docs:check`-style scripts in `package.json`, docs checks in CI
  workflows, scripts under `private/scripts/`, and what `.claude/agents/docs-updater.md` runs. Write
  the heading only when a repository adds checks of its own, listing the default first.
