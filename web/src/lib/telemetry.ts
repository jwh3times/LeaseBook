function readCookie(name: string): string | null {
  const match = document.cookie.match(new RegExp(`(?:^|; )${name}=([^;]*)`));
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

/**
 * Click-budget telemetry (§C.8 / P47): records how many interactions a budgeted task took. Posts to the
 * host, which emits a tags-only OTel `ux.budget` event — never amounts or PII. Fire-and-forget over a
 * raw fetch (the budget endpoint is host-owned, WP-10) so a failure never disrupts the UI; `met` is
 * whether the task came in at/under its interaction budget.
 */
export function trackInteraction(task: string, interactions: number, met?: boolean): void {
  const xsrf = readCookie('XSRF-TOKEN');
  void fetch(`${window.location.origin}/api/telemetry/budget`, {
    method: 'POST',
    credentials: 'include',
    headers: {
      'Content-Type': 'application/json',
      ...(xsrf ? { 'X-XSRF-TOKEN': xsrf } : {}),
    },
    body: JSON.stringify({ task, interactions, met: met ?? null }),
  }).catch(() => {
    /* telemetry is best-effort; swallow */
  });
}
