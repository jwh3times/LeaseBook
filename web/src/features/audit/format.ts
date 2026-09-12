/**
 * An audit timestamp, rendered in UTC.
 *
 * Deliberately not local time. The period filter is bounded in UTC on the server — `from` and `to`
 * become `[from 00:00Z, to+1 00:00Z)` — so a locally-rendered column puts the screen in disagreement
 * with its own filter: a reviewer in ET who asks for 2026-09-11 gets rows whose first entries display
 * as "9/10/2026, 8:00 PM" and misses that day's evening. One clock for the filter, the column and the
 * CSV export (whose header already says UTC) is the version that can be read without arithmetic.
 */
export function formatWhen(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return iso;
  return `${date.toISOString().slice(0, 19).replace('T', ' ')} UTC`;
}
