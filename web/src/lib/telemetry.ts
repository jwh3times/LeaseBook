import { postApiTelemetryBudget } from '@/api';

/**
 * Click-budget telemetry (§C.8 / P47): records how many interactions a budgeted task took. Posts to
 * the host, which emits a tags-only OTel `ux.budget` event — never amounts or PII. Fire-and-forget
 * so a failure never disrupts the UI; `met` is whether the task came in at/under its budget.
 *
 * This goes through the generated client rather than a raw `fetch` so it inherits one copy of
 * transport policy — credentials and the XSRF header. It previously hand-rolled both, justified by a
 * comment claiming the budget endpoint was host-owned and therefore ungenerated; the endpoint is in
 * `sdk.gen.ts` and always was. A second copy of *security* policy is the hazard here, not the
 * duplication: it works today and would fail silently forever if the XSRF contract ever moved.
 */
export function trackInteraction(task: string, interactions: number, met?: boolean): void {
  void postApiTelemetryBudget({
    body: { task, interactions, met: met ?? null },
  }).catch(() => {
    /* telemetry is best-effort; swallow */
  });
}

/**
 * React Router navigation state carrying the interactions a *previous* surface already spent on its
 * way to the destination (#408). The ⌘K palette spends two getting to "Record payment → X"; without
 * this the ledger composer restarts at one and the budget sample under-reports the real flow.
 *
 * It rides in history state rather than the URL deliberately: it describes how the operator arrived,
 * not what they are looking at, so a refresh or a bookmark carries none of it and the destination
 * falls back to counting only its own interactions.
 */
export interface SpentInteractionsState {
  spentInteractions: number;
}

export function spentInteractions(count: number): SpentInteractionsState {
  return { spentInteractions: count };
}

/**
 * Reads that count back out. History state is whatever the browser replays, so anything that is not
 * a positive whole number reads as absent — "nobody told us" — rather than seeding a nonsense count.
 */
export function readSpentInteractions(state: unknown): number | undefined {
  if (typeof state !== 'object' || state === null) return undefined;
  const count = (state as Partial<SpentInteractionsState>).spentInteractions;
  return typeof count === 'number' && Number.isInteger(count) && count > 0 ? count : undefined;
}
