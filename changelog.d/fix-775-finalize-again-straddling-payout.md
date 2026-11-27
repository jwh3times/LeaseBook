### Fixed

- **Finalize again no longer shows a $0.00 difference that the server then refuses** (#775). In
  a reopened month's Finalize again view, a processor payout with one line on the month's last day
  and another after it could be ticked. Its in-month line counted toward the difference, so
  Finalize could be enabled at $0.00, and finalize then refused the payout because one of its
  lines falls after the month end. The lines read and finalize now use one check. The view shows
  such a line unticked and disabled, with the reason it belongs to a later month, and never counts
  or sends it. That includes a line ticked before a refusal and marked when the lines are read
  again. The lines read (`GET /api/accounting/reconciliations/{id}/lines`) adds an optional
  `notAssignableReason` to each line of a payout that finalize would refuse.
