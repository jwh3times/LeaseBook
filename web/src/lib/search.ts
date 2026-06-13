import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, type components } from '@/api';

export type SearchResult = components['schemas']['SearchResult'];

/** Cross-entity search for the palette (§C.5). Disabled until the (debounced) query is non-empty. */
export function useSearch(q: string): UseQueryResult<SearchResult[]> {
  return useQuery({
    queryKey: ['search', q],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/search', { params: { query: { q, limit: 20 } } });
      if (error || !data) throw new Error('Search failed');
      return data;
    },
    enabled: q.trim().length >= 1,
    staleTime: 10_000,
  });
}
