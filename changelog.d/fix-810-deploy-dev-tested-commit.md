### Fixed

- **`deploy-dev` now deploys the commit CI tested** (#810). A `workflow_run` run's `github.sha` is
  `main`'s tip when it starts, so the dev deploy that follows CI on a merge would have built and
  tagged `version.yml`'s `[skip ci]` stamp commit (the run after `v0.39.6` started on it), or a
  later merge whose CI had not finished. It now checks out and tags the image with
  `github.event.workflow_run.head_sha`; a manual dispatch still deploys its ref's tip. The workflow
  stays skipped until the dev environment is configured, so nothing was deployed. ADR-058,
  `docs/agents/merging.md` and the Azure infrastructure guide are corrected to match.
