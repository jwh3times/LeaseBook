# Changelog fragments

Each branch writes its changelog entries here, in one file named for the branch with each `/`
replaced by `-` (`feat/812-late-fee-preview` → `feat-812-late-fee-preview.md`). `/ship` writes it;
never edit `CHANGELOG.md` on a branch.

A fragment holds only category headings and their bullets, with no version or date, using Keep a
Changelog's categories:

```markdown
### Added

- Staff can preview a late fee before the run posts it.

### Fixed

- The owner statement no longer drops a reversed disbursement from the period total.
```

Name a file in backticks rather than linking it: a relative link resolves from `changelog.d/` on
the branch but from the repository root once stamped into `CHANGELOG.md`, so it breaks in one place
or the other.

After the merge, `.github/workflows/version.yml` runs `scripts/stamp-changelog.mjs`, which moves
the fragments that merge added into its dated section of `CHANGELOG.md`, deletes them, and tags
the merge (ADR-058). A merge with no fragment gets one `### Changed` bullet from its PR title. A
fragment already on `main` belongs to the merge that added it: never edit or delete it, and the
changelog check rejects a branch that does. This README is not a fragment.
