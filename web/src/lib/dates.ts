/**
 * The viewer's calendar date for a server instant, as `YYYY-MM-DD` — the format ledgers and
 * registers already show. The server sends instants in UTC, so slicing the string instead gives the
 * UTC date, which an evening in a US time zone has already passed.
 */
export function localDate(instant: string): string {
  const date = new Date(instant);
  if (Number.isNaN(date.getTime())) return instant;
  const pad = (n: number) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}`;
}
