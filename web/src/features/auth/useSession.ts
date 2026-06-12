import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, type components } from '@/api';

export type Session = components['schemas']['MeResponse'];

export const sessionQueryKey = ['session'] as const;

/**
 * The current session, or null when unauthenticated. A 401 is a normal "logged out" state, not an
 * error — it resolves to null so the route guard can redirect without retry churn.
 */
export function useSession(): UseQueryResult<Session | null> {
  return useQuery({
    queryKey: sessionQueryKey,
    queryFn: async (): Promise<Session | null> => {
      const { data, response } = await api.GET('/api/auth/me');
      if (response.status === 401) return null;
      if (!data) throw new Error(`Unexpected /api/auth/me response: ${response.status}`);
      return data;
    },
    retry: false,
    staleTime: 60_000,
  });
}
