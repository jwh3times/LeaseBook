import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { getApiSearch, type SearchResult } from '@/api';

export type { SearchResult };

/** Cross-entity search for the palette (§C.5). Disabled until the (debounced) query is non-empty. */
export function useSearch(q: string): UseQueryResult<SearchResult[]> {
  return useQuery({
    queryKey: ['search', q],
    queryFn: async () => {
      const { data, error } = await getApiSearch({ query: { q, limit: 20 } });
      if (error || !data) throw new Error('Search failed');
      return data;
    },
    enabled: q.trim().length >= 1,
    staleTime: 10_000,
  });
}
