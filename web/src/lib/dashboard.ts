import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { api, type components } from '@/api';

export type DashboardResponse = components['schemas']['DashboardResponse'];

export function useDashboard(): UseQueryResult<DashboardResponse> {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: async () => {
      const { data, error } = await api.GET('/api/dashboard');
      if (error || !data) throw new Error('Failed to load the dashboard');
      return data;
    },
  });
}
