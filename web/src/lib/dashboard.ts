import { useQuery, type UseQueryResult } from '@tanstack/react-query';
import { getApiDashboard, unwrap, type DashboardResponse } from '@/api';

export type { DashboardResponse };

export function useDashboard(): UseQueryResult<DashboardResponse> {
  return useQuery({
    queryKey: ['dashboard'],
    queryFn: () => unwrap(getApiDashboard(), 'Failed to load the dashboard'),
  });
}
