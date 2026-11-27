### Changed

- **The run-detail API says what a confirmed run did to each target** (#573, step 1 of 2).
  `GET /api/operations/runs/{id}` now returns every target's run outcome (posted, skipped or
  excluded), the reason for each one that did not post, and who the target is today: the tenant and
  unit for a lease, the owner for an owner. A lease or owner that can no longer be found is still
  listed, without a name. Targets come back excluded first, then skipped, then posted, by name
  within each. Nothing calls it yet; the run page that shows it follows in the next step.
- The fourteen reasons a run records for a target it did not post are one published list, and each
  reason decides whether that target is skipped or excluded. The API contract publishes them, the
  run outcome and the target kind as enums, on the run detail and on the run preview's excluded
  reason. What runs post and what they store are unchanged.
