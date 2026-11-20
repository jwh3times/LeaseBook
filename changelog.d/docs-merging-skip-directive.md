### Changed

- **`docs/agents/merging.md` records the commit-body skip-directive trap.** GitHub skips a pull
  request's push and `pull_request` workflows when a skip directive appears anywhere in its head
  commit's message, the body included, and the required checks then stay pending. The new bullet
  says how to name the stamp commit without the directive, how to recover with an empty commit,
  and why a PR body that quotes it is harmless (the merge commit carries only the PR title).
