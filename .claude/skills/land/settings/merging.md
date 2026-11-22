<!-- Generated from settings/merging.md in jwh3times/skills by scripts/build-settings.mjs; edit it there. -->
# Topic: merging

`docs/agents/merging.md` — how a pull request becomes a merge in this repository. The file usually
exists already as the repository's own merging guide, its prose holding the reds that are not the
code and the steps that have gone wrong; these settings are added beside that prose, which `land`
reads in full every run. Each setting below is a `##` heading in that file. The file may also
hold `## Hook: after merge`, the steps this repository adds once a merge is released (stripping a
stale label, publishing an advisory's resolution), which `land` runs before the next pull request.

## Guide

- **Read by:** land
- **Value:** the path of the prose `land` reads in full before every run: the repository's account
  of reading CI, its known false reds and their remedies, and what it adds around a merge.
- **Default:** `docs/agents/merging.md`, the file these settings live in.
- **Discover:** the document `AGENTS.md` points to for merging or landing. Write the heading only
  when that is another file.

## Merge command

- **Read by:** land
- **Value:** the command that merges a pull request, with `<n>` where its number goes; or `none`,
  when this repository lands its work some other way, in which case `land` follows the `Guide` for
  the merge itself and does everything around it.
- **Default:** `gh pr merge <n> --merge`
- **Discover:** the `Guide`'s merge command, the merge methods the branch rules allow
  (`gh api repos/<owner>/<repo>/rules/branches/main`, the `pull_request` rule's
  `allowed_merge_methods`), and whether GitHub deletes a merged branch already
  (`gh api repos/<owner>/<repo> --jq .delete_branch_on_merge`). Never `--admin` or `--auto`. Write
  the heading only when the default does not fit, as for `--squash` or a repository that lands
  with its own tool.

## Catch-up

- **Read by:** land
- **Value:** the command that brings a pull request whose branch is behind `main` up to date, with
  `<n>` where its number goes; or `none`, when `main` does not require branches to be up to date.
- **Default:** `gh pr update-branch <n>`, which merges `main` into the branch on GitHub; `none` when
  the branch rules are not strict.
- **Discover:** the `required_status_checks` rule's `strict_required_status_checks_policy`, and
  what the `Guide` says about catching up. Write the heading only when the default does not fit.

## Dependabot catch-up

- **Read by:** land
- **Value:** how a Dependabot pull request that is behind `main` or conflicts is brought up to
  date, as a command with `<n>` (`gh pr comment <n> --body "@dependabot rebase"`,
  `gh pr update-branch <n> --rebase`). A merge commit pushed onto Dependabot's branch, which
  `gh pr update-branch` makes, can stop Dependabot from rebasing it later; repositories differ on
  which cost they take.
- **Default:** the `Catch-up` value.
- **Discover:** the `Guide`'s section on dependency pull requests. Write the heading only when it
  differs from `Catch-up`.

## Release workflow

- **Read by:** land
- **Value:** the workflow file that versions a merge (`version.yml`), whose run for the merge commit
  `land` finds and watches, confirming the tag on the merge; or `none`, when nothing tags a merge.
- **Default:** `version.yml` with `Scheme: per-merge` or `stamp-on-tag`; `none` otherwise.
- **Discover:** the workflow in `.github/workflows/` that runs on a push to `main` and pushes `v*`
  tags (`next-version.mjs` or `stamp-changelog.mjs`). Write the heading only when it is another
  file.

## Deploys

- **Read by:** land
- **Value:** `none`, or a bulleted list of what a merge starts that `land` watches and never runs,
  each one of:
  - a workflow file in backticks (`deploy.yml`), whose run for the merge `land` finds by the merge's
    sha or by its stamp commit's, which is the sha a `workflow_run` run lists;
  - `check: <name>`, a check run on the merge commit that reports a deploy (`check: Cloudflare
    Pages`);
  - a sentence for a service that reports nowhere on GitHub, saying how to confirm the deploy
    without changing anything (a health endpoint's version, a read-only log query).

  A step the release workflow's own run covers, such as its deploy job, needs no entry.
- **Default:** `none`
- **Discover:** workflows in `.github/workflows/` triggered by a push to `main` or by `workflow_run`
  on another workflow that deploy, migrate or publish; deploy checks on recent merge commits
  (`gh api repos/<owner>/<repo>/commits/<sha>/check-runs`); and the `Guide`'s account of what a
  merge deploys.
