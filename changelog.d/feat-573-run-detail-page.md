### Added

- **Open a confirmed run and see what it did to each tenant or owner** (#573). Run history counted
  "3 skipped" with no way to find which three, or why. Each run in history now opens its own page,
  from its period, and the panel shown after confirming a run links to it too. The page lists every
  target the run touched: posted, skipped or excluded, the amount, and in plain words why each one
  that did not post was skipped or excluded. Targets that need attention come first, each linked to
  the tenant's ledger or the owner's page; one that can no longer be found is still listed. A filter
  narrows the list to one outcome. When the run posted into a month whose owner statement was
  already issued, the page carries the same warning as the confirm panel.

### Changed

- The run preview and the run page word every reason a run records the same way, from one list. A
  reason the server adds without wording now fails the build instead of reaching a screen as code.
