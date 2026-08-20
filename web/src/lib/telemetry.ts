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
