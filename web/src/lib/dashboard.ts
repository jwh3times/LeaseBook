import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { getApiDashboard, type DashboardResponse } from '@/api';

export type { DashboardResponse };

export function useDashboard(): UseQueryResult<DashboardResponse> {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: async () => {
      const { data, error } = await getApiDashboard();
      if (error || !data) throw new Error('Failed to load the dashboard');
      return data;
    },
  });
}
