/**
 * The words for every reason a run records for a target it did not post (#573). One map for the run
 * preview and the run page, so what an operator saw before confirming reads the same as what the run
 * recorded.
 *
 * The server publishes the reason codes as an enum in the API contract (`RunReason` there, ADR-019's
 * addendum under §4b), and this record is typed over the generated union: a code added on the server
 * without wording here, or a stale key left here, fails `typecheck` rather than reaching a screen as
 * raw text.
 */
import type { BulkRunItemSpa } from '@/api';

/** A run reason code, as the API contract publishes it. */
export type RunReason = NonNullable<BulkRunItemSpa['reason']>;

export const RUN_REASON_LABELS: Record<RunReason, string> = {
  // Refused when posting.
  duplicate_source_ref: 'Already posted by an earlier run',
  period_locked: 'Bank period locked by a finalized reconciliation',
  period_closed: 'Accounting period closed',
  reserve_floor: 'Would go below the reserve floor',
  // Decided by the run before anything posts.
  already_charged_in_period: 'Rent already charged for this period',
  lease_not_in_schedule: 'Lease not active in this period',
  rent_zero: 'Rent is zero',
  lease_not_delinquent: 'Not delinquent for this period',
  rent_obligation_not_found: 'No open rent charge to attach a late fee to',
  no_policy: 'No late-fee policy',
  before_late_fee_eligibility: 'Not yet eligible for a late fee',
  owner_not_found: 'Owner not found',
  non_positive_equity: 'Non-positive equity',
  below_reserve_floor: 'Below reserve floor',
};

/** The words for a reason code. */
export function runReasonLabel(reason: RunReason): string {
  return RUN_REASON_LABELS[reason];
}
