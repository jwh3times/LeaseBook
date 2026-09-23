import { useRef, useState } from 'react';
import { trackInteraction, type BudgetTask } from '@/lib/telemetry';
import { currentPeriod } from './periodUtils';
import { CAPABILITIES_CHANGED, useConfirmRun, useRunPreview } from './useRuns';
import type { RunError, RunResultSpaResponse, RunType } from './useRuns';

/**
 * `selective` — the operator ticks which targets to post (late fees, disbursements).
 * `all-eligible` — the run is all-or-nothing for the period (rent); there is nothing to choose.
 */
export type RunMode = 'selective' | 'all-eligible';

const TASK: Record<RunType, BudgetTask> = {
  rent: 'rent-run-confirm',
  latefee: 'latefee-run-confirm',
  disbursement: 'disbursement-run-confirm',
};

const NONE: ReadonlySet<string> = new Set();

/**
 * The bulk-run flow every run screen shares: period, preview, selection, confirm, the outcome, and
 * the interaction count. Screens own only their layout and how a row renders.
 *
 * **A tick and an error each describe one row set** — the preview the operator was looking at. Both
 * are stored against that row set's key and simply stop applying when a different one is on screen,
 * whatever replaced it: a period change, a retry, the re-preview after a capability conflict, or any
 * future invalidation. Carrying a tick onto rows whose amounts may have moved would turn "I approved
 * these amounts" into "I approved whatever is there now". Deriving this rather than resetting it in
 * an effect keeps it correct under StrictMode and on the render the new rows first appear.
 *
 * **A capability conflict is not an error to the operator.** The server refused because the amounts
 * on screen are no longer what would post; the preview refetches (see `useConfirmRun`) and the flow
 * reports `conflicted` so the screen can say why the selection emptied, until the next confirm or
 * period change.
 *
 * **The interaction count is measured**, and reported only for a run that posted, with no pass/fail
 * claim: no product document sets a click budget for bulk runs. Period changes, each toggle,
 * select-all (as one) and Confirm count; a conflict re-preview does not restart the count, because
 * the ticks it took were real effort.
 */
export function useRunFlow(type: RunType, mode: RunMode) {
  const [period, setPeriodState] = useState(currentPeriod);
  const [selection, setSelection] = useState<{ key: string; ids: ReadonlySet<string> }>({
    key: '',
    ids: NONE,
  });
  const [failure, setFailure] = useState<{ key: string; error: RunError } | null>(null);
  const [conflicted, setConflicted] = useState(false);
  const [result, setResult] = useState<RunResultSpaResponse | null>(null);
  const interactions = useRef(0);

  const preview = useRunPreview(type, period.year, period.month);
  const confirmMutation = useConfirmRun(type);

  const rowSetKey = preview.data
    ? `${type}:${period.year}:${period.month}:${preview.dataUpdatedAt}`
    : '';
  const eligibleIds =
    preview.data?.rows.filter((r) => !r.excludedReason && !r.alreadyDone).map((r) => r.targetId) ??
    [];

  const selected: ReadonlySet<string> =
    mode === 'all-eligible'
      ? new Set(eligibleIds)
      : rowSetKey !== '' && selection.key === rowSetKey
        ? selection.ids
        : NONE;
  const error = failure && failure.key === rowSetKey ? failure.error : null;

  const clearWork = () => {
    setSelection({ key: '', ids: NONE });
    setFailure(null);
  };

  const setPeriod = (year: number, month: number) => {
    interactions.current += 1;
    // A cached period can come back with the same row-set key, so its old ticks and error would
    // otherwise reappear with it.
    clearWork();
    setConflicted(false);
    setPeriodState({ year, month });
  };

  const toggle = (id: string) => {
    if (mode === 'all-eligible') return;
    interactions.current += 1;
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    setSelection({ key: rowSetKey, ids: next });
  };

  const toggleAll = () => {
    if (mode === 'all-eligible') return;
    interactions.current += 1;
    const allSelected = eligibleIds.length > 0 && eligibleIds.every((id) => selected.has(id));
    setSelection({ key: rowSetKey, ids: allSelected ? NONE : new Set(eligibleIds) });
  };

  const retry = () => {
    clearWork();
    void preview.refetch();
  };

  const confirm = () => {
    if (!preview.data) return;
    interactions.current += 1;
    const confirmedAgainst = rowSetKey;
    setFailure(null);
    setConflicted(false);
    confirmMutation.mutate(
      {
        year: period.year,
        month: period.month,
        selectedTargetIds: Array.from(selected),
        // Echoed verbatim from the preview whose amounts are on screen — the server compares it.
        capabilitiesVersion: preview.data.capabilitiesVersion,
      },
      {
        onSuccess: (data) => {
          trackInteraction(TASK[type], interactions.current, undefined);
          interactions.current = 0;
          setResult(data);
        },
        onError: (err) => {
          if (err.code === CAPABILITIES_CHANGED) setConflicted(true);
          else setFailure({ key: confirmedAgainst, error: err });
        },
      },
    );
  };

  const done = () => {
    setResult(null);
    clearWork();
  };

  return {
    period,
    setPeriod,
    preview,
    eligibleIds,
    selected,
    toggle,
    toggleAll,
    retry,
    confirm,
    isConfirming: confirmMutation.isPending,
    error,
    conflicted,
    result,
    done,
  };
}
