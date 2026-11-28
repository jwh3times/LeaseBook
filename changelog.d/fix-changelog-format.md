### Fixed

- `npm run docs:check` passes on `main` again. The v0.41.5 release copied an unformatted changelog
  entry into `CHANGELOG.md`, which failed the **Web (lint + test + build)** check on every branch
  that caught up with `main`.

### Changed

- `npm run docs:format` and the docs gate now also format and check the `changelog.d/` fragments,
  so an unformatted entry fails on its own pull request instead of after the release stamps it.
