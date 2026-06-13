import type { SearchResult } from '@/lib/search';

const KEY = 'leasebook.palette.recent';
const MAX = 6;

/** A small most-recently-used list of jumped-to entities, shown when the palette query is empty. */
export function getRecent(): SearchResult[] {
  try {
    const raw = localStorage.getItem(KEY);
    return raw ? (JSON.parse(raw) as SearchResult[]) : [];
  } catch {
    return [];
  }
}

export function pushRecent(result: SearchResult): void {
  try {
    const next = [result, ...getRecent().filter((r) => !(r.type === result.type && r.id === result.id))].slice(0, MAX);
    localStorage.setItem(KEY, JSON.stringify(next));
  } catch {
    /* storage may be unavailable — recents are a nicety, not load-bearing */
  }
}
