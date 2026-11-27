### Fixed

- **The dashboard shows a reconciliation's cleared lines as soon as it finalizes** (#774).
  Finalizing from Banking's reconcile-in-place screen cleared the ticked lines, but a dashboard read
  in the 30 seconds before kept showing the old uncleared count and trust figures until its cache
  expired. Every Banking write that refreshes an account now refreshes the dashboard too: the
  in-place finalize, a statement import's confirm, and every bank fee, transfer, fee sweep and
  owner contribution.
